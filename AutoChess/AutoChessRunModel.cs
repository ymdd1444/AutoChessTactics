using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AutoChessTactics;

/// <summary>
/// 自走棋玩法的主控模型。
///
/// 利息的关键约束是：AfterRoomEntered 发生在房间节点切换流程内部，
/// 不能在这个调用栈里直接执行 GainGold，否则原生金币 UI 可能访问已释放的商店节点。
/// </summary>
public sealed class AutoChessRunModel : AbstractModel
{
    /// <summary>单例：ModHelper 的订阅回调每次返回同一个实例，跨局复用。</summary>
    public static AutoChessRunModel Instance { get; } = new();

    /// <summary>最近一次进入的真实房间，用于判断哪个房间刚刚完成。</summary>
    private AbstractRoom? _currentRoom;

    /// <summary>是否已经完成一个房间，等下一次进入房间时结算利息。</summary>
    private bool _interestPending;

    /// <summary>运行状态是否有效。</summary>
    private bool _isRunActive;

    /// <summary>当前计划发放“上一房间利息”的新房间。</summary>
    private AbstractRoom? _interestRoom;

    /// <summary>防止同一个房间重复安排延迟任务。</summary>
    private AbstractRoom? _interestScheduledRoom;

    /// <summary>重试时记录已经成功发放过的玩家，避免部分成功后重复加钱。</summary>
    private readonly HashSet<string> _interestPaidPlayers = new(StringComparer.Ordinal);

    /// <summary>保存本次利息金额快照，重试时不因金币变化而重新计算。</summary>
    private readonly Dictionary<string, int> _interestAmounts = new(StringComparer.Ordinal);

    public override bool ShouldReceiveCombatHooks => false;

    /// <summary>公有无参构造：ModelDb.Init 会用 Activator 创建 AbstractModel。</summary>
    public AutoChessRunModel()
    {
    }

    /// <summary>开始新的一局并清理上一局的运行时状态。</summary>
    public void OnRunStarted(RunState runState)
    {
        _isRunActive = true;
        _currentRoom = null;
        _interestPending = false;
        _interestRoom = null;
        _interestScheduledRoom = null;
        _interestPaidPlayers.Clear();
        _interestAmounts.Clear();
        StarTracker.ClearRunData();
        int recovered = SynthesisService.RecoverStarsInRunState(runState, "RunStarted");
        if (recovered > 0)
        {
            Log.Info($"[AutoChessTactics] RunStarted 后恢复 {recovered} 张已合成卡星级，防止 SL 清空弱引用。");
        }
        Log.Info("[AutoChessTactics] 新的一局开始，自走棋玩法已就绪。");
    }

    /// <summary>
    /// 收到进入房间钩子。
    ///
    /// 这里只启动一个脱离原生进入房间调用栈的安全任务，并立即返回。
    /// </summary>
    public override Task AfterRoomEntered(AbstractRoom room)
    {
        _currentRoom = room;
        if (_interestPending && !ReferenceEquals(_interestScheduledRoom, room))
        {
            _interestPending = false;
            _interestRoom = room;
            _interestScheduledRoom = room;

            // 如果这里 await，EnterRoomInternal 会被金币 UI 更新阻塞；
            // TaskHelper 让任务脱离房间进入调用栈，同时保留游戏主线程调度。
            TaskHelper.RunSafely(GrantInterestWhenRoomReadyAsync(room));
        }

        return Task.CompletedTask;
    }

    /// <summary>地图打开时记账。顶栏查看地图不算完成房间。</summary>
    public void OnMapOpenedAfterRoom(bool fromTopBar)
    {
        try
        {
            if (fromTopBar || !_isRunActive)
            {
                return;
            }
            if (RunManager.Instance.DebugOnlyGetState() == null)
            {
                return;
            }

            // 开局第一次打开地图没有上一房间，不产生利息。
            if (_currentRoom == null)
            {
                return;
            }

            _currentRoom = null;
            _interestPending = true;
            _interestRoom = null;
            _interestScheduledRoom = null;
            _interestPaidPlayers.Clear();
            _interestAmounts.Clear();

            Log.Debug("[AutoChessTactics] 房间完成，利息已记账，等待下一房间 UI 稳定后发放。");
        }
        catch (Exception e)
        {
            // 补丁回调绝不能把异常抛回游戏。
            Log.Error($"[AutoChessTactics] OnMapOpenedAfterRoom 异常: {e}");
        }
    }

