using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace AutoChessTactics;

/// <summary>
/// 卡牌稀有度补丁。
///
/// 这个版本的游戏在商店、奖励、事件等地方都通过 CardRarityOdds 来决定抽到白/蓝/金卡的概率。
/// 因此最稳的做法是把“自定义稀有度概率”接在这里，而不是改每个库存生成器。
///
/// 开关关闭时完全回原版。
/// 开关打开时按设置里的白/蓝/金百分比抽样：
///   - 例如 1 / 1 / 98
///   - 例如 98 / 1 / 1
/// </summary>
public static class CardRarityOddsPatches
{
    [HarmonyPatch]
    private static class RollWithoutChangingFutureOddsPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(
                typeof(CardRarityOdds),
                nameof(CardRarityOdds.RollWithoutChangingFutureOdds),
                new[] { typeof(CardRarityOddsType) });

            yield return AccessTools.Method(
                typeof(CardRarityOdds),
                nameof(CardRarityOdds.RollWithoutChangingFutureOdds),
                new[] { typeof(CardRarityOddsType), typeof(float) });
        }

        public static void Postfix(CardRarityOdds __instance, ref CardRarity __result)
        {
            ApplyCustomRarityIfEnabled(__instance, ref __result);
        }
    }

    [HarmonyPatch]
    private static class RollWithBaseOddsPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(
                typeof(CardRarityOdds),
                nameof(CardRarityOdds.RollWithBaseOdds),
                new[] { typeof(CardRarityOddsType) });
        }

        public static void Postfix(CardRarityOdds __instance, ref CardRarity __result)
        {
            ApplyCustomRarityIfEnabled(__instance, ref __result);
        }
    }

    private static void ApplyCustomRarityIfEnabled(CardRarityOdds odds, ref CardRarity result)
    {
        if (!AutoChessConfig.CustomCardRarityEnabled)
        {
            return;
        }

        if (!TryGetRng(odds, out Rng? rng))
        {
            return;
        }

        if (rng == null)
        {
            return;
        }

        var weights = AutoChessConfig.GetCustomCardRarityWeights();
        float roll = rng.NextFloat();
        result = roll < weights.Rare
            ? CardRarity.Rare
            : roll < weights.Rare + weights.Uncommon
                ? CardRarity.Uncommon
                : CardRarity.Common;
    }

    private static bool TryGetRng(CardRarityOdds odds, out Rng? rng)
    {
        try
        {
            rng = Traverse.Create(odds).Field("_rng").GetValue<Rng>();
            return rng != null;
        }
        catch
        {
            rng = null;
            return false;
        }
    }
}
