using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace AutoChessTactics;

/// <summary>
/// 卡牌存档补丁：让“星级”能够随存档保存/恢复。
///
/// 原理：CardModel.ToSerializable() 生成的 SerializableCard 里有一个 Props（SavedProperties）
/// 字段，本来就是一个 key-value 存档容器。我们：
///   - 保存时：往 Props.ints 里追加一条 AutoChessStar = 星级；
///   - 读档时：从 Props 里读出星级，重新把卡牌数值缩放到对应星级。
///
/// 这样玩家中途保存/读档，合成结果不会丢。
///
/// 已知问题与修复：
/// SavedProperties 的 Packet 序列化（战斗回放/多人）要求属性名能映射到 net ID。
/// beta 版和正式版这里的内部缓存类名字不同：
///   - beta：ModelIdSerializationCache.GetNetIdForPropertyName(...)
///   - 正式版：SavedPropertiesTypeCache.GetNetIdForPropertyName(...)
/// 自定义的 AutoChessStar 不在原版映射表里，会导致战斗结算/保存退出写 Packet 时抛异常卡死。
///
/// 兼容修复：
/// 不再编译期引用任何一个版本的内部缓存类，只在 Packet 序列化前临时移除本 mod 自己的
/// AutoChessStar 字段；JSON 存档仍然保留它，所以 SL/读档不会丢星级。
/// </summary>
public static class CardSavePatches
{
    /// <summary>保存补丁：把星级写入 SerializableCard.Props。</summary>
    [HarmonyPatch(typeof(CardModel), nameof(CardModel.ToSerializable))]
    public static class SavePatch
    {
        public static void Postfix(CardModel __instance, ref SerializableCard? __result)
        {
            try
            {
                if (__result == null)
                {
                    return;
                }
                // QuickSL 有一种危险顺序：读档已经把数值恢复成二星/三星，
                // 但 RunStarted 随后清空了弱引用星级表。保存前先尝试从已缩放数值救回星级，
                // 否则第一次 SL 会写丢 AutoChessStar，第二次 SL 时数值也会退回一星。
                SynthesisService.RecoverStarFromValuesIfNeeded(__instance, "save", out _);

                // 保存时使用“有效星级”：事件/战斗里的临时克隆可能没有自己的弱引用记录，
                // 但 DeckVersion 指向的牌组本体仍然是高星卡。只读直接星级会把它误存成一星。
                int star = StarTracker.GetEffective(__instance);
                __result.Props ??= new SavedProperties();
                __result.Props.ints ??= new List<SavedProperties.SavedProperty<int>>();

                // 先移除旧版本可能遗留的 AutoChessStar。
                // 否则同一张卡多次保存/二星升三星时可能出现多个星级键，
                // 读档拿到前面的旧值，看起来就像“莫名其妙降星”。
                __result.Props.ints.RemoveAll(prop => prop.name == AutoChessConfig.SaveKey);

                if (star <= 1)
                {
                    return;
                }

                __result.Props.ints.Add(new SavedProperties.SavedProperty<int>(AutoChessConfig.SaveKey, star));
            }
            catch (Exception e)
            {
                Log.Error($"[AutoChessTactics] ToSerializable postfix 异常: {e}");
            }
        }
    }

    /// <summary>读档补丁：从 Props 读出星级并重新应用到卡牌。</summary>
    [HarmonyPatch(typeof(CardModel), nameof(CardModel.FromSerializable))]
    public static class LoadPatch
    {
        public static void Postfix(SerializableCard? save, ref CardModel? __result)
        {
            try
            {
                if (__result == null)
                {
                    return;
                }
                int star = 1;
                if (save?.Props?.ints != null)
                {
                    foreach (SavedProperties.SavedProperty<int> prop in save.Props.ints)
                    {
                        if (prop.name == AutoChessConfig.SaveKey)
                        {
                            // 兼容旧版本可能留下的重复 AutoChessStar：
                            // 如果列表里同时有 2 和 3，取最高值，避免读档后降星。
                            star = Math.Max(star, prop.value);
                        }
                    }
                }
                if (star >= 2)
                {
                    // 先记录星级，再把卡牌数值从“一星基准”缩放到对应星级
                    StarTracker.Set(__result, star);
                    SynthesisService.ApplyStarScalingFromBase(__result, star);
                    return;
                }

                // 某些第三方 SL/存档流程可能没带 AutoChessStar，
                // 但 SerializableCard 仍恢复出了已缩放 DynamicVars。这里兜底恢复一次。
                SynthesisService.RecoverStarFromValuesIfNeeded(__result, "load", out _);
            }
            catch (Exception e)
            {
                Log.Error($"[AutoChessTactics] FromSerializable postfix 异常: {e}");
            }
        }
    }

    /// <summary>
    /// SavedProperties.Serialize 补丁：Packet 序列化时跳过 AutoChessStar。
    /// 这样战斗回放/多人 Packet 序列化不会因未知属性名崩溃；本地 JSON 存档不受影响。
    /// </summary>
    [HarmonyPatch(typeof(SavedProperties), nameof(SavedProperties.Serialize))]
    public static class SerializePatch
    {
        /// <summary>序列化前的原 ints 列表备份（postfix 恢复用）。</summary>
        private static readonly Dictionary<SavedProperties, List<SavedProperties.SavedProperty<int>>> _backups = new();

        public static void Prefix(SavedProperties __instance)
        {
            try
            {
                if (__instance.ints == null || __instance.ints.Count == 0)
                {
                    return;
                }

                if (!__instance.ints.Any(prop => prop.name == AutoChessConfig.SaveKey))
                {
                    return;
                }

                List<SavedProperties.SavedProperty<int>> original = __instance.ints;
                List<SavedProperties.SavedProperty<int>> filtered = original
                    .Where(prop => prop.name != AutoChessConfig.SaveKey)
                    .ToList();

                if (filtered.Count != original.Count)
                {
                    _backups[__instance] = original;
                    __instance.ints = filtered;
                    Log.Debug("[AutoChessTactics] Packet 序列化临时跳过 AutoChessStar；JSON 存档仍会保留星级");
                }
            }
            catch (Exception e)
            {
                Log.Error($"[AutoChessTactics] SavedProperties.Serialize prefix 异常: {e}");
            }
        }

        public static void Finalizer(SavedProperties __instance)
        {
            try
            {
                if (_backups.TryGetValue(__instance, out List<SavedProperties.SavedProperty<int>>? original))
                {
                    __instance.ints = original;
                    _backups.Remove(__instance);
                }
            }
            catch (Exception e)
            {
                Log.Error($"[AutoChessTactics] SavedProperties.Serialize postfix 异常: {e}");
            }
        }
    }
}
