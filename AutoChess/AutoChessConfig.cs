using System;
using Godot;
using Godot.Collections;
using MegaCrit.Sts2.Core.Logging;

namespace AutoChessTactics;

/// <summary>
/// 自走棋战术 Mod 的全局配置。
///
/// 数值设置保存在 user://AutoChessTactics_Settings.json，
/// 这样游戏重启、读档和更新 DLL 后仍会保留玩家自己的平衡设置。
/// </summary>
public static class AutoChessConfig
{
    private const string ConfigPath = "user://AutoChessTactics_Settings.json";

    private static int _interestPercent = 10;
    private static int _synthesisCost = 20;
    private static int _shopRefreshCost = 20;
    private static bool _freeShopCardRemoval = true;

    /// <summary>每完成一个房间后获得的金币利息百分比（0~100）。</summary>
    public static int InterestPercent
    {
        get => _interestPercent;
        set => _interestPercent = Math.Clamp(value, 0, 100);
    }

    /// <summary>合成一张高星卡牌需要花费的金币。</summary>
    public static int SynthesisCost
    {
        get => _synthesisCost;
        set => _synthesisCost = Math.Clamp(value, 0, 9999);
    }

    /// <summary>在商店刷新一次货物需要花费的金币。</summary>
    public static int ShopRefreshCost
    {
        get => _shopRefreshCost;
        set => _shopRefreshCost = Math.Clamp(value, 0, 9999);
    }

    /// <summary>是否让商店删牌服务免费。</summary>
    public static bool FreeShopCardRemoval
    {
        get => _freeShopCardRemoval;
        set => _freeShopCardRemoval = value;
    }

    /// <summary>卡牌最高星级（1星→2星→3星）。</summary>
    public const int MaxStarLevel = 3;

    /// <summary>
    /// 存档属性键：我们把“星级”写进卡牌的 SavedProperties，
    /// 这样保存/读档后合成结果不会丢失。
    /// </summary>
    public const string SaveKey = "AutoChessStar";

    /// <summary>
    /// 是否启用 AncientWaifus 快捷兼容。
    /// 这个开关保留为代码配置，避免把兼容补丁的生命周期和玩法设置混在一起。
    /// </summary>
    public static bool CompatAncientWaifus = true;

    /// <summary>读取用户设置；配置损坏时保留默认值，不能影响游戏启动。</summary>
    public static void Load()
    {
        try
        {
            if (!FileAccess.FileExists(ConfigPath))
            {
                return;
            }

            using FileAccess file = FileAccess.Open(ConfigPath, FileAccess.ModeFlags.Read);
            Json json = new();
            if (json.Parse(file.GetAsText()) != Error.Ok)
            {
                Log.Warn("[AutoChessTactics] 设置文件解析失败，使用默认设置。");
                return;
            }

            Dictionary data = json.Data.AsGodotDictionary();
            if (data.ContainsKey("InterestPercent"))
            {
                InterestPercent = data["InterestPercent"].AsInt32();
            }
            if (data.ContainsKey("SynthesisCost"))
            {
                SynthesisCost = data["SynthesisCost"].AsInt32();
            }
            if (data.ContainsKey("ShopRefreshCost"))
            {
                ShopRefreshCost = data["ShopRefreshCost"].AsInt32();
            }
            if (data.ContainsKey("FreeShopCardRemoval"))
            {
                FreeShopCardRemoval = data["FreeShopCardRemoval"].AsBool();
            }

            Log.Info(
                $"[AutoChessTactics] 已读取设置：利息 {InterestPercent}%、合成 {SynthesisCost}、刷新 {ShopRefreshCost}、删牌免费={FreeShopCardRemoval}。");
        }
        catch (Exception e)
        {
            Log.Warn($"[AutoChessTactics] 读取设置失败，使用默认设置：{e.Message}");
        }
    }

    /// <summary>保存用户设置。设置 UI 每次修改后立即保存。</summary>
    public static void Save()
    {
        try
        {
            Dictionary data = new()
            {
                ["InterestPercent"] = InterestPercent,
                ["SynthesisCost"] = SynthesisCost,
                ["ShopRefreshCost"] = ShopRefreshCost,
                ["FreeShopCardRemoval"] = FreeShopCardRemoval,
            };

            using FileAccess file = FileAccess.Open(ConfigPath, FileAccess.ModeFlags.Write);
            file.StoreString(Json.Stringify(data));
        }
        catch (Exception e)
        {
            Log.Warn($"[AutoChessTactics] 保存设置失败：{e.Message}");
        }
    }
}
