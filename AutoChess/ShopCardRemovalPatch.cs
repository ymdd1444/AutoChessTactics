using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Logging;

namespace AutoChessTactics;

/// <summary>
/// 让商店删牌服务跟随设置变成免费。
///
/// MerchantCardRemovalEntry 的实际扣款和 UI 价格都读取 MerchantEntry.Cost，
/// 因此只需要在 Cost getter 的 postfix 中把“删牌条目”的价格改为 0：
/// 原生购买流程、同步消息和“已使用”状态仍然由游戏自己处理。
/// </summary>
public static class ShopCardRemovalPatch
{
    [HarmonyPatch(typeof(MerchantEntry), "get_Cost")]
    public static class CostPatch
    {
        public static void Postfix(MerchantEntry __instance, ref int __result)
        {
            try
            {
                if (AutoChessConfig.FreeShopCardRemoval
                    && __instance is MerchantCardRemovalEntry)
                {
                    __result = 0;
                }
            }
            catch (Exception e)
            {
                Log.Debug($"[AutoChessTactics] 免费删牌价格补丁失败：{e.Message}");
            }
        }
    }
}
