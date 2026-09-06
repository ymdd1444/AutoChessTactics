using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace AutoChessTactics;

/// <summary>
/// 卡牌合成服务。
///
/// 这份实现有两个关键原则：
/// 1. 星级缩放必须“独立重算”，不能在当前值上反复乘，否则会出现逐级误差；
/// 2. 每次只处理当前这张卡本体，绝不遍历整副牌组做全局改写，避免“合成别张牌时把老牌带坏”。
///
/// 规则摘要：
///   - 两张同 id、同星级的卡可合成；
///   - 升级状态可以不同，保留升级更高的那张；
///   - 数值 = 1 星含升级基准 × 累计系数；
///   - 二星系数 1.5，三星系数 3.0；
///   - 费用不变；
///   - 跳过球类卡的 Repeat 变量，球数由 OrbSynthesisService 单独补发。
/// </summary>
public static class SynthesisService
{
    /// <summary>二星数值倍率。集中成常量，避免恢复逻辑和缩放逻辑各写一份。</summary>
    private const decimal Star2Factor = 1.5m;

    /// <summary>三星数值倍率。三星按需求是“一星含升级数值 × 3”。</summary>
    private const decimal Star3Factor = 3.0m;

    /// <summary>
    /// 在构造临时卡、复制升级或附魔时短暂禁用“变更后重新归一”。
    /// 这些卡只是用来计算一星基准/预期数值，不是玩家牌组中的真实卡。
    /// 如果让它们触发 UpgradeInternal/EnchantInternal 的归一化补丁，
    /// 就会再次进入星级推断，形成递归甚至 StackOverflow。
    /// </summary>
    private static int _suppressNormalizeAfterEnchantModify;

    /// <summary>
    /// 尝试把卡 a 与卡 b 合成。
    /// 这个版本是异步的：扣钱走游戏自己的 PlayerCmd，避免金币/UI 不同步。
    /// </summary>
    /// <returns>合成后的那张卡；失败返回 null。</returns>
    public static async Task<CardModel?> TryMergeAsync(Player player, CardModel a, CardModel b)
    {
        if (player == null || a == null || b == null || ReferenceEquals(a, b))
        {
            return null;
        }
        if (player.Gold < AutoChessConfig.SynthesisCost)
        {
            Log.Info("[AutoChessTactics] 金币不足，无法合成。");
            return null;
        }
        if (!IsSameGroupForMerge(a, b))
        {
            Log.Info("[AutoChessTactics] 两张卡不属于同一组（id/星级不一致），无法合成。");
            return null;
        }
        if (!AreEnchantmentsCompatible(a, b, out string enchantmentError))
        {
            Log.Info($"[AutoChessTactics] 附魔不兼容，无法合成：{enchantmentError}");
            return null;
        }

        int currentStar = StarTracker.GetEffective(a);
        if (currentStar >= AutoChessConfig.MaxStarLevel)
        {
            return null;
        }
        if (!SynthesisDatabase.IsMergeable(a))
        {
            return null;
        }

        CardModel keep = ChooseBaseCard(a, b);
        CardModel remove = ReferenceEquals(keep, a) ? b : a;
        int nextStar = currentStar + 1;
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
        {
            Log.Warn("[AutoChessTactics] 当前没有 RunState，取消合成。");
            return null;
        }

        CardPile deck = PileType.Deck.GetPile(player);

        // 选择界面返回的是牌组里的卡牌实例。合成前再检查一次引用，
        // 避免在地图切换/其它命令刚好修改牌组时误删错误的卡牌。
        if (!ContainsCard(deck, keep) || !ContainsCard(deck, remove))
        {
            Log.Warn("[AutoChessTactics] 选中的卡牌已不在牌组中，取消合成。");
            return null;
        }

        // 关键改动：不再直接把 keep 改成高星卡。
        // 新建一个独立的 CardModel，保留原卡的画面、费用、升级状态和其它卡牌信息，
        // 但把它当成牌组中的“新卡实例”。这样原来的两张卡不会被后续游戏流程继续重算，
        // 也不会因为合成另一张牌而共享/污染数值。
        CardModel? synthesized = CreateSynthesisCard(player, keep, nextStar);
        if (synthesized == null)
        {
            Log.Warn("[AutoChessTactics] 创建合成卡失败，取消本次合成。");
            return null;
        }

        bool goldSpent = false;
        CardModel? actualAddedCard = null;
        try
        {
            // 原版融合者的关键顺序是“创建卡 -> 登记到 RunState -> 加入 Deck”。
            // 某些从牌组选择出来的卡，在 CreateCard 后会出现 Owner 或 RunState
            // 登记缺失；CardPileCmd.Add 会因此拒绝这张卡。
            PrepareCardForDeck(runState, synthesized, player);

            // 先扣钱，再进入游戏原生的“移除两张牌 -> 添加一张新牌”命令链。
            // 发生任何异常时，下面的 catch 会把金币和旧卡回滚。
            await PlayerCmd.LoseGold(AutoChessConfig.SynthesisCost, player);
            goldSpent = true;

            // 与 Amalgamator.CombineStrikes/CombineDefends 一致：
            // RemoveFromDeck 负责牌组变更、历史记录和 UI；
            // Add 负责把新卡正确加入牌组并建立运行时上下文。
            await CardPileCmd.RemoveFromDeck(new[] { keep, remove }, true);

            var addResult = await CardPileCmd.Add(
                synthesized,
                PileType.Deck,
                CardPilePosition.Bottom,
                null,
                false);

            // Add 不一定抛异常：某些“加入牌组修改器”会返回 success=false，
            // 或者把待加入卡替换为 result.cardAdded。因此必须检查结果和牌组引用，
            // 不能仅凭 await 正常返回就报告合成成功。
            if (!addResult.success || addResult.cardAdded == null)
            {
                throw new InvalidOperationException(
                    $"CardPileCmd.Add 失败（success={addResult.success}）。");
            }

            actualAddedCard = addResult.cardAdded;
            if (!ContainsCard(deck, actualAddedCard))
            {
                throw new InvalidOperationException(
                    "CardPileCmd.Add 已返回，但新卡没有出现在牌组中。");
            }
            if (!runState.ContainsCard(actualAddedCard))
            {
                throw new InvalidOperationException(
                    "新卡虽然返回成功，但没有登记到 RunState。");
            }

            // 若加入牌组时被某个原版 modifier 替换成了新实例，把星级信息转移到
            // 真正进入牌组的实例，避免预览/读档时又显示成一星。
            if (!ReferenceEquals(actualAddedCard, synthesized))
            {
                StarTracker.Set(actualAddedCard, nextStar);
                ApplyStarScaling(actualAddedCard, nextStar);
                if (keep.Enchantment != null && actualAddedCard.Enchantment == null)
                {
                    CopyEnchantment(keep, actualAddedCard);
                }
            }

            Log.Info(
                $"[AutoChessTactics] 合成成功：新建 {actualAddedCard.Title}（{nextStar} 星）。");
            return actualAddedCard;
        }
        catch (Exception e)
        {
            // 事务式回滚：任何一步失败都不能让玩家白白损失两张卡或 20 金币。
            // 回滚也全部走游戏原生命令，避免直接修改 CardPile 引起 UI 状态不同步。
            Log.Error($"[AutoChessTactics] 合成命令链失败，开始回滚：{e}");
            await RollbackMergeAsync(
                runState,
                player,
                keep,
                remove,
                synthesized,
                actualAddedCard,
                goldSpent);
            return null;
        }
    }

