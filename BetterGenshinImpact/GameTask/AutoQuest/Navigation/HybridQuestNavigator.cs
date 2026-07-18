using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using BetterGenshinImpact.GameTask.AutoPathing.Telemetry;
using BetterGenshinImpact.GameTask.Common.Map.Maps;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoQuest.Navigation;

public interface IQuestRoutePlanner
{
    bool TryPlan(HybridNavigationRequest request, out RouteNavigationPlan plan);
}

/// <summary>
/// 将任务目标适配到 PR #2978 的路网规划器。未知起点连接不允许直走，找不到入口时交给任务标记跟随兜底；
/// 未知目标连接只用于选择最近前沿节点，实际执行任务会在前沿节点结束。
/// </summary>
public sealed class QuestRoutePlanner : IQuestRoutePlanner
{
    private readonly RouteNavigationPlanner _planner;

    public QuestRoutePlanner(RouteNavigationPlanner? planner = null)
    {
        _planner = planner ?? new RouteNavigationPlanner();
    }

    public bool TryPlan(HybridNavigationRequest request, out RouteNavigationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _planner.TryPlan(
            new RouteNavigationPlanRequest
            {
                MapName = request.MapName,
                CurrentImagePoint = request.CurrentImagePoint,
                TargetImagePoint = request.TargetImagePoint,
                MapMatchMethod = request.MapMatchMethod,
                TaskName = string.IsNullOrWhiteSpace(request.TaskName)
                    ? "任务目标混合导航临时路线"
                    : request.TaskName
            },
            out plan,
            new RouteNavigationPlanOptions
            {
                AllowTeleport = true,
                AllowDisabledEdges = false,
                AllowUnknownStartConnector = false,
                AllowUnknownTargetConnector = true
            });
    }
}

public sealed record QuestRouteExecutionResult(bool Succeeded, string Message)
{
    public static QuestRouteExecutionResult Success { get; } = new(true, string.Empty);

    public static QuestRouteExecutionResult Failed(string message) => new(false, message);
}

public interface IQuestRouteExecutor
{
    Task<QuestRouteExecutionResult> ExecuteAsync(PathingTask task, CancellationToken ct);
}

/// <summary>
/// 使用现有 PathExecutor 执行路网路线。PathExecutor 内部复用 PathingMovementController、
/// PathingAnomalyResolver、StuckDetector 和 TrapEscaper。
/// </summary>
public sealed class QuestRouteExecutor : IQuestRouteExecutor
{
    public async Task<QuestRouteExecutionResult> ExecuteAsync(PathingTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        try
        {
            var executor = new PathExecutor(ct)
            {
                PartyConfig = new PathingPartyConfig
                {
                    AutoFightEnabled = false,
                    AutoPickEnabled = false,
                    AutoSkipEnabled = true
                }
            };
            await executor.Pathing(task);
            return executor.SuccessEnd
                ? QuestRouteExecutionResult.Success
                : QuestRouteExecutionResult.Failed("PathExecutor 未完整走完路网路线");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return QuestRouteExecutionResult.Failed(ex.Message);
        }
    }
}

/// <summary>
/// 第一版混合导航核心：优先走历史路网到目标附近前沿节点，再自动切换任务标记跟随。
/// 不承担任务识别、连续阶段、剧情战斗、黄色引导或流程文件加载。
/// </summary>
public sealed class HybridQuestNavigator
{
    private readonly IQuestRoutePlanner _routePlanner;
    private readonly IQuestRouteExecutor _routeExecutor;
    private readonly IQuestMarkerFollower _markerFollower;
    private readonly ILogger<HybridQuestNavigator> _logger;

    public HybridQuestNavigator()
        : this(new QuestRoutePlanner(), new QuestRouteExecutor(), new QuestMarkerFollower())
    {
    }

    public HybridQuestNavigator(
        IQuestRoutePlanner routePlanner,
        IQuestRouteExecutor routeExecutor,
        IQuestMarkerFollower markerFollower,
        ILogger<HybridQuestNavigator>? logger = null)
    {
        _routePlanner = routePlanner ?? throw new ArgumentNullException(nameof(routePlanner));
        _routeExecutor = routeExecutor ?? throw new ArgumentNullException(nameof(routeExecutor));
        _markerFollower = markerFollower ?? throw new ArgumentNullException(nameof(markerFollower));
        _logger = logger ?? App.GetLogger<HybridQuestNavigator>();
    }

