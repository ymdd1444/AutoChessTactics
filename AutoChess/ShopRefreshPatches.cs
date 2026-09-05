using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AutoChessTactics;

/// <summary>
/// 商店刷新补丁。
///
/// NMerchantInventory.Initialize 只能在节点生命周期中调用一次。
/// 反复调用会让同一批 NMerchantSlot 再次连接 Hovered/Unhovered，
/// 从而产生 Godot 的“Signal already connected”错误。
/// 刷新时改为使用各槽位已有的 FillSlot，只替换商品和视觉。
/// </summary>
public static class ShopRefreshPatches
{
    /// <summary>
    /// 每个库存界面的状态。
    /// Generation 让过期的异步刷新结果失效，InProgress 防止双击并发刷新。
    /// </summary>
    private sealed class RefreshState
    {
        public Button? Button;
        public int Generation;
        public bool InProgress;
    }

    private static readonly ConditionalWeakTable<NMerchantInventory, RefreshState> _states = new();

    [HarmonyPatch(typeof(NMerchantInventory), "_Ready")]
    public static class ReadyPatch
    {
        public static void Postfix(NMerchantInventory __instance)
        {
            try
            {
                EnsureRefreshButton(__instance);
            }
            catch (Exception e)
            {
                Log.Error($"[AutoChessTactics] NMerchantInventory._Ready postfix 异常: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(NMerchantRoom), "_Ready")]
    public static class RoomReadyPatch
    {
        public static void Postfix(NMerchantRoom __instance)
        {
            try
            {
                if (__instance.Inventory != null)
                {
                    EnsureRefreshButton(__instance.Inventory);
                }
            }
            catch (Exception e)
            {
                Log.Error($"[AutoChessTactics] NMerchantRoom._Ready postfix 异常: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(NMerchantInventory), nameof(NMerchantInventory.Open))]
    public static class OpenPatch
    {
        public static void Postfix(NMerchantInventory __instance)
        {
            EnsureRefreshButton(__instance);
            SetButtonVisible(__instance, true);
        }
    }

    [HarmonyPatch(typeof(NMerchantRoom), nameof(NMerchantRoom.OpenInventory))]
    public static class RoomOpenInventoryPatch
    {
        public static void Postfix(NMerchantRoom __instance)
        {
            try
            {
                if (__instance.Inventory != null)
                {
                    EnsureRefreshButton(__instance.Inventory);
                    SetButtonVisible(__instance.Inventory, true);
                }
            }
            catch (Exception e)
            {
                Log.Error($"[AutoChessTactics] NMerchantRoom.OpenInventory postfix 异常: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(NMerchantInventory), "Close")]
    public static class ClosePatch
    {
        public static void Postfix(NMerchantInventory __instance)
        {
            SetButtonVisible(__instance, false);
        }
    }

    [HarmonyPatch(typeof(NMerchantInventory), "_ExitTree")]
    public static class ExitTreePatch
    {
        public static void Postfix(NMerchantInventory __instance)
        {
            RemoveRefreshButton(__instance);
        }
    }

    private static void EnsureRefreshButton(NMerchantInventory inventoryNode)
    {
        if (_states.TryGetValue(inventoryNode, out RefreshState? existing)
            && existing.Button != null
            && GodotObject.IsInstanceValid(existing.Button))
        {
            // 设置可以在 Mod 管理界面中运行时修改，按钮文字也要同步更新。
            existing.Button.Text = $"刷新商店 ({AutoChessConfig.ShopRefreshCost}金币)";
            return;
        }

        NRun? run = NRun.Instance;
        if (run?.GlobalUi == null)
        {
            return;
        }

        var button = new Button
        {
            Text = $"刷新商店 ({AutoChessConfig.ShopRefreshCost}金币)",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 1000,
            Modulate = new Color(1f, 0.9f, 0.15f),
            TooltipText = "花费 20 金币，重新随机商店货物",
        };

        button.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        button.Position = new Vector2(-290f, 24f);
        button.CustomMinimumSize = new Vector2(270f, 42f);
        button.Pressed += () => TaskHelper.RunSafely(OnRefreshPressedAsync(inventoryNode));

        run.GlobalUi.AddChild(button);
        if (_states.TryGetValue(inventoryNode, out RefreshState? state))
        {
            state.Button = button;
        }
        else
        {
            _states.Add(inventoryNode, new RefreshState { Button = button });
        }
    }

    private static void SetButtonVisible(NMerchantInventory inventoryNode, bool visible)
    {
        if (_states.TryGetValue(inventoryNode, out RefreshState? state)
            && state.Button != null
            && GodotObject.IsInstanceValid(state.Button))
        {
            state.Button.MoveToFront();
            state.Button.Visible = visible;
        }
    }

    private static void RemoveRefreshButton(NMerchantInventory inventoryNode)
    {
        if (!_states.TryGetValue(inventoryNode, out RefreshState? state))
        {
            return;
        }

        _states.Remove(inventoryNode);
        if (state.Button != null && GodotObject.IsInstanceValid(state.Button))
        {
            state.Button.QueueFree();
        }
    }

    /// <summary>
    /// 点击刷新按钮。
    /// 费用使用原生命令，库存刷新不再调用 Initialize，失败会恢复库存并退款。
    /// </summary>
    private static async Task OnRefreshPressedAsync(NMerchantInventory inventoryNode)
    {
        if (!GodotObject.IsInstanceValid(inventoryNode)
            || !_states.TryGetValue(inventoryNode, out RefreshState? state))
        {
            return;
        }
        if (state.InProgress)
        {
            Log.Debug("[AutoChessTactics] 忽略并发商店刷新请求。");
            return;
        }

        state.InProgress = true;
        int generation = ++state.Generation;
        bool goldSpent = false;
        MerchantInventory? oldInventory = inventoryNode.Inventory;

        try
        {
            Player? player = oldInventory?.Player;
            if (player == null)
            {
                return;
            }
            if (player.Gold < AutoChessConfig.ShopRefreshCost)
            {
                UiToast.Show("金币不足，无法刷新商店！");
                return;
            }

            await PlayerCmd.LoseGold(AutoChessConfig.ShopRefreshCost, player);
            goldSpent = true;

            MerchantInventory fresh = MerchantInventory.CreateForNormalMerchant(player);
            LogInventorySummary(fresh, "生成新库存");
            if (generation != state.Generation
                || !GodotObject.IsInstanceValid(inventoryNode)
                || !inventoryNode.IsInsideTree())
            {
                throw new InvalidOperationException("商店刷新期间库存节点已失效。");
            }

            // FillSlot 不会重复连接槽位自身的悬停信号，是当前版本安全的刷新路径。
            ApplyInventoryToExistingSlots(inventoryNode, fresh);

            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            if (runState?.CurrentRoom is MerchantRoom merchantRoom)
            {
                int slot = runState.GetPlayerSlotIndex(player);
                if (slot >= 0 && slot < merchantRoom.Inventories.Count)
                {
                    merchantRoom.Inventories[slot] = fresh;
                }
            }

            // NMerchantInventory 第一次 Initialize 时订阅旧库存。
            // 替换 Inventory 属性不会自动订阅新条目，因此显式调用游戏自己的
            // SubscribeToEntries，保证新库存购买后导航和商人对话仍正常工作。
            Traverse.Create(inventoryNode).Method("SubscribeToEntries").GetValue();
            Log.Debug($"[AutoChessTactics] 新库存事件已重新订阅，generation={generation}。");

            UiToast.Show("商店已刷新！");
            Log.Info($"[AutoChessTactics] 商店刷新成功：玩家 {player.NetId}，generation={generation}。");
        }
        catch (Exception e)
        {
            Log.Error($"[AutoChessTactics] 商店刷新异常，开始恢复旧库存：{e}");

            try
            {
                if (oldInventory != null && GodotObject.IsInstanceValid(inventoryNode))
                {
                    ApplyInventoryToExistingSlots(inventoryNode, oldInventory);
                }
            }
            catch (Exception restoreError)
            {
                Log.Error($"[AutoChessTactics] 恢复旧商店 UI 失败：{restoreError}");
            }

            if (goldSpent && oldInventory?.Player != null)
            {
                try
                {
                    await PlayerCmd.GainGold(AutoChessConfig.ShopRefreshCost, oldInventory.Player);
                    Log.Info("[AutoChessTactics] 商店刷新失败，已退还刷新费用。");
                }
                catch (Exception refundError)
                {
                    Log.Error($"[AutoChessTactics] 商店刷新退款失败：{refundError}");
                }
            }
        }
        finally
        {
            state.InProgress = false;
        }
    }

    /// <summary>
    /// 把新库存绑定到已经存在的卡牌、遗物和药水槽。
    ///
    /// NMerchantInventory.Initialize 只负责首次连接信号；
    /// FillSlot 负责后续商品替换，因此不会触发“already connected”。
    /// </summary>
    private static void ApplyInventoryToExistingSlots(
        NMerchantInventory inventoryNode,
        MerchantInventory inventory)
    {
        Traverse.Create(inventoryNode)
            .Property(nameof(NMerchantInventory.Inventory))
            .SetValue(inventory);

        List<MerchantCardEntry> cards = inventory.CardEntries.ToList();
        List<MerchantRelicEntry> relics = inventory.RelicEntries.ToList();
        List<MerchantPotionEntry> potions = inventory.PotionEntries.ToList();
        int cardIndex = 0;
        int relicIndex = 0;
        int potionIndex = 0;

        foreach (NMerchantSlot slot in inventoryNode.GetAllSlots())
        {
            switch (slot)
            {
                case NMerchantCard cardSlot when cardIndex < cards.Count:
                    cardSlot.FillSlot(cards[cardIndex++]);
                    break;
                case NMerchantRelic relicSlot when relicIndex < relics.Count:
                    relicSlot.FillSlot(relics[relicIndex++]);
                    break;
                case NMerchantPotion potionSlot when potionIndex < potions.Count:
                    potionSlot.FillSlot(potions[potionIndex++]);
                    break;
                case NMerchantCardRemoval removalSlot when inventory.CardRemovalEntry != null:
                    removalSlot.FillSlot(inventory.CardRemovalEntry);
                    break;
            }
        }

        if (cardIndex != cards.Count || relicIndex != relics.Count || potionIndex != potions.Count)
        {
            throw new InvalidOperationException(
                $"商店槽位数量不匹配：卡牌 {cardIndex}/{cards.Count}，遗物 {relicIndex}/{relics.Count}，药水 {potionIndex}/{potions.Count}。");
        }

        Traverse.Create(inventoryNode).Method("UpdateNavigation").GetValue();
        Log.Debug(
            $"[AutoChessTactics] 商店 UI 已安全重填：卡牌 {cards.Count}，遗物 {relics.Count}，药水 {potions.Count}。");
    }

    private static void LogInventorySummary(MerchantInventory inventory, string context)
    {
        try
        {
            string cards = string.Join(",",
                inventory.CardEntries.Select(entry =>
                    entry.CreationResult?.Card.Id.Entry ?? "<空>"));
            Log.Debug(
                $"[AutoChessTactics] {context}：卡牌={cards}，遗物={inventory.RelicEntries.Count}，药水={inventory.PotionEntries.Count}。");
        }
        catch (Exception e)
        {
            Log.Debug($"[AutoChessTactics] 记录库存摘要失败：{e.Message}");
        }
    }
}
