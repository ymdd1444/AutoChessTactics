namespace AutoChessTactics;

/// <summary>
/// 自走棋战术 Mod 的全局配置。
/// 所有数值都可以在这里集中调整，方便后续平衡性修改。
/// </summary>
public static class AutoChessConfig
{
    /// <summary>每完成一个房间后获得的金币利息百分比（10 = 10%）。</summary>
    public const int InterestPercent = 10;

    /// <summary>合成一张高星卡牌需要花费的金币。</summary>
    public const int SynthesisCost = 20;

    /// <summary>在商店刷新一次货物需要花费的金币。</summary>
    public const int ShopRefreshCost = 20;

    /// <summary>卡牌最高星级（1星→2星→3星）。</summary>
    public const int MaxStarLevel = 3;

    /// <summary>
    /// 存档属性键：我们把“星级”写进卡牌的 SavedProperties，
    /// 这样保存/读档后合成结果不会丢失。
    /// </summary>
    public const string SaveKey = "AutoChessStar";

    /// <summary>
    /// 是否启用 AncientWaifus 快捷兼容。
    /// AncientWaifus 引用了旧版 SetAnimation(string,bool,int)（返回 MegaTrackEntry），
    /// 与 v0.111.0（返回 void）不兼容，会在每次输入时崩溃。开启后我们会短路它的
    /// 输入/点击/背景方法（保留皮肤贴图替换），让游戏能正常运行。
    /// 若 AncientWaifus 作者后续修复了兼容性，可把此项改为 false 关闭本兼容。
    /// </summary>
    public static bool CompatAncientWaifus = true;
}

