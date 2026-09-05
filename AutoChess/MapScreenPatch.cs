using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace AutoChessTactics;

/// <summary>
/// 地图屏幕补丁。
///
/// 游戏里“房间完成 -> 回到地图”时，房间节点会调用 NMapScreen.Open()。
/// 我们在这个方法上挂 postfix，把“刚完成一个房间”这件事通知给 AutoChessRunModel，
/// 由它去结算利息、弹出合成界面。
///
/// 注意：玩家从顶栏打开地图时参数 isOpenedFromTopBar=true，需要排除掉，
/// 否则看地图也会白拿利息。
/// </summary>
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Open))]
public static class MapScreenPatch
{
    /// <summary>Open 执行完后回调自走棋主控模型。</summary>
    public static void Postfix(bool isOpenedFromTopBar)
    {
        try
        {
            AutoChessRunModel.Instance.OnMapOpenedAfterRoom(isOpenedFromTopBar);
            DeckViewSynthesisPatch.RefreshButtonVisibility();
        }
        catch (System.Exception e)
        {
            // 补丁里不允许把异常抛回游戏
            Log.Error($"[AutoChessTactics] MapScreen.Open postfix 异常: {e}");
        }
    }
}
