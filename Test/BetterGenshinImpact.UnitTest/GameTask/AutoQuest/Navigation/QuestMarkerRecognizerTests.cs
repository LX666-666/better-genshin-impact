using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.AutoQuest.Navigation;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoQuest.Navigation;

public class QuestMarkerRecognizerTests
{
    [Fact]
    public void TemplateProvider_WhenAssetsArePresent_LoadsLegacyAndImportedTemplates()
    {
        using var source = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using var frame = new ImageRegion(source, 0, 0);

        var templates = new QuestMarkerTemplateProvider(QuestMarkerTemplateProfile.All).Get(frame);

        Assert.Equal(20, templates.Count);
        Assert.Equal(19, templates.Count(template => template.Name?.StartsWith("QuestMarker:") == true));
        Assert.Single(templates, template => template.Name?.StartsWith("QuestMarker:") != true);
    }

    [Fact]
    public void TemplateProvider_DefaultProfile_OnlyIncludesTrackedTargetTemplates()
    {
        using var source = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using var frame = new ImageRegion(source, 0, 0);

        var templates = new QuestMarkerTemplateProvider().Get(frame);

        Assert.Equal(6, templates.Count);
        Assert.DoesNotContain(templates, template => template.Name == "QuestMarker:IconQuestionCommission");
        Assert.DoesNotContain(templates, template => template.Name?.EndsWith("_Start") == true);
        Assert.DoesNotContain(templates, template => template.Name?.EndsWith("_Finish") == true);
        Assert.DoesNotContain(templates, template => template.Name?.EndsWith("_Enter") == true);
    }

    [Fact]
    public void TemplateProvider_StartProfile_DoesNotIncludeLegacyBlueTrackPoint()
    {
        using var source = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using var frame = new ImageRegion(source, 0, 0);

        var templates = new QuestMarkerTemplateProvider(QuestMarkerTemplateProfile.Start).Get(frame);

        Assert.Equal(4, templates.Count);
        Assert.All(templates, template =>
            Assert.StartsWith("QuestMarker:", template.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void Detect_WhenImportedMaskedTemplateIsOnScreen_FindsItsPosition()
    {
        using var source = new Mat(360, 640, MatType.CV_8UC3);
        Cv2.Randu(source, Scalar.All(0), Scalar.All(255));
        using var frame = new ImageRegion(source, 0, 0);
        var provider = new QuestMarkerTemplateProvider(QuestMarkerTemplateProfile.Start);
        var template = Assert.Single(
            provider.Get(frame),
            candidate => candidate.Name == "QuestMarker:Main_Start");
        var expectedBounds = new Rect(300, 120, template.TemplateImageMat!.Width, template.TemplateImageMat.Height);
        using (var target = new Mat(source, expectedBounds))
        {
            template.TemplateImageMat.CopyTo(target, template.MaskMat!);
        }

        var result = new QuestMarkerRecognizer(provider).Detect(frame);

        Assert.True(result.Found);
        Assert.Equal(expectedBounds, result.Bounds);
        Assert.True(result.Confidence >= 0.99f);
    }

    [Fact]
    public void Detect_WhenInjectedTemplateMatches_ReturnsTemplateBounds()
    {
        using var template = new Mat(7, 7, MatType.CV_8UC3, Scalar.Black);
        Cv2.Line(template, new Point(0, 0), new Point(6, 6), Scalar.White, 1);
        Cv2.Line(template, new Point(6, 0), new Point(0, 6), new Scalar(32, 180, 255), 1);
        var recognition = new RecognitionObject
        {
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = template,
            Use3Channels = true,
            RegionOfInterest = new Rect(0, 0, 80, 60),
            Threshold = 0.99
        }.InitTemplate();
        var recognizer = new QuestMarkerRecognizer(new StaticTemplateProvider(recognition));
        using var source = new Mat(60, 80, MatType.CV_8UC3, new Scalar(12, 12, 12));
        using (var target = new Mat(source, new Rect(31, 17, template.Width, template.Height)))
        {
            template.CopyTo(target);
        }

        using var frame = new ImageRegion(source, 0, 0);
        var result = recognizer.Detect(frame);

        Assert.True(result.Found);
        Assert.Equal(new Rect(31, 17, 7, 7), result.Bounds);
        Assert.True(result.Confidence >= 0.99f);
    }

    [Fact]
    public void Detect_WhenMatchedTemplateRemainsNearby_ReusesActiveTemplate()
    {
        using var template = new Mat(7, 7, MatType.CV_8UC3, Scalar.Black);
        Cv2.Line(template, new Point(0, 0), new Point(6, 6), Scalar.White, 1);
        Cv2.Line(template, new Point(6, 0), new Point(0, 6), new Scalar(20, 160, 250), 1);
        var recognition = new RecognitionObject
        {
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = template,
            Use3Channels = true,
            RegionOfInterest = new Rect(0, 0, 80, 60),
            Threshold = 0.99
        }.InitTemplate();
        var provider = new StaticTemplateProvider(recognition);
        var recognizer = new QuestMarkerRecognizer(provider);
        using var firstSource = CreateFrameWithTemplate(template, new Rect(30, 16, 7, 7));
        using var firstFrame = new ImageRegion(firstSource, 0, 0);
        using var secondSource = CreateFrameWithTemplate(template, new Rect(35, 18, 7, 7));
        using var secondFrame = new ImageRegion(secondSource, 0, 0);

        var first = recognizer.Detect(firstFrame);
        var second = recognizer.Detect(secondFrame);

        Assert.True(first.Found);
        Assert.True(second.Found);
        Assert.Equal(new Rect(35, 18, 7, 7), second.Bounds);
        Assert.Equal(1, provider.GetCount);
    }

    [Fact]
    public void IsReservedTopRightUiCandidate_WhenAtObservedStaticUiPosition_ReturnsTrue()
    {
        var result = QuestMarkerRecognizer.IsReservedTopRightUiCandidate(
            new Rect(1589, 34, 28, 28),
            1920,
            1080);

        Assert.True(result);
    }

    [Theory]
    [InlineData(889, 280)]
    [InlineData(1589, 280)]
    [InlineData(900, 34)]
    public void IsReservedTopRightUiCandidate_WhenOutsideReservedUi_ReturnsFalse(int x, int y)
    {
        var result = QuestMarkerRecognizer.IsReservedTopRightUiCandidate(
            new Rect(x, y, 28, 28),
            1920,
            1080);

        Assert.False(result);
    }

    private static Mat CreateFrameWithTemplate(Mat template, Rect bounds)
    {
        var source = new Mat(60, 80, MatType.CV_8UC3, new Scalar(12, 12, 12));
        using var target = new Mat(source, bounds);
        template.CopyTo(target);
        return source;
    }

    private sealed class StaticTemplateProvider(params RecognitionObject[] templates)
        : IQuestMarkerTemplateProvider
    {
        public int GetCount { get; private set; }

        public IReadOnlyList<RecognitionObject> Get(ImageRegion frame)
        {
            GetCount++;
            return templates;
        }
    }
}
