using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.GameTask.AutoPick.Assets;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoQuest.Navigation;

public sealed record ArrivalObservation(
    DateTimeOffset Timestamp,
    int? DistanceMeters,
    bool MarkerFound,
    bool HasInteractionPrompt,
    bool IsInTalkUi,
    bool QuestDescriptionChanged);

public sealed record ArrivalVerificationResult(
    bool Arrived,
    bool DistanceProgressing,
    string Reason);

public sealed class ArrivalVerifierOptions
{
    public TimeSpan DistanceTrendWindow { get; init; } = TimeSpan.FromSeconds(6);

    public int MinimumProgressMeters { get; init; } = 2;

    public int ArrivalDistanceMeters { get; init; } = 3;

    public int InteractionDistanceMeters { get; init; } = 10;

    public int MarkerMissingDistanceMeters { get; init; } = 12;
}

public interface IArrivalVerifier
{
    ArrivalVerificationResult Verify(ArrivalObservation observation);

    void Reset();
}

/// <summary>
/// 组合任务距离趋势、标记、交互提示、剧情界面和任务描述变化确认到达。
/// 标记接近屏幕中心不参与到达判定，避免墙后、楼上或楼下目标被误判。
/// </summary>
public sealed class ArrivalVerifier : IArrivalVerifier
{
    private readonly ArrivalVerifierOptions _options;
    private readonly Queue<(DateTimeOffset Timestamp, int Distance)> _distanceSamples = new();

    public ArrivalVerifier(ArrivalVerifierOptions? options = null)
    {
        _options = options ?? new ArrivalVerifierOptions();
    }

    public ArrivalVerificationResult Verify(ArrivalObservation observation)
    {
        RecordDistance(observation);
        var distanceProgressing = IsDistanceProgressing();

        if (observation.QuestDescriptionChanged)
        {
            return new ArrivalVerificationResult(true, distanceProgressing, "任务描述已变化");
        }

        if (observation.IsInTalkUi)
        {
            return new ArrivalVerificationResult(true, distanceProgressing, "已进入剧情或对话界面");
        }

        var distance = observation.DistanceMeters;
        var isNear = distance is not null && distance <= _options.ArrivalDistanceMeters;
        var isInteractionRange = distance is not null && distance <= _options.InteractionDistanceMeters;
        var isMarkerMissingRange = distance is not null && distance <= _options.MarkerMissingDistanceMeters;

        if (observation.HasInteractionPrompt && (isInteractionRange || !observation.MarkerFound))
        {
            return new ArrivalVerificationResult(true, distanceProgressing, "目标附近出现交互提示");
        }

        if (isNear && (distanceProgressing || !observation.MarkerFound || observation.HasInteractionPrompt))
        {
            return new ArrivalVerificationResult(true, distanceProgressing, "任务距离已进入到达阈值");
        }

        if (!observation.MarkerFound && isMarkerMissingRange && distanceProgressing)
        {
            return new ArrivalVerificationResult(true, true, "接近目标后任务标记消失");
        }

        return new ArrivalVerificationResult(false, distanceProgressing, "尚未获得足够的到达信号");
    }

    public void Reset()
    {
        _distanceSamples.Clear();
    }

    private void RecordDistance(ArrivalObservation observation)
    {
        if (observation.DistanceMeters is { } distance and >= 0)
        {
            _distanceSamples.Enqueue((observation.Timestamp, distance));
        }

        var cutoff = observation.Timestamp - _options.DistanceTrendWindow;
        while (_distanceSamples.TryPeek(out var sample) && sample.Timestamp < cutoff)
        {
            _distanceSamples.Dequeue();
        }
    }

    private bool IsDistanceProgressing()
    {
        if (_distanceSamples.Count < 2)
        {
            return false;
        }

        return _distanceSamples.Peek().Distance - _distanceSamples.Last().Distance >= _options.MinimumProgressMeters;
    }
}

public interface IQuestArrivalObservationProvider
{
    ArrivalObservation Observe(ImageRegion frame, QuestMarkerDetection marker);
}

/// <summary>
/// 从当前画面收集到达判定信号。任务描述变化由可选回调注入，避免在导航核心中承担任务识别职责。
/// </summary>
public sealed class QuestArrivalObservationProvider : IQuestArrivalObservationProvider
{
    private static readonly TimeSpan DistanceOcrInterval = TimeSpan.FromMilliseconds(400);
    private readonly TimeProvider _timeProvider;
    private readonly Func<ImageRegion, bool>? _questDescriptionChangedDetector;
    private readonly ILogger<QuestArrivalObservationProvider> _logger;
    private DateTimeOffset _lastDistanceOcrAt = DateTimeOffset.MinValue;
    private int? _lastDistance;

    public QuestArrivalObservationProvider(
        Func<ImageRegion, bool>? questDescriptionChangedDetector = null,
        TimeProvider? timeProvider = null,
        ILogger<QuestArrivalObservationProvider>? logger = null)
    {
        _questDescriptionChangedDetector = questDescriptionChangedDetector;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? App.GetLogger<QuestArrivalObservationProvider>();
    }

    public ArrivalObservation Observe(ImageRegion frame, QuestMarkerDetection marker)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var now = _timeProvider.GetUtcNow();
        if (now - _lastDistanceOcrAt >= DistanceOcrInterval)
        {
            _lastDistanceOcrAt = now;
            _lastDistance = TryReadMissionDistance(frame);
        }

        var isInTalkUi = Bv.IsInTalkUi(frame);
        var hasInteractionPrompt = HasInteractionPrompt(frame);
        var questDescriptionChanged = false;
        if (_questDescriptionChangedDetector != null)
        {
            try
            {
                questDescriptionChanged = _questDescriptionChangedDetector(frame);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "检测任务描述变化失败，本帧忽略该信号");
            }
        }

        return new ArrivalObservation(
            now,
            _lastDistance,
            marker.Found,
            hasInteractionPrompt,
            isInTalkUi,
            questDescriptionChanged);
    }

    private static bool HasInteractionPrompt(ImageRegion frame)
    {
        var pickKey = TaskContext.Instance().Config.AutoPickConfig.PickKey;
        var recognition = AutoPickAssets.Get(frame, pickKey).PickRo;
        using var interaction = frame.Find(recognition);
        return interaction.IsExist();
    }

    private int? TryReadMissionDistance(ImageRegion frame)
    {
        try
        {
            using var paimonMenu = frame.Find(ElementRecognition.Get("PaimonMenu", frame));
            if (!paimonMenu.IsExist())
            {
                return null;
            }

            var scale = TaskContext.Instance().SystemInfo.AssetScale;
            var distanceArea = new Rect(
                    paimonMenu.X,
                    paimonMenu.Y + (int)(195 * scale),
                    (int)(320 * scale),
                    (int)(110 * scale))
                .ClampTo(frame.SrcMat);
            if (distanceArea.Width <= 0 || distanceArea.Height <= 0)
            {
                return null;
            }

            var regions = frame.FindMulti(new RecognitionObject
            {
                RecognitionType = RecognitionTypes.Ocr,
                RegionOfInterest = distanceArea
            });

            try
            {
                foreach (var region in regions)
                {
                    if (!region.Text.Contains('m', StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var distance = StringUtils.TryExtractPositiveInt(region.Text);
                    if (distance >= 0)
                    {
                        return distance;
                    }
                }
            }
            finally
            {
                regions.ForEach(region => region.Dispose());
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OCR 任务距离失败，本帧沿用上次结果");
        }

        return null;
    }
}
