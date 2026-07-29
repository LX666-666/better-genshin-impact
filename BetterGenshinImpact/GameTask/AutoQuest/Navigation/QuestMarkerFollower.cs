using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Movement;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.WindowsInput;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoQuest.Navigation;

public sealed record QuestMarkerFollowerOptions
{
    /// <summary>
    /// 实验开关，默认关闭。关闭时完全沿用任务图标跟随路径。
    /// </summary>
    public bool EnableGoldenParticleGuidance { get; init; }

    public int HorizontalDeadZonePixels { get; init; } = 40;

    public int ParticleHorizontalDeadZonePixels { get; init; } = 32;

    public double RotationDivisor { get; init; } = 32;

    public double ParticleRotationDivisor { get; init; } = 12;

    public int MinimumRotationPixels { get; init; } = 2;

    public int MaximumRotationPixels { get; init; } = 18;

    public int ParticleMaximumRotationPixels { get; init; } = 24;

    public int ParticleMoveWhileTurningLimitPixels { get; init; } = 260;

    public float MinimumMarkerConfidence { get; init; } = 0.6f;

    public float MinimumParticleConfidence { get; init; } = 0.6f;

    public int ParticleStableFramesRequired { get; init; } = 3;

    public float ParticleOffsetSmoothingFactor { get; init; } = 0.35f;

    public float ParticleMaximumFrameOffsetJumpPixels { get; init; } = 180;

    public int FinalApproachDistanceMeters { get; init; } = 12;

    public TimeSpan ParticleFallbackDuration { get; init; } = TimeSpan.FromSeconds(5);

    public int MaxParticleFallbacks { get; init; } = 3;

    public int ParticleMaximumDistanceRegressionMeters { get; init; } = 10;

    public int MarkerLostFramesBeforeRetry { get; init; } = 8;

    public TimeSpan MarkerRefreshInterval { get; init; } = TimeSpan.FromSeconds(5);

    public int MaxNavigationRetries { get; init; } = 10;

    public int MaxTurnFramesWithoutProgress { get; init; } = 30;

    public float TurnProgressEpsilonPixels { get; init; } = 4;

    public int MaxRecoveryAttempts { get; init; } = 3;

    public int MaxIterations { get; init; } = 3600;

    public TimeSpan MaxFollowDuration { get; init; } = TimeSpan.FromMinutes(6);

    public TimeSpan LoopInterval { get; init; } = TimeSpan.FromMilliseconds(100);
}

public interface IQuestMarkerFollower
{
    Task<NavigationResult> FollowAsync(QuestMarkerFollowRequest request, CancellationToken ct);
}

public interface IQuestNavigationFrameProvider
{
    ImageRegion Capture();
}

public sealed class QuestNavigationFrameProvider : IQuestNavigationFrameProvider
{
    public ImageRegion Capture() => TaskControl.CaptureToRectArea();
}

public interface IQuestNavigationInput
{
    void RotateHorizontal(int deltaX);

    void SetMoveForward(bool isDown);

    void PressQuestNavigation();

    void ReleaseAll();
}

/// <summary>
/// 任务标记导航的唯一输入出口。ReleaseAll 无条件释放所有可能参与导航的按键。
/// </summary>
public sealed class SimulationQuestNavigationInput : IQuestNavigationInput
{
    private static readonly GIActions[] ReleasableActions =
    [
        GIActions.MoveForward,
        GIActions.MoveBackward,
        GIActions.MoveLeft,
        GIActions.MoveRight,
        GIActions.SprintKeyboard,
        GIActions.SprintMouse,
        GIActions.Jump
    ];

    private readonly ILogger<SimulationQuestNavigationInput> _logger;
    private bool _forwardDown;

    public SimulationQuestNavigationInput(ILogger<SimulationQuestNavigationInput>? logger = null)
    {
        _logger = logger ?? App.GetLogger<SimulationQuestNavigationInput>();
    }

    public void RotateHorizontal(int deltaX)
    {
        if (deltaX != 0)
        {
            Simulation.SendInput.Mouse.MoveMouseBy(deltaX, 0);
        }
    }

    public void SetMoveForward(bool isDown)
    {
        if (_forwardDown == isDown)
        {
            return;
        }

        Simulation.SendInput.SimulateAction(
            GIActions.MoveForward,
            isDown ? KeyType.KeyDown : KeyType.KeyUp);
        _forwardDown = isDown;
    }