    /// <summary>
    /// 等待旧 UI 释放、新 UI Ready 和房间转场完成后再调用原生 GainGold。
    /// </summary>
    private async Task GrantInterestWhenRoomReadyAsync(AbstractRoom room)
    {
        const int maxAttempts = 5;
        NRun? runNode = NRun.Instance;
        if (runNode == null || !GodotObject.IsInstanceValid(runNode))
        {
            _interestPending = true;
            Log.Warn("[AutoChessTactics] NRun 尚未就绪，利息推迟到下一次房间进入。");
            return;
        }

        try
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                // 第一帧让旧房间 QueueFree 生效，第二帧让新房间完成视觉初始化。
                await NodeUtil.AwaitProcessFrame(runNode, CancellationToken.None);
                await NodeUtil.AwaitProcessFrame(runNode, CancellationToken.None);

                RunState? state = RunManager.Instance.DebugOnlyGetState();
                if (state == null
                    || state.CurrentRoom == null
                    || !ReferenceEquals(state.CurrentRoom, room)
                    || state.CurrentRoomCount <= 0)
                {
                    Log.Debug(
                        $"[AutoChessTactics] 利息等待房间栈稳定，第 {attempt}/{maxAttempts} 次，" +
                        $"当前房间={(state?.CurrentRoom?.GetType().Name ?? "null")}，" +
                        $"目标房间={room.GetType().Name}。");
                    continue;
                }

                NTransition? transition = NGame.Instance?.Transition;
                if (transition != null
                    && GodotObject.IsInstanceValid(transition)
                    && transition.InTransition)
                {
                    Log.Debug($"[AutoChessTactics] 利息等待转场结束，第 {attempt}/{maxAttempts} 次。");
                    continue;
                }

                bool completed = await GrantInterestAsync(room);
                if (completed)
                {
                    _interestPending = false;
                    _interestRoom = null;
                    _interestScheduledRoom = null;
                    return;
                }
            }
        }
        catch (ObjectDisposedException e)
        {
            // 保护房间进入流程：已销毁 UI 不得继续向上抛出。
            Log.Warn($"[AutoChessTactics] 利息访问已销毁 UI，推迟结算：{e.Message}");
        }
        catch (Exception e)
        {
            Log.Error($"[AutoChessTactics] 延迟利息任务异常：{e}");
        }

        // 已成功的玩家由集合排除，未成功的部分下次继续结算。
        _interestPending = true;
        Log.Warn("[AutoChessTactics] 当前房间仍不适合发放利息，已推迟到下一次安全房间进入。");
    }

    /// <summary>
    /// 使用游戏原生金币命令发放利息。
    /// 返回 false 表示至少一位玩家失败，需要稍后重试。
    /// </summary>
    private async Task<bool> GrantInterestAsync(AbstractRoom room)
    {
        try
        {
            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            if (runState == null)
            {
                return false;
            }

            bool allCompleted = true;
            foreach (Player player in runState.Players)
            {
                if (player == null)
                {
                    continue;
                }

                string playerKey = player.NetId.ToString();
                if (_interestPaidPlayers.Contains(playerKey))
                {
                    continue;
                }

                if (!_interestAmounts.TryGetValue(playerKey, out int interest))
                {
                    interest = player.Gold * AutoChessConfig.InterestPercent / 100;
                    _interestAmounts[playerKey] = interest;
                }

                if (interest <= 0)
                {
                    _interestPaidPlayers.Add(playerKey);
                    continue;
                }

                try
                {
                    int goldBefore = player.Gold;
                    await PlayerCmd.GainGold(interest, player);
                    _interestPaidPlayers.Add(playerKey);
                    Log.Info(
                        $"[AutoChessTactics] 房间 {room.GetType().Name} 稳定后，玩家 {player.NetId} 利息结算成功：{goldBefore} -> {player.Gold}，金额 +{interest}。");
                    UiToast.Show($"利息 +{interest} 金币");
                }
                catch (Exception e)
                {
                    allCompleted = false;
                    Log.Warn(
                        $"[AutoChessTactics] 玩家 {player.NetId} 利息发放失败（+{interest}），保留待结算状态：{e.GetType().Name}: {e.Message}");
                }
            }

            return allCompleted;
        }
        catch (Exception e)
        {
            Log.Warn($"[AutoChessTactics] GrantInterestAsync 兜底失败：{e.GetType().Name}: {e.Message}");
            return false;
        }
    }
}
