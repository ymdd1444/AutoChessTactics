using System;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace AutoChessTactics;

/// <summary>
/// Mod 入口。
///
/// 游戏加载 mod 时会调用标记了 [ModInitializer("ModLoaded")] 的静态方法 ModLoaded()。
/// 在这里做四件事：
///   1. 用 Harmony 应用所有补丁（地图打开、卡牌标题、卡牌存档、商店刷新、牌组预览合成按钮）；
///   2. 计划在“所有 Mod 加载完成后”应用旧版动画 API 兼容补丁（见 ModCompatPatches.cs）；
///   3. 把 AutoChessRunModel 注册为“运行状态钩子模型”，从而收到房间钩子；
///   4. 监听 RunStarted 事件，在每局开始时重置状态。
///
/// 为什么兼容补丁要延迟到所有 Mod 加载完：
/// 本 Mod 的加载顺序不固定（可能早于 AncientWaifus / KaguyaSilentRavenSkin），
/// 而这些目标类型要等它们各自的 dll 加载后才存在；太早应用会找不到类型而跳过。
/// </summary>
[ModInitializer("ModLoaded")]
public static class AutoChessTactics
{
    internal const string HarmonyId = "com.codex.AutoChessTactics";

    private const string StartupSelfTestEnvVar = "AUTOCHESS_SELFTEST";
    private static Harmony? _harmony;
    private static bool _harmonyPatchesApplied;

    /// <summary>游戏调用此方法加载本 Mod。</summary>
    public static void ModLoaded()
    {
        try
        {
            // 1. Harmony 补丁（id 要唯一，避免和其它 mod 冲突）
            Harmony harmony = EnsureHarmonyPatchesApplied();

            // 开发自测入口：只在当前进程设置 AUTOCHESS_SELFTEST=1 时运行。
            // 平时玩家启动游戏不会执行，避免测试代码影响正常开局。
            RunStartupSelfTestIfRequested();

            // 2. 等所有 Mod 加载完再应用旧版动画 API 兼容补丁
            TaskHelper.RunSafely(ApplyCompatWhenModsReadyAsync(harmony));

            // 3. 注册运行状态钩子模型（单例，跨局复用）
            ModHelper.SubscribeForRunStateHooks("AutoChessTactics", static _ =>
                new AbstractModel[] { AutoChessRunModel.Instance });

            // 4. 每局开始时重置状态
            RunManager.Instance.RunStarted += AutoChessRunModel.Instance.OnRunStarted;

            Log.Info("[AutoChessTactics] 自走棋玩法加载成功！（利息/商店刷新/牌组合成）");
        }
        catch (Exception e)
        {
            // 初始化失败也要记录下来，方便排查
            Log.Error($"[AutoChessTactics] 初始化失败: {e}");
        }
    }

    /// <summary>
    /// 应用 Harmony 补丁，且保证同一进程只应用一次。
    ///
    /// SelfTest 在离线测试进程里也会调用它，用来验证真实 Harmony 克隆补丁；
    /// 游戏加载入口先调用后，SelfTest 再调用时会直接复用，避免重复 postfix。
    /// </summary>
    internal static Harmony EnsureHarmonyPatchesApplied()
    {
        _harmony ??= new Harmony(HarmonyId);
        if (!_harmonyPatchesApplied)
        {
            _harmony.PatchAll(typeof(AutoChessTactics).Assembly);
            _harmonyPatchesApplied = true;
        }
        return _harmony;
    }

    private static void RunStartupSelfTestIfRequested()
    {
        string? enabled = Environment.GetEnvironmentVariable(StartupSelfTestEnvVar);
        if (!string.Equals(enabled, "1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            string result = SelfTest.RunAll();
            foreach (string line in result.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                Log.Info("[AutoChessTactics] SelfTest: " + line);
            }
        }
        catch (Exception e)
        {
            Log.Error($"[AutoChessTactics] SelfTest 启动自检失败: {e}");
        }
    }

    /// <summary>
    /// 等待 ModManager 完成所有 Mod 的加载，再应用兼容补丁。
    /// ModManager.State 变为 Initialized 表示所有 Mod 已处理完毕。
    /// </summary>
    private static async Task ApplyCompatWhenModsReadyAsync(Harmony harmony)
    {
        try
        {
            // 轮询等待：最多 30 秒
            for (int i = 0; i < 60 && ModManager.State != ModManagerState.Initialized; i++)
            {
                await Task.Delay(500);
            }
            // 再等一瞬，确保目标类型可被 TypeByName 查到
            await Task.Delay(500);
            ModCompatPatches.Apply(harmony);
        }
        catch (Exception e)
        {
            Log.Warn($"[AutoChessTactics] 延迟应用兼容补丁失败: {e}");
        }
    }
}