    public void PressQuestNavigation()
    {
        Simulation.SendInput.SimulateAction(GIActions.QuestNavigation);
    }

    public void ReleaseAll()
    {
        foreach (var action in ReleasableActions)
        {
            try
            {
                Simulation.SendInput.SimulateAction(action, KeyType.KeyUp);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "释放导航输入 {Action} 失败", action);
            }
        }

        _forwardDown = false;
    }
}

public interface IQuestPositionProvider
{
    Point2f? GetPosition(ImageRegion frame, string mapName, string mapMatchMethod);
}

public sealed class QuestPositionProvider : IQuestPositionProvider
{
    private readonly ILogger<QuestPositionProvider> _logger;

    public QuestPositionProvider(ILogger<QuestPositionProvider>? logger = null)
    {
        _logger = logger ?? App.GetLogger<QuestPositionProvider>();
    }

    public Point2f? GetPosition(ImageRegion frame, string mapName, string mapMatchMethod)
    {
        try
        {
            var position = BetterGenshinImpact.GameTask.AutoPathing.Navigation.GetPosition(frame, mapName, mapMatchMethod);
            return float.IsNaN(position.X) || float.IsNaN(position.Y) ? null : position;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "任务标记跟随期间获取地图坐标失败");
            return null;
        }
    }
}

public interface IQuestNavigationRecovery
{
    int Attempts { get; }

    bool IsStuck(Point2f position);

    Task<bool> RecoverAsync(CancellationToken ct);

    void ResetObservation();
}

public interface IQuestNavigationRecoveryFactory
{
    IQuestNavigationRecovery Create(CancellationToken ct, int maxAttempts);
}

/// <summary>
/// 复用 PR #2978 的坐标卡死检测和 TrapEscaper，不实现另一套随机 WASD 脱困。
/// </summary>
public sealed class ExistingPathingQuestNavigationRecovery : IQuestNavigationRecovery
{
    private readonly StuckDetector _stuckDetector;
    private readonly TrapEscaper _trapEscaper;
    private readonly int _maxAttempts;
    private readonly ILogger<ExistingPathingQuestNavigationRecovery> _logger;

    public ExistingPathingQuestNavigationRecovery(
        CancellationToken ct,
        int maxAttempts,
        StuckDetector? stuckDetector = null,
        TrapEscaper? trapEscaper = null,
        ILogger<ExistingPathingQuestNavigationRecovery>? logger = null)
    {
        _maxAttempts = Math.Max(1, maxAttempts);
        _stuckDetector = stuckDetector ?? new StuckDetector();
        _trapEscaper = trapEscaper ?? new TrapEscaper(ct);
        _logger = logger ?? App.GetLogger<ExistingPathingQuestNavigationRecovery>();
    }

    public int Attempts { get; private set; }

    public bool IsStuck(Point2f position) => _stuckDetector.CheckStuck(position, 0);

    public async Task<bool> RecoverAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Attempts++;
        if (Attempts > _maxAttempts)
        {
            return false;
        }

        try
        {
            _logger.LogWarning("[任务标记导航] 检测到卡死，执行现有 TrapEscaper，第 {Attempt}/{MaxAttempts} 次", Attempts, _maxAttempts);
            await _trapEscaper.RotateAndMove();
            _stuckDetector.ClearQueue();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[任务标记导航] TrapEscaper 脱困失败");
            return false;
        }
    }

    public void ResetObservation()
    {
        _stuckDetector.ClearQueue();
    }
}

public sealed class ExistingPathingQuestNavigationRecoveryFactory : IQuestNavigationRecoveryFactory
{
    public IQuestNavigationRecovery Create(CancellationToken ct, int maxAttempts) =>
        new ExistingPathingQuestNavigationRecovery(ct, maxAttempts);
}

/// <summary>
/// 根据任务导航标记旋转镜头并前进。标记丢失、取消、异常和正常结束都会统一释放输入。
/// </summary>
public sealed class QuestMarkerFollower : IQuestMarkerFollower
{
    private readonly IQuestMarkerRecognizer _recognizer;
    private readonly IQuestNavigationFrameProvider _frameProvider;
    private readonly IQuestNavigationInput _input;
    private readonly IQuestArrivalObservationProvider _arrivalObservationProvider;
    private readonly IArrivalVerifier _arrivalVerifier;
    private readonly IQuestPositionProvider _positionProvider;
    private readonly IQuestNavigationRecoveryFactory _recoveryFactory;
    private readonly IGoldenParticlePathRecognizer _particlePathRecognizer;
    private readonly QuestMarkerFollowerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<QuestMarkerFollower> _logger;

