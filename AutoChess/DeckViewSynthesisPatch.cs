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
using MegaCrit.Sts2.Core.Rooms;
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

    private const string VisibilityTickerNodeName = "AutoChessSynthesisButtonVisibilityTicker";

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

    /// <summary>地图打开时刷新按钮状态。</summary>
    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen), nameof(MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Open))]
    public static class MapOpenPatch
    {
        public static void Postfix()
        {
            RefreshButtonVisibility();
        }
    }

    /// <summary>地图关闭时隐藏按钮，避免战斗或事件界面残留。</summary>
    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen), "Close")]
    public static class MapClosePatch
    {
        public static void Postfix()
        {
            RefreshButtonVisibility();
        }
    }

    /// <summary>
    /// 创建主界面按钮。GlobalUi 尚未完成时，稍后自动重试，
    /// 不依赖牌组预览界面是否打开。
    /// </summary>
    private static void TryEnsureButton(NRun run)
    {
        if (run == null || HasLiveButton(run))
        {
            return;
        }

        if (IsValid(run.GlobalUi))
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
            if (!IsValid(run))
            {
                return;
            }

            if (IsValid(run.GlobalUi))
            {
                CreateButton(run);
                return;
            }

            await Task.Delay(100);
        }
    }

    private static void CreateButton(NRun run)
    {
        NGlobalUi? globalUi = run.GlobalUi;
        if (!IsValid(globalUi) || HasLiveButton(run))
        {
            return;
        }

        var button = new Button
        {
            Text = $"合成 ({AutoChessConfig.SynthesisCost}金币)",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 1000,
            // 黄色按钮，和商店刷新按钮保持一致但更醒目
            Modulate = new Color(1f, 0.85f, 0.05f),
            TooltipText = "使用游戏标准选牌界面，选择两张相同卡牌进行合成",
        };

        // GlobalUi 覆盖整个运行界面。
        // 按钮放在左上侧，避开顶栏血量/金币，同时满足“非战斗时随手可合成”的入口需求。
        button.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        button.Position = new Vector2(24f, 150f);
        button.CustomMinimumSize = new Vector2(230f, 42f);
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

        globalUi!.AddChild(button);
        EnsureVisibilityTicker(globalUi);
        _buttons.Add(run, button);
        RefreshButtonVisibility();
    }

    /// <summary>
    /// 合成入口只属于地图 UI。
    /// NRun.GlobalUi 会贯穿战斗、事件和商店，所以不能只靠挂载位置判断。
    /// </summary>
    internal static void RefreshButtonVisibility()
    {
        NRun? run = NRun.Instance;
        if (run == null)
        {
            return;
        }

        if (!_buttons.TryGetValue(run, out Button? button))
        {
            TryEnsureButton(run);
            return;
        }

        if (!IsValid(button))
        {
            // 有些界面切换会重建 GlobalUi，旧按钮托管对象还在但底层节点没了。
            // 从弱表移除后立即重建，避免“战斗结束后左上角没有合成按钮”。
            _buttons.Remove(run);
            TryEnsureButton(run);
            return;
        }

        bool inCombat = IsLiveCombat(run);
        // 费用可在运行时设置，避免按钮仍显示旧价格。
        button!.Text = $"合成 ({AutoChessConfig.SynthesisCost}金币)";
        button.Visible = !inCombat && !_flowRunning;
        if (button.Visible)
        {
            button.MoveToFront();
        }
    }

    /// <summary>
    /// 只在真正的战斗过程中隐藏按钮。
    /// 战斗结束奖励、地图、事件、休息、商店都允许打开合成。
    /// </summary>
    private static bool IsLiveCombat(NRun run)
    {
        try
        {
            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            if (runState?.CurrentRoom is CombatRoom combatRoom
                && combatRoom.CombatState != null)
            {
                return combatRoom.CombatState.IsLiveCombat();
            }
        }
        catch (Exception e)
        {
            Log.Debug($"[AutoChessTactics] 判断当前战斗状态失败：{e.Message}");
        }

        return false;
    }

    private static bool HasLiveButton(NRun run)
    {
        if (!_buttons.TryGetValue(run, out Button? existing))
        {
            return false;
        }

        if (IsValid(existing))
        {
            return true;
        }

        _buttons.Remove(run);
        return false;
    }

    private static void EnsureVisibilityTicker(NGlobalUi globalUi)
    {
        try
        {
            if (globalUi.GetNodeOrNull<Node>(VisibilityTickerNodeName) != null)
            {
                return;
            }

            globalUi.AddChild(new AutoChessSynthesisButtonVisibilityTicker
            {
                Name = VisibilityTickerNodeName,
            });
        }
        catch (Exception e)
        {
            Log.Debug($"[AutoChessTactics] 创建合成按钮可见性刷新器失败：{e.Message}");
        }
    }

    private static bool IsValid(GodotObject? obj)
    {
        try
        {
            return obj != null && GodotObject.IsInstanceValid(obj);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
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
                RefreshButtonVisibility();
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

/// <summary>
/// 定时刷新合成按钮可见性。
/// 有些界面切换不会触发地图 Open/Close（例如奖励、事件、SL 后恢复），
/// 所以用一个很轻的 UI tick 兜底，避免按钮状态卡在旧界面。
/// </summary>
public sealed partial class AutoChessSynthesisButtonVisibilityTicker : Node
{
    private double _timer;

    public override void _Process(double delta)
    {
        _timer += delta;
        if (_timer < 0.25)
        {
            return;
        }

        _timer = 0;
        DeckViewSynthesisPatch.RefreshButtonVisibility();
    }
}
