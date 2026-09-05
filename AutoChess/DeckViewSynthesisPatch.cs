using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;

namespace AutoChessTactics;

/// <summary>
/// 主界面合成入口。
///
/// 旧实现把按钮放在牌组预览里，然后手动关闭牌组 Capstone、打开选择界面、
/// 选择后再手动打开牌组预览。多个 Overlay/Capstone 连续切换时容易发生竞态，
/// 最终卡死在合成画面。
///
/// 现在改为：
///   - 按钮直接挂在运行时 GlobalUi，始终位于主界面；
///   - 使用只负责选择、不修改牌组的 CardSelectCmd.FromDeckGeneric；
///   - 选择结束后不手动关闭/重新打开任何界面；
///   - 卡牌替换使用 CardCmd.RemoveFromDeck + RunState.CreateCard + CardCmd.Add。
/// </summary>
public static class DeckViewSynthesisPatch
{
    /// <summary>每个运行界面只创建一个合成按钮。</summary>
    private static readonly ConditionalWeakTable<NRun, Button> _buttons = new();

    /// <summary>防止按钮连点导致同时打开两个卡牌选择 Overlay。</summary>
    private static bool _flowRunning;

    /// <summary>NRun 创建后尝试挂载按钮。</summary>
    [HarmonyPatch(typeof(NRun), nameof(NRun.Create))]
    public static class RunCreatePatch
    {
        public static void Postfix(NRun __result)
        {
            TryEnsureButton(__result);
        }
    }

    /// <summary>NRun 场景就绪后再次尝试挂载按钮。</summary>
    [HarmonyPatch(typeof(NRun), "_Ready")]
    public static class RunReadyPatch
    {
        public static void Postfix(NRun __instance)
        {
            TryEnsureButton(__instance);
        }
    }

    /// <summary>GlobalUi 就绪后再尝试一次，覆盖不同版本的节点初始化顺序。</summary>
    [HarmonyPatch(typeof(NGlobalUi), "_Ready")]
    public static class GlobalUiReadyPatch
    {
        public static void Postfix(NGlobalUi __instance)
        {
            NRun? run = NRun.Instance;
            if (run != null)
            {
                TryEnsureButton(run);
            }
        }
    }

    /// <summary>
    /// 创建主界面按钮。GlobalUi 尚未完成时，稍后自动重试，
    /// 不依赖牌组预览界面是否打开。
    /// </summary>
    private static void TryEnsureButton(NRun run)
    {
        if (run == null || _buttons.TryGetValue(run, out _))
        {
            return;
        }

        if (run.GlobalUi != null)
        {
            CreateButton(run);
            return;
        }

        TaskHelper.RunSafely(WaitForGlobalUiAsync(run));
    }

    private static async Task WaitForGlobalUiAsync(NRun run)
    {
        for (int i = 0; i < 30; i++)
        {
            if (run == null || !GodotObject.IsInstanceValid(run))
            {
                return;
            }

            if (run.GlobalUi != null)
            {
                CreateButton(run);
                return;
            }

            await Task.Delay(100);
        }
    }

    private static void CreateButton(NRun run)
    {
        if (run.GlobalUi == null || _buttons.TryGetValue(run, out _))
        {
            return;
        }

        var button = new Button
        {
            Text = $"合成 ({AutoChessConfig.SynthesisCost}金币)",
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 1000,
            // 黄色按钮，和商店刷新按钮保持一致但更醒目
            Modulate = new Color(1f, 0.85f, 0.05f),
            TooltipText = "使用游戏标准选牌界面，选择两张相同卡牌进行合成",
        };

        // GlobalUi 覆盖整个运行界面，按钮放在右上角，避开原版顶部资源栏。
        button.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        button.Position = new Vector2(-290f, 76f);
        button.CustomMinimumSize = new Vector2(270f, 42f);
        button.Pressed += () =>
        {
            if (_flowRunning)
            {
                return;
            }
            _flowRunning = true;
            button.Disabled = true;
            button.Visible = false;
            TaskHelper.RunSafely(SynthesisFlowAsync(button));
        };

        run.GlobalUi.AddChild(button);
        _buttons.Add(run, button);
    }

