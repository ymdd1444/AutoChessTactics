using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

            LogInventorySummary(oldInventory, "刷新前库存");
            MerchantInventory fresh = MerchantInventory.CreateForNormalMerchant(player);
            LogInventorySummary(fresh, "生成新库存");
            if (generation != state.Generation
                || !GodotObject.IsInstanceValid(inventoryNode)
                || !inventoryNode.IsInsideTree())
            {
                throw new InvalidOperationException("商店刷新期间库存节点已失效。");
            }

            Log.Debug(
                $"[AutoChessTactics] 开始替换商店槽位：generation={generation}，" +
                $"oldInventory={(oldInventory == null ? "null" : "valid")}。");
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
        MerchantInventory? oldInventory = inventoryNode.Inventory;

        // 先解除旧库存的“购买完成/条目变化”回调。
        // 如果不做这一步，旧条目仍持有 NMerchantInventory 和槽位的委托；
        // 连续刷新后购买一次商品会触发多套旧回调，严重时会访问已释放节点。
        DetachInventoryCallbacks(inventoryNode, oldInventory);

        // FillSlot 会连接当前商品条目的回调，但不会替我们清理上一次
        // FillSlot 连接的旧条目，因此必须按槽位类型逐一拆除。
        int detachedSlots = 0;
        foreach (NMerchantSlot slot in inventoryNode.GetAllSlots())
        {
            if (DetachSlotCallbacks(slot))
            {
                detachedSlots++;
            }
        }

        Traverse.Create(inventoryNode)
            .Property(nameof(NMerchantInventory.Inventory))
            .SetValue(inventory);

        List<MerchantCardEntry> cards = inventory.CardEntries.ToList();
        List<MerchantRelicEntry> relics = inventory.RelicEntries.ToList();
        List<MerchantPotionEntry> potions = inventory.PotionEntries.ToList();
        int cardIndex = 0;
        int relicIndex = 0;
        int potionIndex = 0;

        int filledSlots = 0;
        foreach (NMerchantSlot slot in inventoryNode.GetAllSlots())
        {
            switch (slot)
            {
                case NMerchantCard cardSlot when cardIndex < cards.Count:
                    cardSlot.FillSlot(cards[cardIndex++]);
                    filledSlots++;
                    break;
                case NMerchantRelic relicSlot when relicIndex < relics.Count:
                    relicSlot.FillSlot(relics[relicIndex++]);
                    filledSlots++;
                    break;
                case NMerchantPotion potionSlot when potionIndex < potions.Count:
                    potionSlot.FillSlot(potions[potionIndex++]);
                    filledSlots++;
                    break;
                case NMerchantCardRemoval removalSlot when inventory.CardRemovalEntry != null:
                    removalSlot.FillSlot(inventory.CardRemovalEntry);
                    filledSlots++;
                    break;
            }
        }

        if (cardIndex != cards.Count || relicIndex != relics.Count || potionIndex != potions.Count)
        {
            throw new InvalidOperationException(
                $"商店槽位数量不匹配：卡牌 {cardIndex}/{cards.Count}，遗物 {relicIndex}/{relics.Count}，药水 {potionIndex}/{potions.Count}。");
        }

        // Inventory.Initialize 只允许首次初始化；这里调用的是私有的
        // SubscribeToEntries，仅为新库存挂上一次事件，不会重复初始化槽位信号。
        Traverse.Create(inventoryNode).Method("SubscribeToEntries").GetValue();

        Traverse.Create(inventoryNode).Method("UpdateNavigation").GetValue();
        Log.Debug(
            $"[AutoChessTactics] 商店 UI 已安全重填：卡牌 {cards.Count}，遗物 {relics.Count}，" +
            $"药水 {potions.Count}，填充槽位={filledSlots}，解除旧槽位回调={detachedSlots}。");
    }

    /// <summary>
    /// 解除 NMerchantInventory 对旧库存的订阅。
    /// 事件是 Action 委托，使用 -= 是幂等的，重复调用不会抛异常。
    /// </summary>
    private static void DetachInventoryCallbacks(
        NMerchantInventory inventoryNode,
        MerchantInventory? oldInventory)
    {
        if (oldInventory == null)
        {
            return;
        }

        int detached = 0;
        foreach (MerchantEntry entry in oldInventory.AllEntries)
        {
            detached += RemoveEventHandler(
                entry,
                nameof(MerchantEntry.PurchaseCompleted),
                inventoryNode,
                "OnPurchaseCompleted");
            detached += RemoveEventHandler(
                entry,
                nameof(MerchantEntry.EntryUpdated),
                inventoryNode,
                "UpdateNavigation");
        }

        if (detached > 0)
        {
            Log.Debug($"[AutoChessTactics] 已解除旧库存事件订阅：条目数={detached}。");
        }
    }

    /// <summary>
    /// 解除一个槽位当前条目的所有回调。
    ///
    /// 这些字段是原生槽位的私有字段，使用 Traverse 读取是为了兼容
    /// 当前版本的封装；不修改原生程序集，也不重新调用 Initialize。
    /// </summary>
    private static bool DetachSlotCallbacks(NMerchantSlot slot)
    {
        try
        {
            MerchantEntry? oldEntry = slot switch
            {
                NMerchantCard card => Traverse.Create(card).Field("_cardEntry").GetValue<MerchantCardEntry>(),
                NMerchantPotion potion => Traverse.Create(potion).Field("_potionEntry").GetValue<MerchantPotionEntry>(),
                NMerchantRelic relic => Traverse.Create(relic).Field("_relicEntry").GetValue<MerchantRelicEntry>(),
                NMerchantCardRemoval removal => Traverse.Create(removal).Field("_removalEntry").GetValue<MerchantCardRemovalEntry>(),
                _ => null,
            };

            if (oldEntry == null)
            {
                return false;
            }

            int removed = 0;
            removed += RemoveEventHandler(
                oldEntry,
                nameof(MerchantEntry.EntryUpdated),
                slot,
                "UpdateVisual");
            removed += RemoveEventHandler(
                oldEntry,
                nameof(MerchantEntry.PurchaseFailed),
                slot,
                "OnPurchaseFailed");

            switch (slot)
            {
                case NMerchantCard card:
                    removed += RemoveEventHandler(
                        oldEntry,
                        nameof(MerchantEntry.PurchaseCompleted),
                        card,
                        "OnSuccessfulPurchase");
                    break;
                case NMerchantPotion potion:
                    removed += RemoveEventHandler(
                        oldEntry,
                        nameof(MerchantEntry.PurchaseCompleted),
                        potion,
                        "OnSuccessfulPurchase");
                    break;
                case NMerchantRelic relic:
                    removed += RemoveEventHandler(
                        oldEntry,
                        nameof(MerchantEntry.PurchaseCompleted),
                        relic,
                        "OnSuccessfulPurchase");
                    break;
                case NMerchantCardRemoval removal:
                    removed += RemoveEventHandler(
                        oldEntry,
                        nameof(MerchantEntry.PurchaseCompleted),
                        removal,
                        "OnSuccessfulPurchase");
                    break;
            }

            return removed > 0;
        }
        catch (Exception e)
        {
            Log.Warn(
                $"[AutoChessTactics] 解除商店槽位旧回调失败（{slot.GetType().Name}）：{e.Message}");
            return false;
        }
    }

    /// <summary>
    /// 通过事件的 remove 访问器解除原生私有回调。
    ///
    /// 商店节点的回调方法在当前游戏版本是 private，直接写
    /// entry.EntryUpdated -= slot.UpdateVisual 会无法编译。
    /// 这里沿继承链查找真实方法，再按事件声明的委托类型创建同一个方法委托，
    /// 因此可以安全地重复调用，且不会影响新槽位后续的 FillSlot。
    /// </summary>
    private static int RemoveEventHandler(
        MerchantEntry entry,
        string eventName,
        object target,
        string methodName)
    {
        try
        {
            EventInfo? eventInfo = typeof(MerchantEntry).GetEvent(
                eventName,
                BindingFlags.Instance | BindingFlags.Public);
            if (eventInfo?.EventHandlerType == null)
            {
                return 0;
            }
            Type handlerType = eventInfo.EventHandlerType;

            MethodInfo? method = FindInstanceMethod(target.GetType(), methodName);
            if (method == null)
            {
                return 0;
            }

            Delegate? callback = Delegate.CreateDelegate(
                handlerType,
                target,
                method,
                true);
            if (callback == null)
            {
                return 0;
            }
            MethodInfo? removeMethod = eventInfo.GetRemoveMethod(true);
            if (removeMethod == null)
            {
                return 0;
            }

            removeMethod.Invoke(entry, new object[] { callback });
            return 1;
        }
        catch (Exception e)
        {
            // 某些版本可能没有某一类回调；解绑失败不应阻止刷新，
            // 但保留日志便于确认是否需要新增版本适配。
            Log.Debug(
                $"[AutoChessTactics] 解除事件回调失败：{eventName}/{methodName}，{e.Message}");
            return 0;
        }
    }

    /// <summary>沿类型继承链查找 private/protected/public 实例方法。</summary>
    private static MethodInfo? FindInstanceMethod(Type type, string methodName)
    {
        for (Type? current = type;
             current != null;
             current = current.BaseType)
        {
            MethodInfo? method = current.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly);
            if (method != null)
            {
                return method;
            }
        }

        return null;
    }

    private static void LogInventorySummary(MerchantInventory? inventory, string context)
    {
        try
        {
            if (inventory == null)
            {
                Log.Info($"[AutoChessTactics] {context}：库存为空。");
                return;
            }

            string cards = string.Join(",",
                inventory.CardEntries.Select(entry =>
                    entry.CreationResult?.Card.Id.Entry ?? "<空>"));
            Log.Info(
                $"[AutoChessTactics] {context}：卡牌={cards}，遗物={inventory.RelicEntries.Count}，药水={inventory.PotionEntries.Count}。");
        }
        catch (Exception e)
        {
            Log.Debug($"[AutoChessTactics] 记录库存摘要失败：{e.Message}");
        }
    }
}
