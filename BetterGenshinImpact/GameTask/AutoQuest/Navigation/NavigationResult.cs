using BetterGenshinImpact.GameTask.AutoPathing.Telemetry;

namespace BetterGenshinImpact.GameTask.AutoQuest.Navigation;

/// <summary>
/// 单次导航的结束状态。调用方根据状态决定是否重规划、暂停或继续后续任务逻辑。
/// </summary>
public enum NavigationStatus
{
    Arrived,
    NeedReplan,
    HumanRequired,
    Cancelled,
    Failed
}

/// <summary>
/// 混合导航输入。坐标使用 RouteNavigationPlanner 的地图图片坐标系。
/// </summary>
public sealed record HybridNavigationRequest(
    RouteGraphPoint CurrentImagePoint,
    RouteGraphPoint TargetImagePoint,
    string MapName = "Teyvat",
    string? MapMatchMethod = null,
    string? TaskName = null);

/// <summary>
/// 任务标记跟随输入。
/// </summary>
public sealed record QuestMarkerFollowRequest(
    string MapName,
    string MapMatchMethod);

/// <summary>
/// 单次导航结果。除最终状态外，保留本次是否走过路网及恢复次数，便于上层决策和日志记录。
/// </summary>
public sealed record NavigationResult
{
    public required NavigationStatus Status { get; init; }

    public string Message { get; init; } = string.Empty;

    public bool UsedGraphRoute { get; init; }

    public int GraphWaypointCount { get; init; }

    public int RecoveryAttempts { get; init; }

    public bool IsSuccess => Status == NavigationStatus.Arrived;

    public static NavigationResult Arrived(string message = "已确认到达任务目标") => new()
    {
        Status = NavigationStatus.Arrived,
        Message = message
    };

    public static NavigationResult NeedReplan(string message, int recoveryAttempts = 0) => new()
    {
        Status = NavigationStatus.NeedReplan,
        Message = message,
        RecoveryAttempts = recoveryAttempts
    };

    public static NavigationResult HumanRequired(string message, int recoveryAttempts = 0) => new()
    {
        Status = NavigationStatus.HumanRequired,
        Message = message,
        RecoveryAttempts = recoveryAttempts
    };

    public static NavigationResult Cancelled(string message = "导航已取消") => new()
    {
        Status = NavigationStatus.Cancelled,
        Message = message
    };

    public static NavigationResult Failed(string message) => new()
    {
        Status = NavigationStatus.Failed,
        Message = message
    };
}