    public QuestMarkerFollower(QuestMarkerFollowerOptions? options = null)
        : this(QuestMarkerTemplateProfile.TrackedTarget, options)
    {
    }

    public QuestMarkerFollower(
        QuestMarkerTemplateProfile templateProfile,
        QuestMarkerFollowerOptions? options = null)
        : this(
            new QuestMarkerRecognizer(new QuestMarkerTemplateProvider(templateProfile)),
            new QuestNavigationFrameProvider(),
            new SimulationQuestNavigationInput(),
            new QuestArrivalObservationProvider(),
            new ArrivalVerifier(),
            new QuestPositionProvider(),
            new ExistingPathingQuestNavigationRecoveryFactory(),
            options)
    {
    }

    public QuestMarkerFollower(
        IQuestMarkerRecognizer recognizer,
        IQuestNavigationFrameProvider frameProvider,
        IQuestNavigationInput input,
        IQuestArrivalObservationProvider arrivalObservationProvider,
        IArrivalVerifier arrivalVerifier,
        IQuestPositionProvider positionProvider,
        IQuestNavigationRecoveryFactory recoveryFactory,
        QuestMarkerFollowerOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger<QuestMarkerFollower>? logger = null,
        IGoldenParticlePathRecognizer? particlePathRecognizer = null)
    {
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
        _frameProvider = frameProvider ?? throw new ArgumentNullException(nameof(frameProvider));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _arrivalObservationProvider = arrivalObservationProvider ?? throw new ArgumentNullException(nameof(arrivalObservationProvider));
        _arrivalVerifier = arrivalVerifier ?? throw new ArgumentNullException(nameof(arrivalVerifier));
        _positionProvider = positionProvider ?? throw new ArgumentNullException(nameof(positionProvider));
        _recoveryFactory = recoveryFactory ?? throw new ArgumentNullException(nameof(recoveryFactory));
        _options = options ?? new QuestMarkerFollowerOptions();
        _particlePathRecognizer = particlePathRecognizer ?? new GoldenParticlePathRecognizer();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? App.GetLogger<QuestMarkerFollower>();
    }

