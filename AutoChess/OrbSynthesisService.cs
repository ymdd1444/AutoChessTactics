using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;

namespace AutoChessTactics;

/// <summary>
/// 生成球类卡（Defect 故障机器人的“充能球”卡）的合成增强。
///
/// 这些卡的 OnPlay 里“生成几个球”是写死在代码里的（如闪电生成 1 个、冰川生成 2 个），
/// 合成缩放 DynamicVars 只影响伤害/格挡/抽牌等数值，球的数量不会变。
/// 本服务在【卡牌播放完成时】（patch CardModel.OnPlayWrapper 的 postfix）补发额外球：
///   - 固定+1 规则：每升一星多生成 1 个球（闪电 1→2→3、冰川 2→3→4、寒冷=敌人数量+星-1）；
///   - X 费牌（风暴 Tempest）：总球数 = X × 2^(星-1)，即一星 1X、二星 2X、三星 4X。
/// 额外球用 TaskHelper.RunSafely 异步补发（在 Godot 主线程执行，紧跟在打牌动作之后）。
/// </summary>
public static class OrbSynthesisService
{
    /// <summary>球类型。</summary>
    private enum OrbKind
    {
        Lightning,
        Frost,
        Dark,
        Random,
    }

    /// <summary>球类卡的规则。</summary>
    private sealed class OrbRule
    {
        public required OrbKind Kind;

        /// <summary>是否 X 费卡（风暴）：球数 = X × 2^(星-1)。</summary>
        public bool IsXCost;
    }

    /// <summary>球类卡名单（entry -> 规则）。</summary>
    private static readonly Dictionary<string, OrbRule> _orbCards = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ball_lightning"] = new() { Kind = OrbKind.Lightning },
        ["cold_snap"] = new() { Kind = OrbKind.Frost },
        ["coolheaded"] = new() { Kind = OrbKind.Frost },
        ["glacier"] = new() { Kind = OrbKind.Frost },
        ["zap"] = new() { Kind = OrbKind.Lightning },
        ["darkness"] = new() { Kind = OrbKind.Dark },
        ["chill"] = new() { Kind = OrbKind.Frost },
        ["tempest"] = new() { Kind = OrbKind.Lightning, IsXCost = true },
        ["chaos"] = new() { Kind = OrbKind.Random },
    };

    /// <summary>判断卡牌 id 是否属于“生成球类卡”。</summary>
    public static bool IsOrbCard(string? entry)
    {
        return entry != null && _orbCards.ContainsKey(entry);
    }

    /// <summary>所有球类卡的 entry 列表（用于加入合成白名单）。</summary>
    public static IEnumerable<string> OrbCardEntries => _orbCards.Keys;

    /// <summary>
    /// 卡牌播放完成（OnPlayWrapper postfix）后调用：如果是一张已合成的球类卡（星级≥2），
    /// 异步补发额外球。
    /// </summary>
    public static void TryChannelExtra(CardModel card, PlayerChoiceContext choiceContext)
    {
        try
        {
            if (card == null || choiceContext == null)
            {
                return;
            }
            if (!_orbCards.TryGetValue(card.Id.Entry, out OrbRule? rule))
            {
                return;
            }
            // 打牌时拿到的通常是战斗克隆体；显示/效果判断要能回到牌组本体。
            // 这里不能用纯实例级 Get，否则克隆没有被某条路径复制星级时会少发球。
            SynthesisService.RecoverStarFromValuesIfNeeded(card, "orb-play", out _);
            int star = StarTracker.GetForDisplay(card);
            if (star < 2)
            {
                return;
            }
            // 只在战斗进行中补发（防止打牌动作结束/死亡后补球）
            if (!CombatManager.Instance.IsInProgress)
            {
                return;
            }

            int extra = GetExtraOrbCount(card, rule, star);
            if (extra <= 0)
            {
                return;
            }
            TaskHelper.RunSafely(ChannelExtraAsync(card, choiceContext, rule, extra));
        }
        catch (Exception e)
        {
            Log.Debug($"[AutoChessTactics] 补发球失败: {e.Message}");
        }
    }

    /// <summary>计算需要额外补发的球数量。</summary>
    private static int GetExtraOrbCount(CardModel card, OrbRule rule, int star)
    {
        if (rule.IsXCost)
        {
            // 风暴：总球数 = X × 2^(星-1)，额外 = 总 - X（X 从打牌时捕获的 X 值读取）
            int x;
            try
            {
                x = card.ResolveEnergyXValue();
            }
            catch
            {
                return 0;
            }
            int multiplier = 1 << (star - 1); // 2^(星-1)：二星=2、三星=4
            return Math.Max(0, x * multiplier - x);
        }
        // 固定+1：每升一星多 1 个球
        return star - 1;
    }

    /// <summary>异步补发额外球。</summary>
    private static async Task ChannelExtraAsync(CardModel card, PlayerChoiceContext choiceContext, OrbRule rule, int extra)
    {
        Player? player = card.Owner;
        if (player == null)
        {
            return;
        }
        for (int i = 0; i < extra; i++)
        {
            switch (rule.Kind)
            {
                case OrbKind.Lightning:
                    await OrbCmd.Channel<LightningOrb>(choiceContext, player);
                    break;
                case OrbKind.Frost:
                    await OrbCmd.Channel<FrostOrb>(choiceContext, player);
                    break;
                case OrbKind.Dark:
                    await OrbCmd.Channel<DarkOrb>(choiceContext, player);
                    break;
                case OrbKind.Random:
                    await OrbCmd.Channel(choiceContext, OrbModel.GetRandomOrb(player.RunState.Rng.CombatOrbGeneration).ToMutable(), player);
                    break;
            }
        }
    }

    /// <summary>
    /// 补丁：卡牌播放完成后补发额外球。
    /// OnPlayWrapper 是所有手动/自动打牌的统一入口，postfix 在此执行，
    /// 此时卡牌的原始生成球效果已经完成。
    /// </summary>
    [HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
    public static class OnPlayWrapperPatch
    {
        public static void Postfix(CardModel __instance, PlayerChoiceContext choiceContext)
        {
            try
            {
                OrbSynthesisService.TryChannelExtra(__instance, choiceContext);
            }
            catch (Exception e)
            {
                Log.Error($"[AutoChessTactics] OnPlayWrapper postfix 异常: {e}");
            }
        }
    }
}
