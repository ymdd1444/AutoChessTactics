using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace AutoChessTactics;

/// <summary>
/// 卡牌标题补丁：在卡名后面追加星级标记（★★ / ★★★），方便在牌组、手牌里一眼看出合成等级。
///
/// CardModel.Title 是 virtual 属性，我们无法为已有卡牌子类覆写，所以用 Harmony
/// 在 getter 上挂 postfix，把星级后缀拼到结果后面。
/// </summary>
[HarmonyPatch(typeof(CardModel), "get_Title")]
public static class CardTitlePatch
{
    /// <summary>在标题后追加星级后缀。</summary>
    public static void Postfix(CardModel __instance, ref string __result)
    {
        try
        {
            if (string.IsNullOrEmpty(__result))
            {
                return;
            }
            // 标题 getter 是玩家最常触发的路径。若 SL 后只剩二星/三星数值、弱引用星级丢了，
            // 这里会先把星级救回来，再显示星标，避免“数值还在但星星没了”的错觉。
            SynthesisService.RecoverStarFromValuesIfNeeded(__instance, "title", out _);
            int star = StarTracker.GetForDisplay(__instance);
            if (star >= 2)
            {
                // 二星显示 ★★，三星显示 ★★★
                __result += star == 2 ? " ★★" : " ★★★";
            }
        }
        catch (System.Exception e)
        {
            Log.Error($"[AutoChessTactics] CardModel.Title postfix 异常: {e}");
        }
    }
}

