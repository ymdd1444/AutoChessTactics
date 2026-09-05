using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace AutoChessTactics;

/// <summary>
/// 旧版动画 API 兼容补丁（针对引用旧版 SetAnimation 的创意工坊 Mod）。
///
/// 背景：游戏 v0.111.0 把 MegaAnimationState.SetAnimation(string, bool, int) 的返回类型
/// 从 MegaTrackEntry 改成了 void。一些创意工坊 Mod 仍按旧签名编译，导致两处崩溃：
///   1. 这些 Mod 的方法体被 JIT 编译时（方法引用解析失败）；
///   2. 用 Harmony 直接 patch 这些方法时也会崩（Harmony 复制原方法 IL 并解析引用）。
/// 因此【只能】patch 它们外层“不含坏引用”的入口方法，短路后坏方法永远不会被调用，
/// 也就不会被 JIT 编译。
///
/// 每个目标独立 try/catch：能 patch 的成功，不能的跳过（不影响其它兼容项）。
/// 类型/方法不存在时同样静默跳过（对应 Mod 未安装或已更新）。
/// </summary>
public static class ModCompatPatches
{
    /// <summary>应用兼容补丁（在所有 Mod 加载完成后调用，见 AutoChessTactics.ApplyCompatWhenModsReadyAsync）。</summary>
    public static void Apply(Harmony harmony)
    {
        if (!AutoChessConfig.CompatAncientWaifus)
        {
            Log.Info("[AutoChessTactics] 旧版动画 API 兼容已关闭（CompatAncientWaifus=false）。");
            return;
        }

        // AncientWaifus：最优先 patch 输入入口（每次输入必崩的主路径）。
        // GlobalInputCatcher._Input 只调用 HandleGlobalInput，方法体不含坏引用，可以被 patch。
        PatchSkip(harmony, "AncientWaifus.Core.GlobalInputCatcher", "_Input");

        // 以下方法直接包含坏引用（SetAnimation 旧签名），Harmony 复制 IL 时会崩；
        // 逐个尝试，能成功最好，失败则跳过（不阻断其它兼容项）。
        PatchSkip(harmony, "AncientWaifus.Core.GlobalTouchHook", "HandleGlobalInput");
        PatchSkip(harmony, "AncientWaifus.Core.GlobalTouchHook", "PlayIntroAnimation");
        PatchSkip(harmony, "AncientWaifus.SpineClickInteractor", "_Input");
        PatchSkip(harmony, "AncientWaifus.TezcataraBg", "_Ready");

        // KaguyaSilentRavenSkin（同源问题：旧版 SetAnimation）
        PatchSkip(harmony, "SilentSkinMod.Core.Nodes.Animation.KaguyaSilentHeadPressInteractor", "_Ready");
        PatchSkip(harmony, "SilentSkinMod.Core.Nodes.Animation.KaguyaSilentHeadPressInteractor", "_Input");
        PatchSkip(harmony, "SilentSkinMod.Core.Nodes.Animation.KaguyaSilentHeadPressInteractor", "_Process");
    }

    /// <summary>
    /// 给指定类型的指定方法挂一个“跳过”prefix。每个目标独立容错。
    /// </summary>
    private static void PatchSkip(Harmony harmony, string typeName, string methodName)
    {
        try
        {
            Type? type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                Log.Info($"[AutoChessTactics] 未找到类型 {typeName}，跳过兼容项。");
                return;
            }
            MethodInfo? method = AccessTools.Method(type, methodName);
            if (method == null)
            {
                Log.Info($"[AutoChessTactics] 未找到方法 {typeName}.{methodName}，跳过兼容项。");
                return;
            }
            MethodInfo prefix = typeof(ModCompatPatches).GetMethod(nameof(SkipPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
            harmony.Patch(method, prefix: new HarmonyMethod(prefix));
            Log.Info($"[AutoChessTactics] 兼容补丁：已短路 {typeName}.{methodName}");
        }
        catch (Exception e)
        {
            // 例如方法体内含坏引用导致 Harmony 无法复制 IL —— 跳过该目标即可
            Log.Warn($"[AutoChessTactics] 兼容补丁跳过 {typeName}.{methodName}: {e.Message}");
        }
    }

    /// <summary>
    /// Harmony prefix：返回 false 表示“跳过原方法”。
    /// </summary>
    private static bool SkipPrefix()
    {
        return false;
    }
}