    /// <summary>
    /// 合成流程。
    ///
    /// 不再调用 NCapstoneContainer.Close，也不再调用 NDeckViewScreen.ShowScreen。
    /// FromDeckGeneric 会自己打开并关闭标准的卡牌选择界面；它只返回选择结果，
    /// 不会像 FromDeckForRemoval 那样在返回前把选中的卡移出牌组。
    ///
    /// 这一点很重要：真正的移除必须留给 SynthesisService 统一处理，
    /// 否则选择器返回后，TryMergeAsync 的“卡牌仍在牌组中”检查会永远失败。
    /// </summary>
    private static async Task SynthesisFlowAsync(Button button)
    {
        try
        {
            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            Player? player = runState?.Players.FirstOrDefault();
            if (player == null)
            {
                UiToast.Show("当前没有可用的玩家牌组");
                return;
            }

            if (!HasAnyMergeablePair(player))
            {
                UiToast.Show("此时没有可合成的卡牌");
                return;
            }

            // 使用通用牌组选择器：这里只做“选中两张卡”，不提前修改牌组。
            // 合成服务稍后会以一个可回滚的命令链移除旧卡并加入新卡。
            var prefs = new CardSelectorPrefs(CardSelectorPrefs.RemoveSelectionPrompt, 2);
            List<CardModel> selected = (await CardSelectCmd.FromDeckGeneric(
                player,
                prefs,
                card => IsSelectableForSynthesis(player, card),
                null)).ToList();

            // 取消、返回或选择数量不足时，不做任何改动。
            if (selected.Count != 2)
            {
                return;
            }

            if (!SynthesisService.IsSameGroupForMerge(selected[0], selected[1]))
            {
                UiToast.Show("请选择两张相同的卡牌（同名、同星级）");
                return;
            }

            CardModel? merged = await SynthesisService.TryMergeAsync(player, selected[0], selected[1]);
            if (merged != null)
            {
                UiToast.Show($"合成成功：{merged.Title}");
            }
            else
            {
                UiToast.Show("合成失败，卡牌和金币未改变");
            }
        }
        catch (Exception e)
        {
            // 选择器/Overlay 失败时只提示，不把异常抛回游戏主流程。
            UiToast.Show("合成界面关闭失败，请稍后再试");
            Log.Error($"[AutoChessTactics] 主界面合成流程异常：{e}");
        }
        finally
        {
            _flowRunning = false;
            if (button != null && GodotObject.IsInstanceValid(button))
            {
                button.Disabled = false;
                button.Visible = true;
            }
        }
    }

    /// <summary>牌组中是否至少有一组可合成卡。</summary>
    private static bool HasAnyMergeablePair(Player player)
    {
        List<CardModel> cards = PileType.Deck.GetPile(player).Cards
            .Where(SynthesisDatabase.IsMergeable)
            .GroupBy(card => (card.Id.Entry, GetEffectiveStarForSynthesis(card)))
            .SelectMany(group => group)
            .ToList();

        for (int i = 0; i < cards.Count; i++)
        {
            for (int j = i + 1; j < cards.Count; j++)
            {
                if (cards[i].Id.Entry == cards[j].Id.Entry
                    && GetEffectiveStarForSynthesis(cards[i]) == GetEffectiveStarForSynthesis(cards[j])
                    && SynthesisService.AreEnchantmentsCompatible(cards[i], cards[j], out _))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 选择器过滤：只显示“属于某个可合成组”的卡。
    /// 玩家仍可能选中两个不同组，所以流程结束后还会做一次严格校验。
    /// </summary>
    private static bool IsSelectableForSynthesis(Player player, CardModel card)
    {
        if (!SynthesisDatabase.IsMergeable(card))
        {
            return false;
        }

        int star = GetEffectiveStarForSynthesis(card);
        return PileType.Deck.GetPile(player).Cards
            .Count(other =>
                other != null
                && !ReferenceEquals(other, card)
                && other.Id.Entry == card.Id.Entry
                && GetEffectiveStarForSynthesis(other) == star
                && SynthesisService.AreEnchantmentsCompatible(card, other, out _)) > 0;
    }

    /// <summary>
    /// 合成入口读取星级前先做一次 SL 兜底恢复。
    /// 这样玩家第一次 SL 后即使弱引用表被清空，选择器也不会把二星卡当一星分组。
    /// </summary>
    private static int GetEffectiveStarForSynthesis(CardModel card)
    {
        SynthesisService.RecoverStarFromValuesIfNeeded(card, "synthesis-ui", out _);
        return StarTracker.GetEffective(card);
    }
}