    /// <summary>
    /// 准备一张真正可以加入牌组的新卡。
    ///
    /// CardPileCmd.Add 不仅检查 Owner，还要求卡牌已经登记到 RunState。
    /// RunState.CreateCard(canonicalCard, player) 会同时完成 Owner 和 RunState 登记；
    /// 只有“ModelDb 模板 -> ToMutable”的兜底卡才需要调用 RunState.AddCard。
    ///
    /// 注意：RunState.AddCard 明确要求 mutable 卡尚未设置 Owner，
    /// 因此这里绝不能对一张已经有 Owner 的卡再次执行 card.Owner = player。
    /// </summary>
    private static void PrepareCardForDeck(RunState runState, CardModel card, Player player)
    {
        // 官方 CreateCard 路径返回的卡已经登记在 RunState 中。
        // 直接返回，尤其不能重复设置 Owner（游戏会抛
        // “Card ... already has an owner”）。
        if (runState.ContainsCard(card))
        {
            if (card.Owner == null || !ReferenceEquals(card.Owner, player))
            {
                throw new InvalidOperationException(
                    $"合成卡 {card.Id.Entry} 已登记，但 Owner 不属于当前玩家。");
            }
            return;
        }

        // AddCard 的契约要求卡牌还没有 Owner。
        // 如果这里已经有 Owner，说明创建路径不符合 RunState 的规范；
        // 不要强行覆盖 Owner，而是让事务回滚并记录准确原因。
        if (card.Owner != null)
        {
            throw new InvalidOperationException(
                $"合成卡 {card.Id.Entry} 有 Owner，但尚未登记到 RunState，无法安全加入牌组。");
        }

        // 在模板兜底路径中，mutable 卡可能带有“已从状态移除”标记。
        // 此时它尚未属于当前 RunState，可以安全清除后再重新登记。
        if (card.HasBeenRemovedFromState)
        {
            card.HasBeenRemovedFromState = false;
        }

        // AddCard 会同时登记 RunState 并设置 Owner。
        runState.AddCard(card, player);

        if (!runState.ContainsCard(card)
            || card.Owner == null
            || !ReferenceEquals(card.Owner, player))
        {
            throw new InvalidOperationException(
                $"无法准备合成卡 {card.Id.Entry}：Owner 或 RunState 登记缺失。");
        }
    }

    /// <summary>按引用判断一张卡是否仍在指定牌组中。</summary>
    private static bool ContainsCard(CardPile pile, CardModel card)
    {
        return pile.Cards.Any(existing => ReferenceEquals(existing, card));
    }

    /// <summary>
    /// 合成失败时恢复现场：
    /// 1. 删除可能已经成功加入的合成卡；
    /// 2. 把两张旧卡重新加入牌组；
    /// 3. 退还合成费用；
    /// 4. 清理只创建出来、但没有进牌组的临时实例。
    /// </summary>
    private static async Task RollbackMergeAsync(
        RunState runState,
        Player player,
        CardModel keep,
        CardModel remove,
        CardModel synthesized,
        CardModel? actualAddedCard,
        bool goldSpent)
    {
        CardPile deck = PileType.Deck.GetPile(player);

        try
        {
            CardModel? cardToRemove = actualAddedCard != null && ContainsCard(deck, actualAddedCard)
                ? actualAddedCard
                : ContainsCard(deck, synthesized) ? synthesized : null;
            if (cardToRemove != null)
            {
                await CardPileCmd.RemoveFromDeck(cardToRemove, true);
            }
        }
        catch (Exception e)
        {
            Log.Error($"[AutoChessTactics] 回滚合成卡失败：{e}");
        }

        await RestoreCardToDeckAsync(deck, keep);
        await RestoreCardToDeckAsync(deck, remove);

        if (goldSpent)
        {
            try
            {
                await PlayerCmd.GainGold(AutoChessConfig.SynthesisCost, player);
            }
            catch (Exception e)
            {
                Log.Error($"[AutoChessTactics] 回滚合成金币失败：{e}");
            }
        }

        // CreateCard 会把实例登记到 RunState。若它最终没有进牌组，
        // 将其从运行状态移除，避免存档里留下“幽灵卡”。
        try
        {
            if (!ContainsCard(deck, synthesized) && runState.ContainsCard(synthesized))
            {
                runState.RemoveCard(synthesized);
            }
        }
        catch (Exception e)
        {
            Log.Debug($"[AutoChessTactics] 清理失败的合成卡实例失败：{e.Message}");
        }
    }

