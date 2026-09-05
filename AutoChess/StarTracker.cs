using System;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Models;

namespace AutoChessTactics;

/// <summary>
/// 卡牌星级追踪器。
///
/// 杀戮尖塔2 的 CardModel 没有给我们预留“自定义字段”，所以我们用 .NET 的
/// ConditionalWeakTable（弱引用表）在运行时把“星级”挂到每张卡牌实例上：
///   - 不会让卡牌对象一直存活（卡被移除/回收后条目自动消失）；
///   - 不修改游戏任何原有字段，避免破坏存档结构。
///
/// 注意：牌组里的卡是“本体”；进入战斗后系统会克隆它们。克隆体保留了缩放后的
/// DynamicVars（数值），但星级需要借助 CardModel.DeckVersion 反向查找本体。
/// </summary>
public static class StarTracker
{
    /// <summary>星级的弱引用存储：CardModel -> 星级（1/2/3）。</summary>
    private static readonly ConditionalWeakTable<CardModel, StarInfo> _stars = new();

    /// <summary>
    /// 防止“推断星级 -> 重建基准卡 -> 触发 UpgradeInternal 补丁 ->
    /// 再次推断星级”形成递归。游戏正常卡牌流程始终在同一线程上执行，
    /// 用线程级深度计数即可覆盖构造基准卡的嵌套调用。
    /// </summary>
    [ThreadStatic]
    private static int _inferenceDepth;

    /// <summary>读取一张卡实例自己的星级，未记录则为 1（一星=普通卡）。</summary>
    public static int Get(CardModel card)
    {
        if (card == null)
        {
            return 1;
        }
        return _stars.TryGetValue(card, out StarInfo? info) ? info.Level : 1;
    }

    /// <summary>
    /// 读取一张卡的“有效星级”。
    ///
    /// 很多事件、奖励预览和战斗流程不会直接使用牌组里的原实例，
    /// 而是拿一张 DeckVersion 指向原卡的临时克隆。临时克隆没有弱引用记录时，
    /// 这里会回看牌组本体，避免卡面/保存/复制流程误判为一星。
    /// </summary>
    public static int GetEffective(CardModel card)
    {
        if (card == null)
        {
            return 1;
        }

        int star = Get(card);
        if (star > 1)
        {
            return star;
        }

        // 战斗/事件克隆体：只在“有效星级”场景沿 DeckVersion 找本体。
        // 不把这段回溯放进 Get：DeckVersion 可能指向共享模板，
        // 否则一张牌的星级会污染同模板的其它卡。
        //
        // 这里用有限链路回溯而不是只看一跳，是为了覆盖：
        // 牌组本体(2星) -> 事件预览克隆 -> 事件选择/保存克隆
        // 这种多次复制流程。8 跳足够覆盖正常游戏链路，自环/断链会安全停止。
        CardModel? current = card?.DeckVersion;
        for (int i = 0; i < 8 && current != null; i++)
        {
            if (ReferenceEquals(current, card))
            {
                break;
            }

            int deckStar = Get(current);
            if (deckStar > 1)
            {
                return deckStar;
            }

            CardModel? next = current.DeckVersion;
            if (next == null || ReferenceEquals(next, current))
            {
                break;
            }
            current = next;
        }

        // 某些 SL 流程会先恢复 DynamicVars，再晚一帧恢复自定义 Props。
        // 这里最后再用“已缩放数值”做一次严格推断，避免 UI/事件把二星卡
        // 当成一星继续处理。TryInferStarFromScaledValues 只在数值完全匹配
        // 理论二星/三星时返回，不会把普通升级卡误判成高星卡。
        if (_inferenceDepth > 0)
        {
            return 1;
        }

        _inferenceDepth++;
        try
        {
            if (SynthesisService.TryInferStarFromScaledValues(card, out int inferredStar)
                && inferredStar > 1)
            {
                Set(card!, inferredStar);
                return inferredStar;
            }
        }
        finally
        {
            _inferenceDepth--;
        }

        return 1;
    }

    /// <summary>
    /// 读取一张卡的“显示用星级”。保留旧方法名，实际使用有效星级逻辑。
    /// </summary>
    public static int GetForDisplay(CardModel card)
    {
        return GetEffective(card);
    }

    /// <summary>设置/更新一张卡的星级。</summary>
    public static void Set(CardModel card, int starLevel)
    {
        if (card == null)
        {
            return;
        }
        _stars.Remove(card);
        _stars.Add(card, new StarInfo { Level = Math.Clamp(starLevel, 1, AutoChessConfig.MaxStarLevel) });
    }

    /// <summary>
    /// 判断两张卡是否属于“同一组”（相同 id + 相同星级）。
    /// 升级状态（+）可以不同：按需求“只要有+，合成出的牌也带+”，
    /// 所以允许普通与升级混搭合成，升级数在合成时取较高者。
    /// </summary>
    public static bool IsSameGroup(CardModel a, CardModel b)
    {
        return a != null
            && b != null
            && a.Id == b.Id
            && GetEffective(a) == GetEffective(b);
    }

    /// <summary>
    /// 新一轮开始时清空旧数据，避免跨局残留。
    /// 旧卡的弱引用会被回收，这里只是显式清理。
    /// </summary>
    public static void ClearRunData()
    {
        _stars.Clear();
    }

    /// <summary>弱引用表的值类型。</summary>
    private sealed class StarInfo
    {
        public int Level = 1;
    }
}
