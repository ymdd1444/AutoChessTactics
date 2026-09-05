using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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

    /// <summary>
    /// 最近一次完成房间的运行时唯一标识。
    ///
    /// 地图 Open 可能因为动画、读档或重复信号触发多次。
    /// 记录房间实例的唯一键可以把“同一个房间重复结算”与“下一个房间结算”区分开。
    /// </summary>
    private string? _lastCompletedRoomId;

    /// <summary>当前等待结算的来源房间唯一标识，便于日志和重复保护。</summary>
    private string? _interestSourceRoomId;

    /// <summary>当前正在等待稳定的新房间唯一标识。</summary>
    private string? _interestTargetRoomId;

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
        _lastCompletedRoomId = null;
        _interestSourceRoomId = null;
        _interestTargetRoomId = null;
        // 不在这里清空星级弱引用。
        // QuickSL 的实际顺序可能是“先重建卡牌，再触发 RunStarted”，
        // 清空会制造“数值还是二星、星级却变一星”的半坏状态。
        // 弱引用表不会让旧局卡牌存活；新卡实例也不会继承旧实例的星级，
        // 因此保留表是安全的，关键是随后从存档数值恢复当前牌组。
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
            _interestTargetRoomId = GetRoomId(room);

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

            string completedRoomId = GetRoomId(_currentRoom);
            if (string.Equals(_lastCompletedRoomId, completedRoomId, StringComparison.Ordinal))
            {
                Log.Debug($"[AutoChessTactics] 忽略重复的房间完成信号：roomId={completedRoomId}。");
                return;
            }

            _lastCompletedRoomId = completedRoomId;
            _currentRoom = null;
            _interestPending = true;
            _interestRoom = null;
            _interestScheduledRoom = null;
            _interestSourceRoomId = completedRoomId;
            _interestTargetRoomId = null;
            _interestPaidPlayers.Clear();
            _interestAmounts.Clear();

            Log.Debug(
                $"[AutoChessTactics] 房间完成，利息已记账：sourceRoomId={completedRoomId}，" +
                "等待下一房间节点稳定后发放。");
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
        const int stableFramesRequired = 3;
        NRun? runNode = NRun.Instance;
        if (runNode == null || !GodotObject.IsInstanceValid(runNode))
        {
            _interestPending = true;
            Log.Warn(
                $"[AutoChessTactics] NRun 尚未就绪，利息推迟：sourceRoomId={_interestSourceRoomId}，" +
                $"targetRoomId={_interestTargetRoomId}。");
            return;
        }

        try
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                Node? stableNode = null;
                int stableFrames = 0;
                string lastFailureReason = "尚未找到当前房间节点";

                // 不能只看 RunState.CurrentRoomCount：
                // 飞行特效会改变地图移动/房间计数时序，但实际房间节点仍然会正常创建。
                // 连续看到同一个节点数帧，才认为旧商店/宝箱节点已经离开、当前房间已经可用。
                for (int frame = 0; frame < 45; frame++)
                {
                    await NodeUtil.AwaitProcessFrame(runNode, CancellationToken.None);

                    Node? candidate = GetRoomNode(runNode, room);
                    if (candidate == null
                        || !GodotObject.IsInstanceValid(candidate)
                        || !candidate.IsInsideTree()
                        || candidate.GetParent() == null)
                    {
                        stableNode = null;
                        stableFrames = 0;
                        lastFailureReason = "当前房间节点尚未进入场景树";
                        continue;
                    }

                    if (!ReferenceEquals(stableNode, candidate))
                    {
                        stableNode = candidate;
                        stableFrames = 1;
                    }
                    else
                    {
                        stableFrames++;
                    }

                    if (stableFrames >= stableFramesRequired)
                    {
                        break;
                    }
                }

                if (stableNode == null || stableFrames < stableFramesRequired)
                {
                    Log.Debug(
                        $"[AutoChessTactics] 利息等待房间节点稳定，第 {attempt}/{maxAttempts} 次：" +
                        $"roomId={_interestTargetRoomId}，reason={lastFailureReason}。");
                    continue;
                }

                NTransition? transition = NGame.Instance?.Transition;
                bool transitionActive = transition != null
                    && GodotObject.IsInstanceValid(transition)
                    && transition.InTransition;
                if (transitionActive)
                {
                    // InTransition 只作为诊断信息。
                    // 飞行特效下转场标志可能比真实房间节点晚清理，
                    // 不能再把它当成永久硬门槛，否则利息永远不会发放。
                    Log.Debug(
                        $"[AutoChessTactics] 房间节点已连续稳定 {stableFrames} 帧，" +
                        $"但转场标志仍为 true；继续尝试原生金币命令：attempt={attempt}/{maxAttempts}。");
                }

                RunState? state = RunManager.Instance.DebugOnlyGetState();
                if (state == null)
                {
                    Log.Debug(
                        $"[AutoChessTactics] 利息等待 RunState 恢复，第 {attempt}/{maxAttempts} 次：" +
                        $"roomId={_interestTargetRoomId}。");
                    continue;
                }

                // 以实际节点为准，不要求 CurrentRoomCount > 0。
                // 这正是飞行特效模式下与普通模式的关键差异。
                bool completed = await GrantInterestAsync(room);
                if (completed)
                {
                    _interestPending = false;
                    _interestRoom = null;
                    _interestScheduledRoom = null;
                    _interestSourceRoomId = null;
                    _interestTargetRoomId = null;
                    return;
                }

                Log.Debug(
                    $"[AutoChessTactics] 原生金币命令未完整成功，第 {attempt}/{maxAttempts} 次：" +
                    $"sourceRoomId={_interestSourceRoomId}，targetRoomId={_interestTargetRoomId}。");
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
        Log.Warn(
            $"[AutoChessTactics] 利息本轮最多重试 {maxAttempts} 次仍未完成，" +
            $"推迟到下一次安全房间进入：sourceRoomId={_interestSourceRoomId}，" +
            $"targetRoomId={_interestTargetRoomId}。");
    }

    /// <summary>
    /// 根据逻辑房间取得当前实际显示的房间节点。
    ///
    /// 这里不通过节点名称查找，避免不同场景/第三方 Mod 改名后失效；
    /// NRun 的强类型属性才是当前版本最稳定的入口。
    /// </summary>
    private static Node? GetRoomNode(NRun run, AbstractRoom room)
    {
        return room switch
        {
            CombatRoom => run.CombatRoom,
            TreasureRoom => run.TreasureRoom,
            EventRoom => run.EventRoom,
            RestSiteRoom => run.RestSiteRoom,
            MerchantRoom => run.MerchantRoom,
            _ => null,
        };
    }

    /// <summary>生成本局内稳定的房间实例 ID，只用于去重和诊断日志。</summary>
    private static string GetRoomId(AbstractRoom room)
    {
        return $"{room.GetType().FullName}:{RuntimeHelpers.GetHashCode(room):X8}";
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
                        $"[AutoChessTactics] 房间 {room.GetType().Name} 稳定后，玩家 {player.NetId} " +
                        $"利息结算成功：{goldBefore} -> {player.Gold}，金额 +{interest}，" +
                        $"sourceRoomId={_interestSourceRoomId}，targetRoomId={_interestTargetRoomId}。");
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