    /// <summary>只在缺失时把旧卡放回牌组，避免回滚时重复添加。</summary>
    private static async Task RestoreCardToDeckAsync(CardPile deck, CardModel card)
    {
        try
        {
            if (ContainsCard(deck, card))
            {
                return;
            }

            if (card.Owner == null)
            {
                Log.Warn($"[AutoChessTactics] 无法回滚 {card.Id.Entry}：卡牌没有 Owner。");
                return;
            }

            var result = await CardPileCmd.Add(
                card,
                PileType.Deck,
                CardPilePosition.Bottom,
                null,
                true);
            if (!result.success || result.cardAdded == null || !ContainsCard(deck, result.cardAdded))
            {
                Log.Error($"[AutoChessTactics] 回滚卡牌 {card.Id.Entry} 未成功加入牌组。");
            }
        }
        catch (Exception e)
        {
            Log.Error($"[AutoChessTactics] 回滚卡牌 {card.Id.Entry} 失败：{e}");
        }
    }

    /// <summary>
    /// 创建一张独立的合成卡。
    ///
    /// ToMutable() 会深拷贝 CardModel 的动态变量集合；这里故意不复用 keep，
    /// 也不把高星数值写回原卡。卡牌 Id/模型保持不变，所以卡面仍使用原版资源；
    /// 标题由 CardTitlePatch 根据 StarTracker 显示“★★/★★★”。
    /// </summary>
    internal static CardModel? CreateSynthesisCard(CardModel source, int star)
    {
        // 测试/工具用重载：正式游戏流程会传入 Player，优先走 RunState.CreateCard。
        return CreateSynthesisCard(null, source, star);
    }

    /// <summary>正式游戏流程中创建一张带运行时上下文的独立合成卡。</summary>
    internal static CardModel? CreateSynthesisCard(Player? player, CardModel source, int star)
    {
        try
        {
            CardModel? result = TryCreateIndependentCard(player, source, out string createError);
            if (result == null || ReferenceEquals(result, source))
            {
                // 如果没能创建新实例，宁可取消合成，也不要退回旧的“原地修改”行为。
                throw new InvalidOperationException("[AutoChessTactics] 没有创建独立卡实例，拒绝原地合成。" + createError);
            }

            // 巨镰/遗传算法这类卡的永久成长不是普通升级，而是存在卡牌自己的
            // SavedProperty 字段里。新建合成卡时必须先把这些字段复制过去，
            // 后面的星级缩放才会以“已成长的一星基准”计算。
            CopyPersistentGrowthState(source, result);
            StarTracker.Set(result, star);
            ApplyStarScaling(result, star);
            // 先尝试补回附魔，再做星级重算。附魔复制如果失败，外层事务会回滚。
            if (source.Enchantment != null && result.Enchantment == null)
            {
                CopyEnchantment(source, result);
            }
            return result;
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("[AutoChessTactics] 创建独立合成卡异常。", e);
        }
    }

    /// <summary>
    /// 创建可放回牌组的新 CardModel 实例。
    ///
    /// 优先级：
    /// 1. ModelDb 原始模板 -> ToMutable -> 补升级：最稳定，也最符合“同卡面的新合成卡”；
    /// 2. ToSerializable -> FromSerializable：保留更多原卡状态的兜底路径；
    /// 3. CreateClone：最后兜底，仍然能保证不是原实例。
    ///
    /// 注意：不要对牌组里的 mutable 卡直接 ToMutable()，当前游戏版本会报
    /// “Mutable model ... used in incorrect place”。
    /// </summary>
    private static CardModel? TryCreateIndependentCard(Player? player, CardModel source, out string error)
    {
        var errors = new List<string>();

        try
        {
            // 游戏事件生成新卡的官方入口。它要求第一个参数是 canonical 卡模板，
            // 会正确设置新卡的 Owner、DeckVersion，并把新卡登记到 RunState。
            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            if (player != null && runState != null)
            {
                CardModel canonical = ModelDb.GetById<CardModel>(source.Id);
                CardModel result = runState.CreateCard(canonical, player);
                if (result != null && !ReferenceEquals(result, source))
                {
                    // CreateCard 从 canonical 模板创建的是未升级卡；
                    // 合成结果要继承两张源卡中较高的升级等级。
                    for (int i = 0; i < source.CurrentUpgradeLevel; i++)
                    {
                        result.UpgradeInternal();
                        result.FinalizeUpgradeInternal();
                    }

                    error = string.Empty;
                    return result;
                }
                errors.Add("RunState.CreateCard 返回空或原实例");
            }

            // 从数据库里的原始不可变模板创建新卡。
            // 这条路径不会引用旧卡的 DynamicVars，因此天然规避“合成别的牌影响老牌”的问题。
            CardModel templateResult = ModelDb.GetById<CardModel>(source.Id).ToMutable();
            RunWithStarNormalizationSuppressed(() =>
            {
                for (int i = 0; i < source.CurrentUpgradeLevel; i++)
                {
                    templateResult.UpgradeInternal();
                    templateResult.FinalizeUpgradeInternal();
                }
            });
            if (templateResult != null && !ReferenceEquals(templateResult, source))
            {
                error = string.Empty;
                return templateResult;
            }
            errors.Add("ModelDb 模板路径返回空或原实例");
        }
        catch (Exception e)
        {
            errors.Add("ModelDb 模板路径失败：" + e.GetType().Name + " / " + e.Message);
        }

        try
        {
            CardModel result = CardModel.FromSerializable(source.ToSerializable());
            if (result != null && !ReferenceEquals(result, source))
            {
                error = string.Empty;
                return result;
            }
            errors.Add("序列化路径返回空或原实例");
        }
        catch (Exception e)
        {
            // 某些测试环境或特殊卡状态下，序列化路径可能不可用，继续走克隆兜底。
            errors.Add("序列化路径失败：" + e.GetType().Name + " / " + e.Message);
        }

        try
        {
            CardModel result = source.CreateClone();
            if (result != null && !ReferenceEquals(result, source))
            {
                error = string.Empty;
                return result;
            }
            errors.Add("CreateClone 返回空或原实例");
        }
        catch (Exception e)
        {
            // 两条路径都失败时，由调用方取消合成。
            errors.Add("CreateClone 失败：" + e.GetType().Name + " / " + e.Message);
        }

        error = " 失败原因：" + string.Join("；", errors);
        return null;
    }

