using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using System;

namespace BetterGenshinImpact.GameTask.AutoQuest.Navigation;

public sealed record QuestMarkerDetection(
    bool Found,
    Rect Bounds,
    float ScreenOffsetX,
    float ScreenOffsetY,
    float Confidence)
{
    public static QuestMarkerDetection NotFound { get; } = new(false, default, 0, 0, 0);
}

public interface IQuestMarkerRecognizer
{
    QuestMarkerDetection Detect(ImageRegion frame);
}

/// <summary>
/// 识别屏幕上的任务导航标记。匹配过程沿用 BetterGI 的 BlueTrackPoint 素材和 ROI，
/// 同时保留本次模板匹配分数作为置信度。
/// </summary>
public sealed class QuestMarkerRecognizer : IQuestMarkerRecognizer
{
    private const double ReservedTopRightStartRatio = 0.81;
    private const double ReservedTopRightHeightRatio = 0.10;
    private const float ActiveTemplateMinimumConfidence = 0.8f;
    private readonly IQuestMarkerTemplateProvider _templateProvider;
    private RecognitionObject? _activeTemplate;
    private Rect? _lastBounds;

    public QuestMarkerRecognizer(IQuestMarkerTemplateProvider? templateProvider = null)
    {
        _templateProvider = templateProvider ?? new QuestMarkerTemplateProvider();
    }

    public QuestMarkerDetection Detect(ImageRegion frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_activeTemplate != null && _lastBounds is { } lastBounds)
        {
            var localDetection = DetectTemplate(
                frame,
                _activeTemplate,
                CreateActiveSearchRegion(frame, lastBounds));
            if (IsReliableActiveDetection(localDetection))
            {
                _lastBounds = localDetection.Bounds;
                return localDetection;
            }

            var fullActiveDetection = DetectTemplate(frame, _activeTemplate);
            if (IsReliableActiveDetection(fullActiveDetection))
            {
                _lastBounds = fullActiveDetection.Bounds;
                return fullActiveDetection;
            }

            _activeTemplate = null;
            _lastBounds = null;
        }

        var bestDetection = QuestMarkerDetection.NotFound;
        var bestRejectedConfidence = 0f;
        RecognitionObject? bestTemplate = null;
        foreach (var recognition in _templateProvider.Get(frame))
        {
            var detection = DetectTemplate(frame, recognition);
            if (detection.Found &&
                (!bestDetection.Found || detection.Confidence > bestDetection.Confidence))
            {
                bestDetection = detection;
                bestTemplate = recognition;
            }
            else if (!detection.Found)
            {
                bestRejectedConfidence = Math.Max(bestRejectedConfidence, detection.Confidence);
            }
        }

        if (bestDetection.Found)
        {
            _activeTemplate = bestTemplate;
            _lastBounds = bestDetection.Bounds;
            return bestDetection;
        }

