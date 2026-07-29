using BetterGenshinImpact.GameTask.AutoQuest.Navigation;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoQuest.Navigation;

public class QuestMarkerFollowerTests
{
    [Fact]
    public void Options_DefaultNavigationRetryLimit_IsTen()
    {
        Assert.Equal(10, new QuestMarkerFollowerOptions().MaxNavigationRetries);
    }

    [Fact]
    public void Options_GoldenParticleGuidance_IsDisabledByDefault()
    {
        Assert.False(new QuestMarkerFollowerOptions().EnableGoldenParticleGuidance);
    }

    [Fact]
    public async Task FollowAsync_WhenMarkerIsLeft_RotatesLeft()
    {
        var fixture = CreateFixture(Marker(-100), arriveAfterObservations: 1);

        var result = await fixture.Follower.FollowAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.Arrived, result.Status);
        Assert.Single(fixture.Input.Rotations);
        Assert.True(fixture.Input.Rotations[0] < 0);
    }

    [Fact]
    public async Task FollowAsync_WhenMarkerIsRight_RotatesRight()
    {
        var fixture = CreateFixture(Marker(100), arriveAfterObservations: 1);

        var result = await fixture.Follower.FollowAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.Arrived, result.Status);
        Assert.Single(fixture.Input.Rotations);
        Assert.True(fixture.Input.Rotations[0] > 0);
    }

    [Fact]
    public async Task FollowAsync_WhenExperimentalParticlePathIsUsable_UsesParticleDirection()
    {
        var options = DefaultOptions() with
        {
            EnableGoldenParticleGuidance = true,
            ParticleStableFramesRequired = 1
        };
        var fixture = CreateFixture(
            Marker(-100),
            arriveAfterObservations: 1,
            options: options,
            particlePathRecognizer: new ConstantParticleRecognizer(Particle(120)));

        var result = await fixture.Follower.FollowAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.Arrived, result.Status);
        Assert.Single(fixture.Input.Rotations);
        Assert.True(fixture.Input.Rotations[0] > 0);
        Assert.Contains(true, fixture.Input.ForwardStates);
    }

    [Fact]
    public async Task FollowAsync_WhenExperimentalParticlePathIsMissing_FallsBackToMarker()
    {
        var options = DefaultOptions() with
        {
            EnableGoldenParticleGuidance = true,
            ParticleStableFramesRequired = 1
        };
        var fixture = CreateFixture(
            Marker(-100),
            arriveAfterObservations: 1,
            options: options,
            particlePathRecognizer: new ConstantParticleRecognizer(
                GoldenParticlePathDetection.NotFound));

        var result = await fixture.Follower.FollowAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.Arrived, result.Status);
        Assert.Single(fixture.Input.Rotations);
        Assert.True(fixture.Input.Rotations[0] < 0);
    }

    [Fact]
    public async Task FollowAsync_WhenParticleOffsetJumpsBetweenSides_NeverLetsParticleTakeOver()
    {
        var options = DefaultOptions() with
        {
            EnableGoldenParticleGuidance = true,
            ParticleStableFramesRequired = 2,
            ParticleMaximumFrameOffsetJumpPixels = 100
        };
        var fixture = CreateFixture(
            Marker(-100),
            arriveAfterObservations: 3,
            options: options,
            particlePathRecognizer: new SequenceParticleRecognizer(
                [Particle(400), Particle(-400), Particle(400)]));

        var result = await fixture.Follower.FollowAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.Arrived, result.Status);
        Assert.Equal(3, fixture.Input.Rotations.Count);
        Assert.All(fixture.Input.Rotations, rotation => Assert.True(rotation < 0));
    }

    [Fact]
    public async Task FollowAsync_WhenParticleGuidanceMovesAway_DisablesItAndFallsBackToMarker()
    {
        var options = DefaultOptions() with
        {
            EnableGoldenParticleGuidance = true,
            ParticleStableFramesRequired = 1,
            ParticleMaximumDistanceRegressionMeters = 5
        };
        var fixture = CreateFixture(
            Marker(-100),
            arriveAfterObservations: 3,
            options: options,
            particlePathRecognizer: new ConstantParticleRecognizer(Particle(0)),
            observationProvider: new SequenceDistanceObservationProvider([100, 100, 108, 108]));

        var result = await fixture.Follower.FollowAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.Arrived, result.Status);
        Assert.Contains(fixture.Input.Rotations, rotation => rotation < 0);
        Assert.Equal([true, false], fixture.Input.ForwardStates);
    }

    [Fact]
    public async Task FollowAsync_WhenMarkerIsFarFromCenter_ClampsRotationStep()
    {
        var options = DefaultOptions() with { MaximumRotationPixels = 18 };
        var fixture = CreateFixture(
            Marker(640),
            arriveAfterObservations: 1,
            options: options);

        var result = await fixture.Follower.FollowAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.Arrived, result.Status);
        Assert.Single(fixture.Input.Rotations);
        Assert.InRange(fixture.Input.Rotations[0], 1, options.MaximumRotationPixels);
    }

    [Fact]
    public async Task FollowAsync_WhenMarkerIsCentered_MovesForward()
    {
        var fixture = CreateFixture(Marker(0), arriveAfterObservations: 1);

        var result = await fixture.Follower.FollowAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.Arrived, result.Status);
        Assert.Contains(true, fixture.Input.ForwardStates);
        Assert.Equal(1, fixture.Input.ReleaseAllCount);
    }

    [Fact]
    public async Task FollowAsync_WhenMarkerIsFound_RefreshesQuestNavigation()
    {
        var fixture = CreateFixture(Marker(0), arriveAfterObservations: 1);

        var result = await fixture.Follower.FollowAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.Arrived, result.Status);
        Assert.Equal(2, fixture.Input.QuestNavigationPressCount);
    }

    [Fact]
    public async Task FollowAsync_WhenMarkerRemainsVisible_RefreshesAtConfiguredInterval()
    {
        var options = DefaultOptions() with { MarkerRefreshInterval = TimeSpan.Zero };
        var fixture = CreateFixture(Marker(0), arriveAfterObservations: 2, options: options);

        var result = await fixture.Follower.FollowAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.Arrived, result.Status);
        Assert.Equal(3, fixture.Input.QuestNavigationPressCount);
    }

    [Fact]
    public async Task FollowAsync_WhenMarkerIsReacquired_RefreshesQuestNavigationAgain()
    {
        var fixture = CreateFixture(
            [Marker(0), QuestMarkerDetection.NotFound, Marker(0)],
            arriveAfterObservations: 3,
            options: DefaultOptions() with { MarkerLostFramesBeforeRetry = 10 });

        var result = await fixture.Follower.FollowAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.Arrived, result.Status);
        Assert.Equal(3, fixture.Input.QuestNavigationPressCount);
    }

    [Fact]
    public async Task FollowAsync_WhenMarkerDisappears_ReleasesForwardImmediately()
    {
        var fixture = CreateFixture(
            [Marker(0), QuestMarkerDetection.NotFound, QuestMarkerDetection.NotFound],
            arriveAfterObservations: 2,
            options: DefaultOptions() with { MarkerLostFramesBeforeRetry = 10 });

        var result = await fixture.Follower.FollowAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.Arrived, result.Status);
        Assert.Equal([true, false], fixture.Input.ForwardStates);
    }

    [Fact]
    public async Task FollowAsync_WhenCancelled_ReleasesAllInput()
    {
        var fixture = CreateFixture(Marker(0), arriveAfterObservations: int.MaxValue);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await fixture.Follower.FollowAsync(Request(), cts.Token);

        Assert.Equal(NavigationStatus.Cancelled, result.Status);
        Assert.Equal(1, fixture.Input.ReleaseAllCount);
    }

    [Fact]
    public async Task FollowAsync_WhenStuck_TriggersExistingRecoveryAndReacquiresMarker()
    {
        var recovery = new RecordingRecovery(isStuck: true, recoverResult: true);
        var fixture = CreateFixture(Marker(0), arriveAfterObservations: 1, recovery: recovery);

        var result = await fixture.Follower.FollowAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.Arrived, result.Status);
        Assert.Equal(1, recovery.RecoverCount);
        Assert.True(fixture.Input.QuestNavigationPressCount >= 2);
        Assert.Equal([true, false], fixture.Input.ForwardStates);
    }

    [Fact]
    public async Task FollowAsync_WhenRecoveryFails_ReturnsNeedReplan()
    {
        var recovery = new RecordingRecovery(isStuck: true, recoverResult: false);
        var fixture = CreateFixture(Marker(0), arriveAfterObservations: int.MaxValue, recovery: recovery);

        var result = await fixture.Follower.FollowAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.NeedReplan, result.Status);
        Assert.Equal(1, recovery.RecoverCount);
        Assert.Equal(1, fixture.Input.ReleaseAllCount);
    }

    [Fact]
    public async Task FollowAsync_WhenMarkerNeverReturns_StopsAfterBoundedRetries()
    {
        var options = DefaultOptions() with
        {
            MarkerLostFramesBeforeRetry = 1,
            MaxNavigationRetries = 2,
            MaxIterations = 100
        };
        var fixture = CreateFixture(
            QuestMarkerDetection.NotFound,
            arriveAfterObservations: int.MaxValue,
            options: options);

        var result = await fixture.Follower.FollowAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.NeedReplan, result.Status);
        Assert.Equal(3, fixture.FrameProvider.CaptureCount);
        Assert.Equal(3, fixture.Input.QuestNavigationPressCount);
    }

    [Fact]
    public async Task FollowAsync_WhenTurnOffsetNeverImproves_StopsAfterBoundedRetries()
    {
        var options = DefaultOptions() with
        {
            MaxTurnFramesWithoutProgress = 2,
            MaxNavigationRetries = 1,
            MaxIterations = 100
        };
        var fixture = CreateFixture(
            Marker(200),
            arriveAfterObservations: int.MaxValue,
            options: options);

        var result = await fixture.Follower.FollowAsync(Request(), CancellationToken.None);

        Assert.Equal(NavigationStatus.NeedReplan, result.Status);
        Assert.Equal(3, fixture.Input.QuestNavigationPressCount);
        Assert.True(fixture.Input.Rotations.Count < options.MaxIterations);
        Assert.Equal(1, fixture.Input.ReleaseAllCount);
    }

    private static FollowerFixture CreateFixture(
        QuestMarkerDetection marker,
        int arriveAfterObservations,
        RecordingRecovery? recovery = null,
        QuestMarkerFollowerOptions? options = null,
        IGoldenParticlePathRecognizer? particlePathRecognizer = null,
        IQuestArrivalObservationProvider? observationProvider = null) =>
        CreateFixture(
            [marker],
            arriveAfterObservations,
            recovery,
            options,
            particlePathRecognizer,
            observationProvider);

    private static FollowerFixture CreateFixture(
        IReadOnlyList<QuestMarkerDetection> markers,
        int arriveAfterObservations,
        RecordingRecovery? recovery = null,
        QuestMarkerFollowerOptions? options = null,
        IGoldenParticlePathRecognizer? particlePathRecognizer = null,
        IQuestArrivalObservationProvider? observationProvider = null)
    {
        var input = new RecordingInput();
        var frameProvider = new RecordingFrameProvider();
        var actualRecovery = recovery ?? new RecordingRecovery(false, true);
        var follower = new QuestMarkerFollower(
            new SequenceRecognizer(markers),
            frameProvider,
            input,
            observationProvider ?? new ObservationProvider(),
            new ArriveAfterVerifier(arriveAfterObservations),
            new ConstantPositionProvider(),
            new RecoveryFactory(actualRecovery),
            options ?? DefaultOptions(),
            logger: NullLogger<QuestMarkerFollower>.Instance,
            particlePathRecognizer: particlePathRecognizer);
        return new FollowerFixture(follower, input, frameProvider);
    }

    private static QuestMarkerFollowerOptions DefaultOptions() => new()
    {
        LoopInterval = TimeSpan.Zero,
        MaxIterations = 10,
        MaxFollowDuration = TimeSpan.FromMinutes(1),
        MinimumMarkerConfidence = 0.5f
    };

    private static QuestMarkerFollowRequest Request() => new("Teyvat", "TemplateMatch");

    private static QuestMarkerDetection Marker(float offsetX) =>
        new(true, new Rect(0, 0, 20, 20), offsetX, 0, 1);

    private static GoldenParticlePathDetection Particle(float offsetX) =>
        new(true, new Rect(0, 0, 40, 200), offsetX, 1, 12, 200);

    private sealed record FollowerFixture(
        QuestMarkerFollower Follower,
        RecordingInput Input,
        RecordingFrameProvider FrameProvider);

    private sealed class SequenceRecognizer(IReadOnlyList<QuestMarkerDetection> markers) : IQuestMarkerRecognizer
    {
        private int _index;

        public QuestMarkerDetection Detect(ImageRegion frame)
        {
            var index = Math.Min(_index, markers.Count - 1);
            _index++;
            return markers[index];
        }
    }

    private sealed class ConstantParticleRecognizer(GoldenParticlePathDetection detection)
        : IGoldenParticlePathRecognizer
    {
        public GoldenParticlePathDetection Detect(ImageRegion frame) => detection;
    }

    private sealed class SequenceParticleRecognizer(
        IReadOnlyList<GoldenParticlePathDetection> detections) : IGoldenParticlePathRecognizer
    {
        private int _index;

        public GoldenParticlePathDetection Detect(ImageRegion frame)
        {
            var index = Math.Min(_index, detections.Count - 1);
            _index++;
            return detections[index];
        }
    }

    private sealed class RecordingFrameProvider : IQuestNavigationFrameProvider
    {
        public int CaptureCount { get; private set; }

        public ImageRegion Capture()
        {
            CaptureCount++;
            return new ImageRegion(new Mat(16, 16, MatType.CV_8UC3, Scalar.Black), 0, 0);
        }
    }

    private sealed class RecordingInput : IQuestNavigationInput
    {
        private bool _forwardDown;

        public List<int> Rotations { get; } = [];

        public List<bool> ForwardStates { get; } = [];

        public int QuestNavigationPressCount { get; private set; }

        public int ReleaseAllCount { get; private set; }

        public void RotateHorizontal(int deltaX) => Rotations.Add(deltaX);

        public void SetMoveForward(bool isDown)
        {
            if (_forwardDown == isDown)
            {
                return;
            }

            _forwardDown = isDown;
            ForwardStates.Add(isDown);
        }

        public void PressQuestNavigation() => QuestNavigationPressCount++;

        public void ReleaseAll()
        {
            ReleaseAllCount++;
            _forwardDown = false;
        }
    }

    private sealed class ObservationProvider : IQuestArrivalObservationProvider
    {
        public ArrivalObservation Observe(ImageRegion frame, QuestMarkerDetection marker) =>
            new(DateTimeOffset.UtcNow, null, marker.Found, false, false, false);
    }

    private sealed class SequenceDistanceObservationProvider(IReadOnlyList<int> distances)
        : IQuestArrivalObservationProvider
    {
        private int _index;

        public ArrivalObservation Observe(ImageRegion frame, QuestMarkerDetection marker)
        {
            var index = Math.Min(_index, distances.Count - 1);
            _index++;
            return new ArrivalObservation(
                DateTimeOffset.UtcNow,
                distances[index],
                marker.Found,
                false,
                false,
                false);
        }
    }

    private sealed class ArriveAfterVerifier(int observationsBeforeArrival) : IArrivalVerifier
    {
        private int _observations;

        public ArrivalVerificationResult Verify(ArrivalObservation observation)
        {
            _observations++;
            return _observations > observationsBeforeArrival
                ? new ArrivalVerificationResult(true, false, "test-arrived")
                : new ArrivalVerificationResult(false, false, "test-running");
        }

        public void Reset() => _observations = 0;
    }

    private sealed class ConstantPositionProvider : IQuestPositionProvider
    {
        public Point2f? GetPosition(ImageRegion frame, string mapName, string mapMatchMethod) => new Point2f(10, 10);
    }

    private sealed class RecoveryFactory(RecordingRecovery recovery) : IQuestNavigationRecoveryFactory
    {
        public IQuestNavigationRecovery Create(CancellationToken ct, int maxAttempts) => recovery;
    }

    private sealed class RecordingRecovery(bool isStuck, bool recoverResult) : IQuestNavigationRecovery
    {
        public int Attempts { get; private set; }

        public int RecoverCount { get; private set; }

        public bool IsStuck(Point2f position) => isStuck && RecoverCount == 0;

        public Task<bool> RecoverAsync(CancellationToken ct)
        {
            RecoverCount++;
            Attempts++;
            return Task.FromResult(recoverResult);
        }

        public void ResetObservation()
        {
        }
    }
}
