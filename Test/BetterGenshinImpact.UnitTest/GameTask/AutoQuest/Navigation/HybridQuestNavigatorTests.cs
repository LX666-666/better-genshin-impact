using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using BetterGenshinImpact.GameTask.AutoPathing.Telemetry;
using BetterGenshinImpact.GameTask.AutoQuest.Navigation;
using BetterGenshinImpact.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoQuest.Navigation;

public class HybridQuestNavigatorTests
{
    public HybridQuestNavigatorTests()
    {
        _ = new ConfigService().Get();
    }

    [Fact]
    public async Task NavigateAsync_WhenGraphPlanningFails_FallsBackToMarkerFollower()
    {
        var planner = new StubPlanner(false, RouteNavigationPlan.Failed("no graph"));
        var executor = new RecordingExecutor(QuestRouteExecutionResult.Success);
        var follower = new RecordingFollower(NavigationResult.Arrived());
        var navigator = CreateNavigator(planner, executor, follower);

        var result = await navigator.NavigateAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.Arrived, result.Status);
        Assert.Equal(0, executor.ExecutionCount);
        Assert.Equal(1, follower.FollowCount);
        Assert.False(result.UsedGraphRoute);
    }

    [Fact]
    public async Task NavigateAsync_WhenGraphRouteCompletes_AutomaticallyHandsOffToMarkerFollower()
    {
        var plan = SuccessfulPlan();
        var planner = new StubPlanner(true, plan);
        var executor = new RecordingExecutor(QuestRouteExecutionResult.Success);
        var follower = new RecordingFollower(NavigationResult.Arrived());
        var navigator = CreateNavigator(planner, executor, follower);

        var result = await navigator.NavigateAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.Arrived, result.Status);
        Assert.Equal(1, executor.ExecutionCount);
        Assert.Equal(1, follower.FollowCount);
        Assert.True(result.UsedGraphRoute);
        Assert.Equal(2, result.GraphWaypointCount);
        Assert.NotNull(executor.LastTask);
        Assert.Equal(20, executor.LastTask!.Positions[^1].X);
        Assert.Equal(WaypointType.Target.Code, executor.LastTask.Positions[^1].Type);
    }

    [Fact]
    public async Task NavigateAsync_WhenGraphExecutionFails_ReturnsNeedReplanWithoutBlindFallback()
    {
        var planner = new StubPlanner(true, SuccessfulPlan());
        var executor = new RecordingExecutor(QuestRouteExecutionResult.Failed("stuck"));
        var follower = new RecordingFollower(NavigationResult.Arrived());
        var navigator = CreateNavigator(planner, executor, follower);

        var result = await navigator.NavigateAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.NeedReplan, result.Status);
        Assert.Equal(1, executor.ExecutionCount);
        Assert.Equal(0, follower.FollowCount);
    }

    private static HybridQuestNavigator CreateNavigator(
        IQuestRoutePlanner planner,
        IQuestRouteExecutor executor,
        IQuestMarkerFollower follower) =>
        new(planner, executor, follower, NullLogger<HybridQuestNavigator>.Instance);

    private static HybridNavigationRequest Request() =>
        new(new RouteGraphPoint(0, 0), new RouteGraphPoint(100, 100));

    private static RouteNavigationPlan SuccessfulPlan()
    {
        var task = new PathingTask
        {
            Info = new PathingTaskInfo
            {
                Name = "test",
                MapName = "Teyvat",
                MapMatchMethod = "TemplateMatch"
            },
            Positions =
            [
                new Waypoint { X = 10, Y = 10, Type = WaypointType.Path.Code, MoveMode = MoveModeEnum.Walk.Code },
                new Waypoint { X = 20, Y = 20, Type = WaypointType.Path.Code, MoveMode = MoveModeEnum.Walk.Code },
                new Waypoint { X = 99, Y = 99, Type = WaypointType.Target.Code, MoveMode = MoveModeEnum.Walk.Code }
            ]
        };

        return new RouteNavigationPlan
        {
            Succeeded = true,
            Task = task,
            FrontierNode = new RouteNavigationNode
            {
                NodeId = "frontier",
                MapName = "Teyvat",
                X = 20,
                Y = 20
            }
        };
    }

    private sealed class StubPlanner(bool succeeds, RouteNavigationPlan plan) : IQuestRoutePlanner
    {
        public bool TryPlan(HybridNavigationRequest request, out RouteNavigationPlan result)
        {
            result = plan;
            return succeeds;
        }
    }

    private sealed class RecordingExecutor(QuestRouteExecutionResult result) : IQuestRouteExecutor
    {
        public int ExecutionCount { get; private set; }

        public PathingTask? LastTask { get; private set; }

        public Task<QuestRouteExecutionResult> ExecuteAsync(PathingTask task, CancellationToken ct)
        {
            ExecutionCount++;
            LastTask = task;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingFollower(NavigationResult result) : IQuestMarkerFollower
    {
        public int FollowCount { get; private set; }

        public Task<NavigationResult> FollowAsync(QuestMarkerFollowRequest request, CancellationToken ct)
        {
            FollowCount++;
            return Task.FromResult(result);
        }
    }
}
