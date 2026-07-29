using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Model.Assets;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;

namespace BetterGenshinImpact.GameTask.AutoQuest.Navigation;

[Flags]
public enum QuestMarkerTemplateProfile
{
    None = 0,
    TrackedTarget = 1,
    Start = 2,
    Enter = 4,
    Finish = 8,
    CommissionQuestion = 16,
    CommissionTask = 32,
    Commission = CommissionQuestion | CommissionTask,
    All = TrackedTarget | Start | Enter | Finish | Commission
}

public interface IQuestMarkerTemplateProvider
{
    IReadOnlyList<RecognitionObject> Get(ImageRegion frame);
}

/// <summary>
/// 提供任务标记模板。除 BetterGI 原有的蓝色追踪点外，还包含主线、支线、
/// 通用任务和委托在开始、进行、进入及完成状态下的常见图标。
/// </summary>
public sealed class QuestMarkerTemplateProvider : IQuestMarkerTemplateProvider
{
    private const double ReferenceWidth = 1920d;
    private const double ReferenceHeight = 1080d;
    private const double TemplateThreshold = 0.8;

    private static readonly TemplateDefinition[] TemplateDefinitions =
    [
        new(@"QuestMarkers\Icon\Main_Start.png", QuestMarkerTemplateProfile.Start),
        new(@"QuestMarkers\Icon\Main_Proce_For_Bigmap.png", QuestMarkerTemplateProfile.TrackedTarget),
        new(@"QuestMarkers\Icon\Main_Enter.png", QuestMarkerTemplateProfile.Enter),
        new(@"QuestMarkers\Icon\Main_Finish.png", QuestMarkerTemplateProfile.Finish),
        new(@"QuestMarkers\Icon\Branch_Start.png", QuestMarkerTemplateProfile.Start),
        new(@"QuestMarkers\Icon\Branch_Proce_For_Bigmap.png", QuestMarkerTemplateProfile.TrackedTarget),
        new(@"QuestMarkers\Icon\Branch_Enter_For_Into.png", QuestMarkerTemplateProfile.Enter),
        new(@"QuestMarkers\Icon\Branch_Finish.png", QuestMarkerTemplateProfile.Finish),
        new(@"QuestMarkers\Icon\Common_Start.png", QuestMarkerTemplateProfile.Start),
        new(@"QuestMarkers\Icon\Common_Proce_For_Bigmap.png", QuestMarkerTemplateProfile.TrackedTarget),
        new(@"QuestMarkers\Icon\Common_Enter.png", QuestMarkerTemplateProfile.Enter),
        new(@"QuestMarkers\Icon\Common_Finish.png", QuestMarkerTemplateProfile.Finish),
        new(@"QuestMarkers\Icon\Common02_Start.png", QuestMarkerTemplateProfile.Start),
        new(@"QuestMarkers\Icon\Common02_Proce_For_Bigmap.png", QuestMarkerTemplateProfile.TrackedTarget),
        new(@"QuestMarkers\Icon\Common02_Enter.png", QuestMarkerTemplateProfile.Enter),
        new(@"QuestMarkers\Icon\Common02_Finish.png", QuestMarkerTemplateProfile.Finish),
        new(@"QuestMarkers\Commission\IconBigmapCommission.jpg", QuestMarkerTemplateProfile.TrackedTarget),
        new(@"QuestMarkers\Commission\IconQuestionCommission.png", QuestMarkerTemplateProfile.CommissionQuestion),
        new(@"QuestMarkers\Commission\IconTaskCommission.png", QuestMarkerTemplateProfile.CommissionTask)
    ];

    private static readonly CaptureAssetsCache<QuestMarkerTemplateAssets> Cache = new(
        static captureSize => new QuestMarkerTemplateAssets(captureSize));

    private readonly QuestMarkerTemplateProfile _profile;

    public QuestMarkerTemplateProvider(
        QuestMarkerTemplateProfile profile = QuestMarkerTemplateProfile.TrackedTarget)
    {
        _profile = profile;
    }

    public IReadOnlyList<RecognitionObject> Get(ImageRegion frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var templates = new List<RecognitionObject>();
        if ((_profile & QuestMarkerTemplateProfile.TrackedTarget) != 0)
        {
            templates.Add(ElementRecognition.Get("BlueTrackPoint", frame));
        }
        foreach (var template in Cache.Get(frame).Templates)
        {
            if ((_profile & template.Profile) != 0)
            {
                templates.Add(template.Recognition);
            }
        }

        return templates;
    }

    private sealed class QuestMarkerTemplateAssets
    {
        public QuestMarkerTemplateAssets(CaptureSize captureSize)
        {
            var roi = CreateSearchRegion(captureSize.Width, captureSize.Height);
            var templates = new List<TemplateEntry>(TemplateDefinitions.Length);
            foreach (var definition in TemplateDefinitions)
            {
                templates.Add(new TemplateEntry(
                    definition.Profile,
                    LoadTemplate(definition.RelativePath, captureSize, roi)));
            }

            Templates = templates;
        }

        public IReadOnlyList<TemplateEntry> Templates { get; }

        private static RecognitionObject LoadTemplate(
            string relativePath,
            CaptureSize captureSize,
            Rect roi)
        {
            using var source = GameTaskManager.LoadAssetImage(
                "AutoQuest",
                relativePath,
                captureSize.Width,
                captureSize.Height,
                ImreadModes.Unchanged);

            var template = new Mat();
            Mat? mask = null;
            switch (source.Channels())
            {
                case 4:
                    Cv2.CvtColor(source, template, ColorConversionCodes.BGRA2BGR);
                    mask = new Mat();
                    Cv2.ExtractChannel(source, mask, 3);
                    break;
                case 3:
                    source.CopyTo(template);
                    break;
                case 1:
                    Cv2.CvtColor(source, template, ColorConversionCodes.GRAY2BGR);
                    break;
                default:
                    template.Dispose();
                    throw new InvalidOperationException($"不支持的任务标记模板通道数：{source.Channels()}");
            }

            return new RecognitionObject
            {
                Name = $"QuestMarker:{Path.GetFileNameWithoutExtension(relativePath)}",
                RecognitionType = RecognitionTypes.TemplateMatch,
                TemplateImageMat = template,
                MaskMat = mask,
                Use3Channels = true,
                TemplateMatchMode = mask == null
                    ? TemplateMatchModes.CCoeffNormed
                    : TemplateMatchModes.CCorrNormed,
                RegionOfInterest = roi,
                Threshold = TemplateThreshold,
                DrawOnWindow = true
            }.InitTemplate();
        }

        private static Rect CreateSearchRegion(int width, int height)
        {
            var x = (int)Math.Round(width * 300d / ReferenceWidth);
            var y = (int)Math.Round(height * 100d / ReferenceHeight);
            var right = (int)Math.Round(width * 1600d / ReferenceWidth);
            var bottom = (int)Math.Round(height * 900d / ReferenceHeight);
            return new Rect(
                x,
                y,
                Math.Max(1, Math.Min(width, right) - x),
                Math.Max(1, Math.Min(height, bottom) - y));
        }
    }

    private readonly record struct TemplateDefinition(
        string RelativePath,
        QuestMarkerTemplateProfile Profile);

    private readonly record struct TemplateEntry(
        QuestMarkerTemplateProfile Profile,
        RecognitionObject Recognition);
}