    public async Task<NavigationResult> FollowAsync(QuestMarkerFollowRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var recovery = _recoveryFactory.Create(ct, _options.MaxRecoveryAttempts);
        var startedAt = _timeProvider.GetUtcNow();
        var markerLostFrames = 0;
        var navigationRetries = 0;
        var iterations = 0;
        var turnFramesWithoutProgress = 0;
        var bestTurnOffset = float.MaxValue;
        var markerWasUsable = false;
        var lastMarkerRefreshAt = DateTimeOffset.MinValue;
        var particleStableFrames = 0;
        var smoothedParticleOffset = 0f;
        float? lastParticleCandidateOffset = null;
        var particleSuppressedUntil = DateTimeOffset.MinValue;
        var particleFallbacks = 0;
        int? bestDistanceDuringParticleGuidance = null;
        var lastSteeringSource = QuestSteeringSource.Marker;
        _arrivalVerifier.Reset();

        try
        {
            ct.ThrowIfCancellationRequested();
            _input.PressQuestNavigation();

            while (iterations++ < _options.MaxIterations &&
                   _timeProvider.GetUtcNow() - startedAt < _options.MaxFollowDuration)
            {
                ct.ThrowIfCancellationRequested();
                using var frame = _frameProvider.Capture();
                var marker = _recognizer.Detect(frame);
                var markerUsable = marker.Found && marker.Confidence >= _options.MinimumMarkerConfidence;
                if (!markerUsable && marker.Found)
                {
                    marker = marker with { Found = false };
                }

                var observation = _arrivalObservationProvider.Observe(frame, marker);
                var particle = _options.EnableGoldenParticleGuidance
                    ? _particlePathRecognizer.Detect(frame)
                    : GoldenParticlePathDetection.NotFound;
                if (iterations == 1 || iterations % 10 == 0)
                {
                    _logger.LogDebug(
                        "[任务标记导航] 识别状态 Found={Found}, Confidence={Confidence:F3}, Bounds=({X},{Y},{Width},{Height}), Offset=({OffsetX:F1},{OffsetY:F1}), Distance={Distance}, Interaction={Interaction}, Talk={Talk}",
                        marker.Found,
                        marker.Confidence,
                        marker.Bounds.X,
                        marker.Bounds.Y,
                        marker.Bounds.Width,
                        marker.Bounds.Height,
                        marker.ScreenOffsetX,
                        marker.ScreenOffsetY,
                        observation.DistanceMeters,
                        observation.HasInteractionPrompt,
                        observation.IsInTalkUi);
                    if (_options.EnableGoldenParticleGuidance)
                    {
                        _logger.LogDebug(
                            "[金色粒子导航] Found={Found}, Confidence={Confidence:F3}, Count={Count}, BrightCores={BrightCores}, OffsetX={OffsetX:F1}, VerticalSpan={VerticalSpan:F1}",
                            particle.Found,
                            particle.Confidence,
                            particle.ParticleCount,
                            particle.BrightCoreCount,
                            particle.ScreenOffsetX,
                            particle.VerticalSpan);
                    }
                }

                var arrival = _arrivalVerifier.Verify(observation);
                if (arrival.Arrived)
                {
                    _logger.LogInformation("[任务标记导航] 到达确认：{Reason}", arrival.Reason);
                    return NavigationResult.Arrived(arrival.Reason) with { RecoveryAttempts = recovery.Attempts };
                }

                if (!marker.Found)
                {
                    markerWasUsable = false;
                    particleStableFrames = 0;
                    lastParticleCandidateOffset = null;
                    _input.SetMoveForward(false);
                    recovery.ResetObservation();
                    turnFramesWithoutProgress = 0;
                    bestTurnOffset = float.MaxValue;
                    markerLostFrames++;
                    if (markerLostFrames >= _options.MarkerLostFramesBeforeRetry)
                    {
                        markerLostFrames = 0;
                        if (navigationRetries >= _options.MaxNavigationRetries)
                        {
                            return NavigationResult.NeedReplan("任务导航标记持续丢失", recovery.Attempts);
                        }

                        navigationRetries++;
                        _logger.LogInformation(
                            "[任务标记导航] 标记持续丢失，第 {Retry}/{MaxRetries} 次重新按任务导航键",
                            navigationRetries,
                            _options.MaxNavigationRetries);
                        _input.PressQuestNavigation();
                    }

                    await DelayNextLoop(ct);
                    continue;
                }

                var now = _timeProvider.GetUtcNow();
                if (!markerWasUsable ||
                    _options.MarkerRefreshInterval <= TimeSpan.Zero ||
                    now - lastMarkerRefreshAt >= _options.MarkerRefreshInterval)
                {
                    _input.PressQuestNavigation();
                    lastMarkerRefreshAt = now;
                    _logger.LogDebug("[任务标记导航] 已按下任务导航键刷新当前标记");
                }

                markerWasUsable = true;
                markerLostFrames = 0;
                var particleUsable = _options.EnableGoldenParticleGuidance &&
                                     particle.Found &&
                                     particle.Confidence >= _options.MinimumParticleConfidence &&
                                     _timeProvider.GetUtcNow() >= particleSuppressedUntil &&
                                     (observation.DistanceMeters is not { } distanceMeters ||
                                      distanceMeters > _options.FinalApproachDistanceMeters);
                if (particleUsable)
                {
                    var smoothingFactor = Math.Clamp(_options.ParticleOffsetSmoothingFactor, 0, 1);
                    var offsetJumped = lastParticleCandidateOffset is { } lastOffset &&
                                       Math.Abs(particle.ScreenOffsetX - lastOffset) >
                                       _options.ParticleMaximumFrameOffsetJumpPixels;
                    if (particleStableFrames == 0 || offsetJumped)
                    {
                        smoothedParticleOffset = particle.ScreenOffsetX;
                        particleStableFrames = 1;
                    }
                    else
                    {
                        smoothedParticleOffset = smoothingFactor * particle.ScreenOffsetX +
                                                 (1 - smoothingFactor) * smoothedParticleOffset;
                        particleStableFrames++;
                    }

                    lastParticleCandidateOffset = particle.ScreenOffsetX;
                }
                else
                {
                    particleStableFrames = 0;
                    lastParticleCandidateOffset = null;
                }

                var useParticleGuidance = particleUsable &&
                                          particleStableFrames >= Math.Max(
                                              1,
                                              _options.ParticleStableFramesRequired);
                if (useParticleGuidance && observation.DistanceMeters is { } currentDistance)
                {
                    if (bestDistanceDuringParticleGuidance is null ||
                        currentDistance < bestDistanceDuringParticleGuidance)
                    {
                        bestDistanceDuringParticleGuidance = currentDistance;
                    }
                    else if (currentDistance - bestDistanceDuringParticleGuidance >
                             _options.ParticleMaximumDistanceRegressionMeters)
                    {
                        particleSuppressedUntil = DateTimeOffset.MaxValue;
                        particleStableFrames = 0;
                        lastParticleCandidateOffset = null;
                        useParticleGuidance = false;
                        _input.SetMoveForward(false);
                        _logger.LogWarning(
                            "[Golden particle navigation] Quest distance regressed from {BestDistance}m to {CurrentDistance}m; disabling particle steering for this run",
                            bestDistanceDuringParticleGuidance,
                            currentDistance);
                        _input.PressQuestNavigation();
                    }
                }

                var steeringSource = useParticleGuidance
                    ? QuestSteeringSource.GoldenParticle
                    : QuestSteeringSource.Marker;
                var steeringOffset = useParticleGuidance
                    ? smoothedParticleOffset
                    : marker.ScreenOffsetX;
                var horizontalDeadZone = useParticleGuidance
                    ? _options.ParticleHorizontalDeadZonePixels
                    : _options.HorizontalDeadZonePixels;
                if (steeringSource != lastSteeringSource)
                {
                    turnFramesWithoutProgress = 0;
                    bestTurnOffset = float.MaxValue;
                    _logger.LogInformation(
                        steeringSource == QuestSteeringSource.GoldenParticle
                            ? "[金色粒子导航] 粒子轨迹已稳定，切换为短程路径引导，任务图标继续负责最终到达"
                            : "[金色粒子导航] 粒子轨迹不可用或已进入最终接近阶段，退回任务图标跟随");
                    lastSteeringSource = steeringSource;
                }

                if (Math.Abs(steeringOffset) > horizontalDeadZone)
                {
                    var absoluteOffset = Math.Abs(steeringOffset);
                    var moveWhileTurning = useParticleGuidance &&
                                           absoluteOffset <=
                                           _options.ParticleMoveWhileTurningLimitPixels;
                    _input.SetMoveForward(moveWhileTurning);
                    if (!moveWhileTurning)
                    {
                        recovery.ResetObservation();
                    }

                    if (absoluteOffset + _options.TurnProgressEpsilonPixels < bestTurnOffset)
                    {
                        bestTurnOffset = absoluteOffset;
                        turnFramesWithoutProgress = 0;
                    }
                    else
                    {
                        turnFramesWithoutProgress++;
                    }

                    if (turnFramesWithoutProgress >= _options.MaxTurnFramesWithoutProgress)
                    {
                        if (useParticleGuidance)
                        {
                            particleFallbacks++;
                            var disableForCurrentRun = particleFallbacks >=
                                                       Math.Max(1, _options.MaxParticleFallbacks);
                            var fallbackDuration = TimeSpan.FromTicks(
                                _options.ParticleFallbackDuration.Ticks *
                                Math.Max(1, particleFallbacks));
                            particleSuppressedUntil = disableForCurrentRun
                                ? DateTimeOffset.MaxValue
                                : _timeProvider.GetUtcNow() + fallbackDuration;
                            particleStableFrames = 0;
                            lastParticleCandidateOffset = null;
                            turnFramesWithoutProgress = 0;
                            bestTurnOffset = float.MaxValue;
                            lastSteeringSource = QuestSteeringSource.Marker;
                            if (disableForCurrentRun)
                            {
                                _logger.LogWarning(
                                    "[金色粒子导航] 粒子偏移 {Offset:F1} 像素持续未改善，已达到 {Fallbacks} 次，本轮禁用粒子并保持任务图标跟随",
                                    steeringOffset,
                                    particleFallbacks);
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "[金色粒子导航] 粒子偏移 {Offset:F1} 像素持续未改善，第 {Fallbacks} 次回退任务图标 {Duration:F1} 秒",
                                    steeringOffset,
                                    particleFallbacks,
                                    fallbackDuration.TotalSeconds);
                            }
                            _input.PressQuestNavigation();
                            await DelayNextLoop(ct);
                            continue;
                        }

                        if (navigationRetries >= _options.MaxNavigationRetries)
                        {
                            return NavigationResult.NeedReplan(
                                "任务导航标记偏移长时未收敛",
                                recovery.Attempts);
                        }

                        navigationRetries++;
                        turnFramesWithoutProgress = 0;
                        bestTurnOffset = float.MaxValue;
                        _logger.LogWarning(
                            "[任务标记导航] 水平偏移 {Offset:F1} 像素持续未改善，第 {Retry}/{MaxRetries} 次重新获取标记",
                            steeringOffset,
                            navigationRetries,
                            _options.MaxNavigationRetries);
                        _input.PressQuestNavigation();
                        await DelayNextLoop(ct);
                        continue;
                    }

                    _input.RotateHorizontal(CalculateRotation(steeringOffset, useParticleGuidance));
                    if (moveWhileTurning)
                    {
                        var turningPosition = _positionProvider.GetPosition(
                            frame,
                            request.MapName,
                            request.MapMatchMethod);
                        if (turningPosition is { } currentTurningPosition &&
                            recovery.IsStuck(currentTurningPosition))
                        {
                            _input.SetMoveForward(false);
                            if (!await recovery.RecoverAsync(ct))
                            {
                                return NavigationResult.NeedReplan(
                                    "Golden particle path following recovery failed",
                                    recovery.Attempts);
                            }

                            _input.PressQuestNavigation();
                        }
                    }

                    await DelayNextLoop(ct);
                    continue;
                }

                if (bestTurnOffset != float.MaxValue)
                {
                    _logger.LogInformation(
                        "[任务标记导航] {Source}已进入水平死区，开始前进，偏移 {Offset:F1} 像素",
                        useParticleGuidance ? "金色粒子路径" : "任务标记",
                        steeringOffset);
                }

                turnFramesWithoutProgress = 0;
                bestTurnOffset = float.MaxValue;
                _input.SetMoveForward(true);
                var position = _positionProvider.GetPosition(frame, request.MapName, request.MapMatchMethod);
                if (position is { } currentPosition && recovery.IsStuck(currentPosition))
                {
                    _input.SetMoveForward(false);
                    if (!await recovery.RecoverAsync(ct))
                    {
                        return NavigationResult.NeedReplan("任务标记跟随脱困失败", recovery.Attempts);
                    }

                    _logger.LogInformation(
                        "[任务标记导航] 脱困动作结束，重新获取任务标记");
                    _input.PressQuestNavigation();
                }

                await DelayNextLoop(ct);
            }

            return NavigationResult.HumanRequired("任务标记跟随超过时间或循环上限", recovery.Attempts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return NavigationResult.Cancelled() with { RecoveryAttempts = recovery.Attempts };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "任务标记跟随异常结束");
            return NavigationResult.Failed(ex.Message) with { RecoveryAttempts = recovery.Attempts };
        }
        finally
        {
            _input.ReleaseAll();
        }
    }

    private int CalculateRotation(float screenOffsetX, bool useParticleGuidance = false)
    {
        var divisor = useParticleGuidance
            ? _options.ParticleRotationDivisor
            : _options.RotationDivisor;
        var maximumRotationPixels = useParticleGuidance
            ? _options.ParticleMaximumRotationPixels
            : _options.MaximumRotationPixels;
        var delta = (int)Math.Round(screenOffsetX / Math.Max(divisor, 1));
        if (delta == 0)
        {
            delta = Math.Sign(screenOffsetX);
        }

        if (Math.Abs(delta) < _options.MinimumRotationPixels)
        {
            delta = Math.Sign(delta) * _options.MinimumRotationPixels;
        }

        return Math.Clamp(delta, -maximumRotationPixels, maximumRotationPixels);
    }

    private Task DelayNextLoop(CancellationToken ct)
    {
        return _options.LoopInterval <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(_options.LoopInterval, ct);
    }

    private enum QuestSteeringSource
    {
        Marker,
        GoldenParticle
    }
}