    public async Task<NavigationResult> NavigateAsync(HybridNavigationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var usedGraphRoute = false;
        var graphWaypointCount = 0;
        var mapMatchMethod = request.MapMatchMethod ?? "TemplateMatch";

        try
        {
            ct.ThrowIfCancellationRequested();

            if (_routePlanner.TryPlan(request, out var plan))
            {
                mapMatchMethod = plan.Task?.Info.MapMatchMethod ?? mapMatchMethod;
                var frontierTask = BuildFrontierTask(plan);
                if (frontierTask is { Positions.Count: >= 2 })
                {
                    graphWaypointCount = frontierTask.Positions.Count;
                    _logger.LogInformation(
                        "[混合任务导航] 开始执行路网路线，共 {WaypointCount} 个点，前沿节点 {FrontierNode}",
                        graphWaypointCount,
                        plan.FrontierNode?.NodeId ?? "-");
                    var execution = await _routeExecutor.ExecuteAsync(frontierTask, ct);
                    if (!execution.Succeeded)
                    {
                        _logger.LogWarning(
                            "[混合任务导航] 路网路线执行失败，返回重规划：{Reason}",
                            execution.Message);
                        return NavigationResult.NeedReplan($"路网路线执行失败：{execution.Message}") with
                        {
                            UsedGraphRoute = true,
                            GraphWaypointCount = graphWaypointCount
                        };
                    }

                    usedGraphRoute = true;
                    _logger.LogInformation("[混合任务导航] 路网路线执行结束，切换任务标记跟随");
                }
                else
                {
                    _logger.LogInformation("[混合任务导航] 当前点已接近路网前沿，直接切换任务标记跟随");
                }
            }
            else
            {
                _logger.LogInformation(
                    "[混合任务导航] 路网规划失败，切换任务标记跟随：{Reason}",
                    plan.FailureReason);
            }

            ct.ThrowIfCancellationRequested();
            var followResult = await _markerFollower.FollowAsync(
                new QuestMarkerFollowRequest(request.MapName, mapMatchMethod),
                ct);
            _logger.LogInformation(
                "[混合任务导航] 导航结束，状态 {Status}，原因：{Reason}",
                followResult.Status,
                followResult.Message);
            return followResult with
            {
                UsedGraphRoute = usedGraphRoute,
                GraphWaypointCount = graphWaypointCount
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return NavigationResult.Cancelled() with
            {
                UsedGraphRoute = usedGraphRoute,
                GraphWaypointCount = graphWaypointCount
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "混合任务导航异常结束");
            return NavigationResult.Failed(ex.Message) with
            {
                UsedGraphRoute = usedGraphRoute,
                GraphWaypointCount = graphWaypointCount
            };
        }
    }

    private static PathingTask? BuildFrontierTask(RouteNavigationPlan plan)
    {
        if (plan.Task == null || plan.FrontierNode == null)
        {
            return null;
        }

        var task = PathingTask.BuildFromJson(plan.Task.ToJsonString());
        while (task.Positions.Count > 0 && task.Positions[^1].Type == WaypointType.Target.Code)
        {
            task.Positions.RemoveAt(task.Positions.Count - 1);
        }

        // 有图边时，规划器已经把最后一条边的终点（即 FrontierNode）写入 PathingTask。
        // 删除未知目标连接点后可以直接执行，无需再次做坐标转换。
        if (task.Positions.Count >= 2)
        {
            task.Positions[^1].Type = WaypointType.Target.Code;
            return task;
        }

        var map = MapManager.GetMap(task.Info.MapName, task.Info.MapMatchMethod);
        var frontierGamePoint = map?.ConvertImageCoordinatesToGenshinMapCoordinates(
            new Point2f((float)plan.FrontierNode.X, (float)plan.FrontierNode.Y));
        if (frontierGamePoint is not { } frontier)
        {
            return task.Positions.Count >= 2 ? task : null;
        }

        if (task.Positions.Count == 0 || Distance(task.Positions[^1], frontier) > 1)
        {
            task.Positions.Add(new Waypoint
            {
                X = frontier.X,
                Y = frontier.Y,
                Type = WaypointType.Target.Code,
                MoveMode = task.Positions.LastOrDefault()?.MoveMode ?? MoveModeEnum.Walk.Code
            });
        }
        else
        {
            task.Positions[^1].Type = WaypointType.Target.Code;
        }

        return task.Positions.Count >= 2 ? task : null;
    }

    private static double Distance(Waypoint waypoint, Point2f point)
    {
        var dx = waypoint.X - point.X;
        var dy = waypoint.Y - point.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