        return QuestMarkerDetection.NotFound with { Confidence = bestRejectedConfidence };
    }

    private static QuestMarkerDetection DetectTemplate(
        ImageRegion frame,
        RecognitionObject recognition,
        Rect? searchLimit = null)
    {
        if (!ImageRegionReferenceSearchHelper.TryGetReferenceSearchRegion(
                frame,
                recognition,
                out var effectiveRoi,
                out var referenceScale))
        {
            return QuestMarkerDetection.NotFound;
        }

        Mat? convertedSource = null;
        Mat? binarySource = null;
        Mat? roiView = null;
        Mat? effectiveTemplate = null;
        Mat? effectiveMask = null;
        var disposeTemplate = false;
        var disposeMask = false;

        try
        {
            Mat searchSource;
            Mat? template;
            if (recognition.Use3Channels)
            {
                if (frame.SrcMat.Channels() == 4)
                {
                    convertedSource = new Mat();
                    Cv2.CvtColor(frame.SrcMat, convertedSource, ColorConversionCodes.BGRA2BGR);
                    searchSource = convertedSource;
                }
                else
                {
                    searchSource = frame.SrcMat;
                }

                template = recognition.TemplateImageMat;
            }
            else
            {
                if (recognition.UseBinaryMatch)
                {
                    binarySource = new Mat();
                    Cv2.Threshold(
                        frame.CacheGreyMat,
                        binarySource,
                        recognition.BinaryThreshold,
                        255,
                        ThresholdTypes.Binary);
                    searchSource = binarySource;
                }
                else
                {
                    searchSource = frame.CacheGreyMat;
                }

                template = recognition.TemplateImageGreyMat;
            }

            if (template == null || template.Empty())
            {
                return QuestMarkerDetection.NotFound;
            }

            effectiveRoi = effectiveRoi == default
                ? new Rect(0, 0, searchSource.Width, searchSource.Height)
                : effectiveRoi.ClampTo(searchSource);
            if (searchLimit is { } limit)
            {
                effectiveRoi = Intersect(effectiveRoi, limit.ClampTo(searchSource));
            }

            if (effectiveRoi.Width <= 0 || effectiveRoi.Height <= 0)
            {
                return QuestMarkerDetection.NotFound;
            }

            effectiveTemplate = ImageRegionReferenceSearchHelper.GetEffectiveTemplate(
                recognition,
                template,
                referenceScale,
                out disposeTemplate);
            effectiveMask = ImageRegionReferenceSearchHelper.GetEffectiveMask(
                recognition.MaskMat,
                effectiveTemplate,
                out disposeMask);

            if (effectiveRoi.Width < effectiveTemplate.Width || effectiveRoi.Height < effectiveTemplate.Height)
            {
                return QuestMarkerDetection.NotFound;
            }

            roiView = new Mat(searchSource, effectiveRoi);
            using var result = new Mat();
            Cv2.MatchTemplate(roiView, effectiveTemplate, result, recognition.TemplateMatchMode, effectiveMask);

            if (recognition.TemplateMatchMode is TemplateMatchModes.SqDiff or TemplateMatchModes.CCoeff or TemplateMatchModes.CCorr)
            {
                Cv2.Normalize(result, result, 0, 1, NormTypes.MinMax);
            }

            var isDifferenceMode = recognition.TemplateMatchMode is TemplateMatchModes.SqDiff or TemplateMatchModes.SqDiffNormed;
            SuppressReservedTopRightUiMatches(
                result,
                effectiveRoi,
                frame.Width,
                frame.Height,
                isDifferenceMode);
            Cv2.MinMaxLoc(result, out var minValue, out var maxValue, out var minLocation, out var maxLocation);
            var confidence = isDifferenceMode ? 1d - minValue : maxValue;
            if (confidence < recognition.Threshold)
            {
                return QuestMarkerDetection.NotFound with { Confidence = (float)Math.Clamp(confidence, 0, 1) };
            }

            var location = isDifferenceMode ? minLocation : maxLocation;
            var bounds = new Rect(
                effectiveRoi.X + location.X,
                effectiveRoi.Y + location.Y,
                effectiveTemplate.Width,
                effectiveTemplate.Height);
            var centerX = bounds.X + bounds.Width / 2f;
            var centerY = bounds.Y + bounds.Height / 2f;

            return new QuestMarkerDetection(
                true,
                bounds,
                centerX - frame.Width / 2f,
                centerY - frame.Height / 2f,
                (float)Math.Clamp(confidence, 0, 1));
        }
        finally
        {
            roiView?.Dispose();
            binarySource?.Dispose();
            convertedSource?.Dispose();
            if (disposeMask)
            {
                effectiveMask?.Dispose();
            }

            if (disposeTemplate)
            {
                effectiveTemplate?.Dispose();
            }
        }
    }

    private static bool IsReliableActiveDetection(QuestMarkerDetection detection) =>
        detection.Found && detection.Confidence >= ActiveTemplateMinimumConfidence;

    private static Rect CreateActiveSearchRegion(ImageRegion frame, Rect lastBounds)
    {
        var paddingX = Math.Max(120, (int)Math.Round(frame.Width * 260d / 1920d));
        var paddingY = Math.Max(100, (int)Math.Round(frame.Height * 220d / 1080d));
        return new Rect(
                lastBounds.X - paddingX,
                lastBounds.Y - paddingY,
                lastBounds.Width + paddingX * 2,
                lastBounds.Height + paddingY * 2)
            .ClampTo(frame.SrcMat);
    }

    private static Rect Intersect(Rect left, Rect right)
    {
        var x = Math.Max(left.X, right.X);
        var y = Math.Max(left.Y, right.Y);
        var rightEdge = Math.Min(left.Right, right.Right);
        var bottomEdge = Math.Min(left.Bottom, right.Bottom);
        return rightEdge <= x || bottomEdge <= y
            ? default
            : new Rect(x, y, rightEdge - x, bottomEdge - y);
    }

    /// <summary>
    /// 原神右上角常驻菜单中存在与蓝色任务菱形非常相似的图标。该小块区域属于固定 UI，
    /// 不能作为世界空间任务标记，否则会把镜头持续拉向右侧。
    /// </summary>
    public static bool IsReservedTopRightUiCandidate(Rect bounds, int frameWidth, int frameHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            return false;
        }

        return bounds.X >= frameWidth * ReservedTopRightStartRatio &&
               bounds.Y < frameHeight * ReservedTopRightHeightRatio;
    }

    private static void SuppressReservedTopRightUiMatches(
        Mat result,
        Rect effectiveRoi,
        int frameWidth,
        int frameHeight,
        bool isDifferenceMode)
    {
        var reservedStartX = (int)Math.Ceiling(frameWidth * ReservedTopRightStartRatio);
        var reservedBottomY = (int)Math.Ceiling(frameHeight * ReservedTopRightHeightRatio);
        var resultStartX = Math.Max(0, reservedStartX - effectiveRoi.X);
        var resultBottomY = Math.Min(result.Height, reservedBottomY - effectiveRoi.Y);
        if (resultStartX >= result.Width || resultBottomY <= 0)
        {
            return;
        }

        using var reservedUiResult = new Mat(
            result,
            new Rect(resultStartX, 0, result.Width - resultStartX, resultBottomY));
        reservedUiResult.SetTo(isDifferenceMode ? Scalar.All(1) : Scalar.All(-1));
    }
}