    /// <summary>
    /// CardPile.Cards 是 IReadOnlyList，没有 IndexOf；手动查找可以兼容当前游戏 API。
    /// </summary>
    private static int FindCardIndex(IReadOnlyList<CardModel> cards, CardModel target)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            if (ReferenceEquals(cards[i], target))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// 选择合成后保留的基底卡：升级数较高的那张（相等时保留 a）。
    /// 这样“只要有 +，结果就带 +”，且数值按升级版基准缩放。
    /// </summary>
    public static CardModel ChooseBaseCard(CardModel a, CardModel b)
    {
        return a.CurrentUpgradeLevel >= b.CurrentUpgradeLevel ? a : b;
    }

    /// <summary>判断两张卡是否满足合成条件（同 id + 同星 + 可合成）。</summary>
    public static bool IsSameGroupForMerge(CardModel a, CardModel b)
    {
        return StarTracker.IsSameGroup(a, b)
            && SynthesisDatabase.IsMergeable(a);
    }

    /// <summary>
    /// 附魔必须完全同类、同数量才能合成。
    ///
    /// 这样合成结果只需复制一份附魔，不会出现两种附魔叠在一起，
    /// 也不会把不同的附魔效果错误相加。
    /// </summary>
    public static bool AreEnchantmentsCompatible(
        CardModel a,
        CardModel b,
        out string reason)
    {
        EnchantmentModel? left = a?.Enchantment;
        EnchantmentModel? right = b?.Enchantment;

        if (left == null && right == null)
        {
            reason = string.Empty;
            return true;
        }
        if (left == null || right == null)
        {
            reason = "一张卡有附魔，另一张卡没有附魔";
            return false;
        }
        if (left.Id != right.Id)
        {
            reason = $"附魔类型不同（{left.Id.Entry} / {right.Id.Entry}）";
            return false;
        }
        if (left.Amount != right.Amount)
        {
            reason = $"附魔数量不同（{left.Amount} / {right.Amount}）";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 把一张卡缩放到指定星级。
    ///
    /// 这里绝对不直接乘“当前值”，而是先用模板重建出“1 星含升级”的基准卡，
    /// 再按星级系数回写到目标卡。这样：
    ///   - 二星不会因为先前的四舍五入/向下取整累积误差；
    ///   - 合成别的牌时，不会把这张牌的属性一起带偏；
    ///   - 升级后的加成也会一并乘算进去。
    /// </summary>
    public static void ApplyStarScaling(CardModel card, int targetStar)
    {
        if (card == null)
        {
            return;
        }

        CardModel? baseCard = BuildOneStarBaseCard(card);
        if (baseCard == null)
        {
            Log.Warn($"[AutoChessTactics] 无法为 {card.Id.Entry} 重建基准卡，跳过星级缩放。");
            return;
        }

        if (card.Enchantment == null)
        {
            ApplyScaledValues(card, baseCard, targetStar);
            return;
        }

        // 真实卡已经带着原生附魔。先在临时卡上计算一次附魔后的结果，
        // 再把 DynamicVars 快照复制回来，避免对真实卡再次调用 ModifyCard。
        CardModel? enchantedReference =
            BuildEnchantedScalingReference(card, baseCard, targetStar);
        if (enchantedReference == null)
        {
            Log.Warn($"[AutoChessTactics] 无法安全重建 {card.Id.Entry} 的附魔数值，跳过本次星级重算。");
            return;
        }

        ApplyScaledValues(card, baseCard, targetStar);
        CopyDynamicVars(card, enchantedReference);
    }

    /// <summary>
    /// 从一星基准直接应用到指定星级（用于读档恢复）。
    ///
    /// 注意：这里也必须重新走“基准卡 + 星级系数”的路线，
    /// 不能对当前值做乘法，否则读档/二次重算时会再次误差叠加。
    /// </summary>
    public static void ApplyStarScalingFromBase(CardModel card, int star)
    {
        ApplyStarScaling(card, star);
    }

    /// <summary>
    /// 让单张卡回到它当前星级应有的数值。
    /// 用在升级/附魔后，避免星级数值被局部改动冲掉。
    /// </summary>
    public static void NormalizeStarCard(CardModel card)
    {
        if (card == null)
        {
            return;
        }

        int star = StarTracker.GetEffective(card);
        if (star <= 1)
        {
            return;
        }

        ApplyStarScaling(card, star);
    }

    /// <summary>
    /// SL/QuickSL 的读档顺序可能是：FromSerializable 先恢复星级和数值，随后 RunStarted 又清空弱引用表。
    /// 这会留下“数值还是二星，但星级标记没了”的半坏状态；下一次保存时就会把 AutoChessStar 写丢。
    ///
    /// 这里在关键边界做保守恢复：只有当前 DynamicVars 与二星/三星理论值精确匹配时，才重新写回星级。
    /// 无法确认的特殊卡不会强行恢复，避免把普通卡误判成高星卡。
    /// </summary>
    internal static bool RecoverStarFromValuesIfNeeded(CardModel? card, string context, out int recoveredStar)
    {
        recoveredStar = 1;
        try
        {
            if (card == null)
            {
                return false;
            }

            int currentStar = StarTracker.GetEffective(card);
            if (currentStar > 1)
            {
                recoveredStar = currentStar;
                return false;
            }

            if (!TryInferStarFromScaledValues(card, out int inferredStar) || inferredStar <= 1)
            {
                return false;
            }

            StarTracker.Set(card, inferredStar);
            ApplyStarScaling(card, inferredStar);
            recoveredStar = inferredStar;
            if (!IsGameLogSuppressedForOfflineTests())
            {
                Log.Info(
                    $"[AutoChessTactics] 从已缩放数值恢复星级：{card.Id.Entry} -> {inferredStar} 星（{context}）。");
            }
            return true;
        }
        catch (Exception e)
        {
            if (!IsGameLogSuppressedForOfflineTests())
            {
                Log.Debug($"[AutoChessTactics] 星级恢复失败（{context}）：{e.Message}");
            }
            return false;
        }
    }

    /// <summary>
    /// 离线 SelfTestRunner 没有完整 Godot 运行时，直接初始化游戏 Log 可能崩溃。
    /// 只在测试 runner 设置环境变量时跳过日志；正式游戏仍正常记录。
    /// </summary>
    private static bool IsGameLogSuppressedForOfflineTests()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("AUTOCHESS_SUPPRESS_GAME_LOG"),
            "1",
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 在 RunStarted 后修复当前运行中的牌。QuickSL 会重建 RunState，
    /// 这一步能把“刚读档恢复过、随后弱引用被清空”的牌组本体重新挂上星级。
    /// </summary>
    internal static int RecoverStarsInRunState(RunState? runState, string context)
    {
        if (runState == null)
        {
            return 0;
        }

        int recovered = 0;
        var seen = new HashSet<CardModel>(ReferenceEqualityComparer.Instance);
        foreach (Player player in runState.Players)
        {
            if (player == null)
            {
                continue;
            }

            // 牌组是跨房间保存的本体；战斗牌堆是临时克隆。两边都扫一遍，
            // 这样无论玩家是在地图、事件还是战斗中 SL，都能尽量把当前界面里的卡救回来。
            recovered += RecoverStarsInPile(player, PileType.Deck, context, seen);
            recovered += RecoverStarsInPile(player, PileType.Draw, context, seen);
            recovered += RecoverStarsInPile(player, PileType.Hand, context, seen);
            recovered += RecoverStarsInPile(player, PileType.Discard, context, seen);
            recovered += RecoverStarsInPile(player, PileType.Exhaust, context, seen);
            recovered += RecoverStarsInPile(player, PileType.Play, context, seen);
        }
        return recovered;
    }

    /// <summary>扫描一个牌堆并恢复可确认的高星卡；牌堆不存在时静默跳过。</summary>
    private static int RecoverStarsInPile(
        Player player,
        PileType pileType,
        string context,
        HashSet<CardModel> seen)
    {
        try
        {
            CardPile pile = pileType.GetPile(player);
            if (pile?.Cards == null)
            {
                return 0;
            }

            int recovered = 0;
            foreach (CardModel card in pile.Cards)
            {
                if (card == null || !seen.Add(card))
                {
                    continue;
                }
                if (RecoverStarFromValuesIfNeeded(card, context + "/" + pileType, out _))
                {
                    recovered++;
                }
            }
            return recovered;
        }
        catch
        {
            // 非战斗房间没有 Draw/Hand 等战斗牌堆，这是正常情况，不需要刷屏。
            return 0;
        }
    }

    /// <summary>
    /// 根据当前 DynamicVars 判断这张“没有星级标记”的卡是否其实已经被二星/三星缩放过。
    /// 返回 true 只代表“有足够证据恢复”，不做任何写入，方便测试和保存补丁复用。
    /// </summary>
    internal static bool TryInferStarFromScaledValues(CardModel? card, out int star)
    {
        star = 1;
        if (card == null || !SynthesisDatabase.IsMergeable(card))
        {
            return false;
        }

        CardModel? baseCard = BuildOneStarBaseCard(card);
        if (baseCard == null)
        {
            return false;
        }

        // 先试三星再试二星，避免三星数值在少数卡上也满足二星的弱条件。
        if (MatchesScaledValues(card, baseCard, 3))
        {
            star = 3;
            return true;
        }
        if (MatchesScaledValues(card, baseCard, 2))
        {
            star = 2;
            return true;
        }
        return false;
    }

    /// <summary>判断当前卡的可缩放变量是否与某个星级的理论值一致。</summary>
    private static bool MatchesScaledValues(CardModel card, CardModel baseCard, int targetStar)
    {
        Dictionary<string, decimal>? expectedValues = BuildExpectedValueSnapshot(card, baseCard, targetStar);
        if (expectedValues == null || expectedValues.Count == 0)
        {
            return false;
        }

        int compared = 0;
        foreach (DynamicVar baseVar in baseCard.DynamicVars.Values)
        {
            string name = baseVar.Name;
            if (!SynthesisDatabase.ShouldScaleDynamicVar(card, name))
            {
                continue;
            }
            if (!card.DynamicVars.TryGetValue(name, out DynamicVar? currentVar)
                || !expectedValues.TryGetValue(name, out decimal expectedValue))
            {
                continue;
            }

            // 基准为 0 或倍率后仍等于基准的变量没有识别力。
            // 例如 1 × 1.5 向下取整仍是 1，不能单凭它判断为二星。
            if (baseVar.BaseValue == 0m || expectedValue == baseVar.BaseValue)
            {
                continue;
            }

            compared++;
            if (currentVar.BaseValue != expectedValue)
            {
                return false;
            }
        }

        return compared > 0;
    }

    /// <summary>构建某个星级下的理论 DynamicVars 快照；带附魔卡会计算“星级 + 一次附魔”的结果。</summary>
    private static Dictionary<string, decimal>? BuildExpectedValueSnapshot(
        CardModel card,
        CardModel baseCard,
        int targetStar)
    {
        CardModel? reference;
        if (card.Enchantment != null)
        {
            reference = BuildEnchantedScalingReference(card, baseCard, targetStar);
        }
        else
        {
            reference = BuildOneStarBaseCard(card);
            if (reference != null)
            {
                ApplyScaledValues(reference, baseCard, targetStar);
            }
        }

        if (reference == null)
        {
            return null;
        }

        return reference.DynamicVars.Values.ToDictionary(v => v.Name, v => v.BaseValue, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 构造“1 星含升级”的基准卡。
    /// 这里使用游戏模型库里的原始卡模板，先复制出可变对象，再补上当前升级数。
    /// </summary>
    private static CardModel? BuildOneStarBaseCard(CardModel card)
    {
        try
        {
            CardModel baseCard = ModelDb.GetById<CardModel>(card.Id).ToMutable();

            // 只补当前升级数，不碰星级。
            // 这是“计算用临时卡”，不能触发真实卡牌的升级归一化补丁。
            RunWithStarNormalizationSuppressed(() =>
            {
                for (int i = 0; i < card.CurrentUpgradeLevel; i++)
                {
                    baseCard.UpgradeInternal();
                    baseCard.FinalizeUpgradeInternal();
                }
            });

            // 有些卡会在使用后永久改写自己的 SavedProperty：
            //   - GeneticAlgorithm：CurrentBlock / IncreasedBlock
            //   - TheScythe：CurrentDamage / IncreasedDamage
            // 如果这里只拿 ModelDb 模板，后续任何一次归一都会把成长洗回初始值。
            CopyPersistentGrowthState(card, baseCard);
            return baseCard;
        }
        catch (Exception e)
        {
            Log.Warn($"[AutoChessTactics] 重建基准卡失败：{e.Message}");
            return null;
        }
    }

    /// <summary>
    /// 按基准卡和星级系数写回目标卡。
    /// </summary>
    private static void ApplyScaledValues(CardModel card, CardModel baseCard, int targetStar)
    {
        decimal factor = GetStarFactor(targetStar);

        // 先把基准值拍扁成快照，避免在写回过程中枚举集合被其他逻辑扰动。
        List<(string Name, decimal BaseValue)> baseValues = baseCard.DynamicVars.Values
            .Select(v => (v.Name, v.BaseValue))
            .ToList();

        foreach (var (name, baseValue) in baseValues)
        {
            if (!SynthesisDatabase.ShouldScaleDynamicVar(card, name))
            {
                continue;
            }

            if (card.DynamicVars.TryGetValue(name, out DynamicVar? targetVar))
            {
                targetVar.BaseValue = Math.Floor(baseValue * factor);
            }
        }

    }

    /// <summary>统一返回星级倍率；一星用于兜底，不主动缩放。</summary>
    private static decimal GetStarFactor(int targetStar)
    {
        if (targetStar >= 3)
        {
            return Star3Factor;
        }
        if (targetStar == 2)
        {
            return Star2Factor;
        }
        return 1.0m;
    }

    /// <summary>
    /// 在临时卡上计算“星级基础值 + 一次附魔”的最终 DynamicVars。
    ///
    /// 这里不用序列化路径，避免离线自测时碰到 ModelIdSerializationCache 未初始化。
    /// 临时卡从原始模板构造，附魔只执行一次，随后只复制 DynamicVars 数值；
    /// 费用、关键字等非 DynamicVar 属性继续由原生卡保留。
    /// </summary>
    private static CardModel? BuildEnchantedScalingReference(
        CardModel card,
        CardModel baseCard,
        int targetStar)
    {
        if (card.Enchantment == null)
        {
            return null;
        }

        try
        {
            CardModel reference = ModelDb.GetById<CardModel>(card.Id).ToMutable();
            RunWithStarNormalizationSuppressed(() =>
            {
                for (int i = 0; i < card.CurrentUpgradeLevel; i++)
                {
                    reference.UpgradeInternal();
                    reference.FinalizeUpgradeInternal();
                }
            });

            CopyPersistentGrowthState(card, reference);
            // 先写入“基础卡星级值”，此时 reference 还没有附魔。
            ApplyScaledValues(reference, baseCard, targetStar);

            // 直接克隆附魔，不依赖序列化缓存；这对离线测试更稳。
            EnchantmentModel enchantment = CloneEnchantment(card.Enchantment);
            RunWithStarNormalizationSuppressed(() =>
            {
                reference.EnchantInternal(enchantment, enchantment.Amount);
                reference.Enchantment?.ModifyCard();
            });
            reference.FinalizeUpgradeInternal();
            return reference;
        }
        catch (Exception e)
        {
            Log.Warn($"[AutoChessTactics] 构造附魔星级基准失败：{e.Message}");
            return null;
        }
    }

    /// <summary>把临时基准卡的 DynamicVars 快照复制到目标卡，不触发附魔逻辑。</summary>
    private static void CopyDynamicVars(CardModel target, CardModel reference)
    {
        foreach (DynamicVar referenceVar in reference.DynamicVars.Values)
        {
            if (target.DynamicVars.TryGetValue(referenceVar.Name, out DynamicVar? targetVar))
            {
                targetVar.BaseValue = referenceVar.BaseValue;
            }
        }
    }

    /// <summary>
    /// 在目标卡仍没有附魔时，完整复制附魔。
    /// 优先走 ClonePreservingMutability，避免依赖序列化缓存。
    /// </summary>
    private static void CopyEnchantment(CardModel source, CardModel target)
    {
        if (source.Enchantment == null || target.Enchantment != null)
        {
            return;
        }

        EnchantmentModel enchantment = CloneEnchantment(source.Enchantment);
        RunWithStarNormalizationSuppressed(() =>
        {
            target.EnchantInternal(enchantment, enchantment.Amount);
            enchantment.ModifyCard();
        });
        target.FinalizeUpgradeInternal();
    }

    /// <summary>复制附魔模型；离线自测优先走深拷贝，序列化只做兜底。</summary>
    private static EnchantmentModel CloneEnchantment(EnchantmentModel source)
    {
        try
        {
            return (EnchantmentModel)source.ClonePreservingMutability();
        }
        catch (Exception e)
        {
            Log.Warn($"[AutoChessTactics] ClonePreservingMutability 复制附魔失败，回退到序列化：{e.Message}");
            return EnchantmentModel.FromSerializable(source.ToSerializable());
        }
    }

    /// <summary>判断是否是“使用后永久成长”的卡。</summary>
    private static bool IsPersistentGrowthCard(CardModel? card)
    {
        string? entry = card?.Id.Entry;
        return entry != null
            && (entry.Equals("genetic_algorithm", StringComparison.OrdinalIgnoreCase)
                || entry.Equals("the_scythe", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>返回成长卡需要同步的 SavedProperty 字段名。</summary>
    private static bool TryGetPersistentGrowthProperties(
        CardModel card,
        out string currentProperty,
        out string increasedProperty)
    {
        currentProperty = string.Empty;
        increasedProperty = string.Empty;

        string? entry = card?.Id.Entry;
        if (entry == null)
        {
            return false;
        }

        if (entry.Equals("genetic_algorithm", StringComparison.OrdinalIgnoreCase))
        {
            currentProperty = "CurrentBlock";
            increasedProperty = "IncreasedBlock";
            return true;
        }
        if (entry.Equals("the_scythe", StringComparison.OrdinalIgnoreCase))
        {
            currentProperty = "CurrentDamage";
            increasedProperty = "IncreasedDamage";
            return true;
        }
        return false;
    }

    /// <summary>
    /// 复制巨镰/遗传算法的永久成长字段。
    ///
    /// 这些字段不是 DynamicVars 的简单一部分：
    /// 原版出牌后会先写 CurrentDamage/CurrentBlock，再顺手把 DynamicVar 改成一星值。
    /// 我们复制字段后再重套星级，才能让“升星 + 后续成长 + 读档/克隆”同时成立。
    /// </summary>
    private static void CopyPersistentGrowthState(CardModel source, CardModel target)
    {
        try
        {
            if (source == null || target == null || !IsPersistentGrowthCard(source))
            {
                return;
            }
            if (!string.Equals(source.Id.Entry, target.Id.Entry, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (!TryGetPersistentGrowthProperties(source, out string currentProperty, out string increasedProperty))
            {
                return;
            }

            CardModel stateSource = FindBestPersistentGrowthStateSource(source, increasedProperty);
            if (TryReadIntProperty(stateSource, increasedProperty, out int increasedValue))
            {
                TryWriteIntProperty(target, increasedProperty, increasedValue);
            }
            if (TryReadIntProperty(stateSource, currentProperty, out int currentValue))
            {
                TryWriteIntProperty(target, currentProperty, currentValue);
            }
        }
        catch (Exception e)
        {
            Log.Debug($"[AutoChessTactics] 复制成长卡状态失败：{e.Message}");
        }
    }

    /// <summary>
    /// 从当前卡和 DeckVersion 链里挑成长最多的状态源。
    /// 战斗/事件克隆有时只有 DeckVersion 指向牌组本体，这里向上回看能避免复制到旧的默认成长值。
    /// </summary>
    private static CardModel FindBestPersistentGrowthStateSource(CardModel source, string increasedProperty)
    {
        CardModel best = source;
        int bestIncrease = TryReadIntProperty(source, increasedProperty, out int sourceIncrease)
            ? sourceIncrease
            : int.MinValue;

        CardModel? current = source.DeckVersion;
        int guard = 0;
        while (current != null && guard++ < 16)
        {
            if (!string.Equals(current.Id.Entry, source.Id.Entry, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            if (TryReadIntProperty(current, increasedProperty, out int currentIncrease)
                && currentIncrease > bestIncrease)
            {
                best = current;
                bestIncrease = currentIncrease;
            }
            current = current.DeckVersion;
        }
        return best;
    }

    private static bool TryReadIntProperty(CardModel card, string propertyName, out int value)
    {
        value = 0;
        PropertyInfo? property = card.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null || !property.CanRead)
        {
            return false;
        }

        object? raw = property.GetValue(card);
        if (raw is int intValue)
        {
            value = intValue;
            return true;
        }
        if (raw is decimal decimalValue)
        {
            value = (int)decimalValue;
            return true;
        }
        return false;
    }

    private static bool TryWriteIntProperty(CardModel card, string propertyName, int value)
    {
        PropertyInfo? property = card.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null || !property.CanWrite)
        {
            return false;
        }

        property.SetValue(card, value);
        return true;
    }

    /// <summary>
    /// 原版成长卡在 OnPlay 末尾会把 DynamicVars 改回一星成长值；
    /// 对二/三星卡，需要等整张卡真正打完后再补一次星级归一。
    /// </summary>
    private static async Task NormalizePersistentGrowthAfterPlay(Task originalTask, CardModel card)
    {
        try
        {
            await originalTask;
        }
        finally
        {
            try
            {
                NormalizePersistentGrowthCard(card);
                if (card?.DeckVersion != null)
                {
                    NormalizePersistentGrowthCard(card.DeckVersion);
                }
            }
            catch (Exception e)
            {
                Log.Debug($"[AutoChessTactics] 成长卡出牌后重新归一失败：{e.Message}");
            }
        }
    }

    private static void NormalizePersistentGrowthCard(CardModel? card)
    {
        if (card == null || !IsPersistentGrowthCard(card))
        {
            return;
        }

        int star = StarTracker.GetEffective(card);
        if (star <= 1)
        {
            return;
        }

        ApplyStarScaling(card, star);
    }

    /// <summary>
    /// 临时卡做附魔复制时，先禁掉“附魔后立刻重新归一”的补丁。
    /// 这样可以避免在附魔还没完成 ModifyCard 之前就先跑一次星级重算。
    /// </summary>
    private static void RunWithStarNormalizationSuppressed(Action action)
    {
        try
        {
            _suppressNormalizeAfterEnchantModify++;
            action();
        }
        finally
        {
            _suppressNormalizeAfterEnchantModify--;
        }
    }

    /// <summary>
    /// 升级后把已合成牌重新归一。
    /// 这个补丁只影响当前被升级的卡，不会碰别的卡。
    /// </summary>
    [HarmonyPatch(typeof(CardModel), nameof(CardModel.UpgradeInternal))]
    private static class UpgradeInternalPatch
    {
        public static void Postfix(CardModel __instance)
        {
            try
            {
                // 临时基准卡的内部升级只用于计算，不应再次触发星级归一化。
                if (_suppressNormalizeAfterEnchantModify > 0)
                {
                    return;
                }
                NormalizeStarCard(__instance);
            }
            catch (Exception e)
            {
                Log.Debug($"[AutoChessTactics] UpgradeInternal 后重新归一失败：{e.Message}");
            }
        }
    }

    /// <summary>
    /// 附魔后也重新归一一次。
    /// 有些卡的附魔会改动态数值，不重新套星级就会看起来“降星”。
    /// </summary>
    [HarmonyPatch(typeof(CardModel), "EnchantInternal")]
    private static class EnchantInternalPatch
    {
        public static void Postfix(CardModel __instance)
        {
            try
            {
                if (_suppressNormalizeAfterEnchantModify > 0)
                {
                    return;
                }
                NormalizeStarCard(__instance);
            }
            catch (Exception e)
            {
                Log.Debug($"[AutoChessTactics] EnchantInternal 后重新归一失败：{e.Message}");
            }
        }
    }

    /// <summary>
    /// 清除附魔后也重新归一一次，避免原数值回退到未放大状态。
    /// </summary>
    [HarmonyPatch(typeof(CardModel), "ClearEnchantmentInternal")]
    private static class ClearEnchantmentInternalPatch
    {
        public static void Postfix(CardModel __instance)
        {
            try
            {
                NormalizeStarCard(__instance);
            }
            catch (Exception e)
            {
                Log.Debug($"[AutoChessTactics] ClearEnchantmentInternal 后重新归一失败：{e.Message}");
            }
        }
    }

    /// <summary>
    /// 巨镰/遗传算法的成长发生在卡牌自己的 OnPlay 里。
    /// OnPlayWrapper 是 async Task，普通 Postfix 会在 Task 创建后立刻执行；
    /// 这里改为包装返回 Task，确保原版出牌逻辑全部结束后再重套星级。
    /// </summary>
    [HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
    private static class PersistentGrowthOnPlayWrapperPatch
    {
        public static void Postfix(CardModel __instance, ref Task __result)
        {
            if (__result == null || !IsPersistentGrowthCard(__instance))
            {
                return;
            }
            __result = NormalizePersistentGrowthAfterPlay(__result, __instance);
        }
    }

    /// <summary>
    /// 牌进入战斗、奖励或预览界面时，游戏可能通过 CreateClone 生成新实例。
    /// 星级存的是“实例级弱引用”，所以必须把本体的星级显式传给克隆体；
    /// 否则克隆体会被当作一星，表现为“合成后又降星/数值回落”。
    /// </summary>
    [HarmonyPatch(typeof(CardModel), nameof(CardModel.CreateClone))]
    private static class CreateClonePatch
    {
        public static void Postfix(CardModel __instance, CardModel __result)
        {
            CopyStarToClone(__instance, __result);
        }
    }

    /// <summary>
    /// 带玩家上下文的克隆同样复制星级。
    ///
    /// 兼容说明：
    /// 正式版目前没有 CardModel.CreateCloneForPlayer(Player)，beta 版有。
    /// 如果这里直接写 nameof(CardModel.CreateCloneForPlayer)，正式版编译/加载都会失败；
    /// 因此改成运行时按字符串查找，存在就补，不存在就静默跳过。
    /// </summary>
    [HarmonyPatch]
    private static class CreateCloneForPlayerPatch
    {
        private static bool Prepare()
        {
            // 正式版没有这个入口；Prepare 返回 false 时 Harmony 会整类跳过，
            // 避免 PatchAll 因“没有目标方法”直接失败。
            return FindCreateCloneForPlayerMethod() != null;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo? method = FindCreateCloneForPlayerMethod();
            if (method != null)
            {
                yield return method;
            }
        }

        public static void Postfix(CardModel __instance, CardModel __result)
        {
            CopyStarToClone(__instance, __result);
        }

        private static MethodInfo? FindCreateCloneForPlayerMethod()
        {
            return AccessTools.Method(
                typeof(CardModel),
                "CreateCloneForPlayer",
                new[] { typeof(Player) });
        }
    }

    /// <summary>
    /// 某些效果会走 CreateDupe 而不是 CreateClone。
    /// 这条路径也要把星级同步过去，不然复制出来的卡会丢掉合成状态。
    /// </summary>
    [HarmonyPatch]
    private static class CreateDupePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            // 正式版：CreateDupe()
            // beta 版：CreateDupe(Player newOwner)
            // 两者返回值和语义一致，Postfix 只需要源卡与结果卡即可。
            return typeof(CardModel)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method =>
                    method.Name == "CreateDupe"
                    && method.ReturnType == typeof(CardModel)
                    && IsSupportedCreateDupeSignature(method));
        }

        public static void Postfix(CardModel __instance, CardModel __result)
        {
            CopyStarToClone(__instance, __result);
        }
    }

    private static bool IsSupportedCreateDupeSignature(MethodInfo method)
    {
        ParameterInfo[] parameters = method.GetParameters();
        return parameters.Length == 0
            || (parameters.Length == 1 && parameters[0].ParameterType == typeof(Player));
    }

    /// <summary>
    /// 事件、奖励和牌组结构流程有时通过 RunState.CloneCard 复制卡，
    /// 不会经过 CardModel.CreateClone。这里补上运行域克隆，防止进事件后星级丢失。
    /// </summary>
    [HarmonyPatch(typeof(RunState), nameof(RunState.CloneCard))]
    private static class RunStateCloneCardPatch
    {
        public static void Postfix(CardModel mutableCard, CardModel __result)
        {
            CopyStarToClone(mutableCard, __result);
        }
    }

    /// <summary>
    /// 战斗域也有自己的 CloneCard 入口；某些战斗临时牌或回放复制会走这里。
    /// 复制直接星级后，后续保存/显示不必只依赖 DeckVersion 回看。
    /// </summary>
    [HarmonyPatch(typeof(CombatState), nameof(CombatState.CloneCard))]
    private static class CombatStateCloneCardPatch
    {
        public static void Postfix(CardModel mutableCard, CardModel __result)
        {
            CopyStarToClone(mutableCard, __result);
        }
    }

    internal static void CopyStarToClone(CardModel source, CardModel clone)
    {
        try
        {
            if (source == null || clone == null)
            {
                return;
            }

            // 不能只读 source 自己的弱引用。事件/战斗克隆经常只有 DeckVersion，
            // 直接 Get 会返回 1，继续克隆时就把高星卡“洗回”一星了。
            int star = StarTracker.GetEffective(source);
            if (star <= 1)
            {
                return;
            }

            StarTracker.Set(clone, star);
            CopyPersistentGrowthState(source, clone);
            // 某些克隆路径只复制了模板值，没有复制牌组本体当前的动态值；
            // 这里按克隆体自己的升级等级重新计算，避免战斗牌与牌组牌不一致。
            ApplyStarScaling(clone, star);
        }
        catch (Exception e)
        {
            Log.Debug($"[AutoChessTactics] 克隆卡复制星级失败：{e.Message}");
        }
    }
}
