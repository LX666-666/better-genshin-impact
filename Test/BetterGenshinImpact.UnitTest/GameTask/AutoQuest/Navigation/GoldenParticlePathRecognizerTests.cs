using BetterGenshinImpact.GameTask.AutoQuest.Navigation;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoQuest.Navigation;

public class GoldenParticlePathRecognizerTests
{
    [Fact]
    public void Detect_WhenCoherentGoldenTrailIsRight_ReturnsRightGuidance()
    {
        using var source = CreateTrailFrame(430);
        using var frame = new ImageRegion(source, 0, 0);

        var result = new GoldenParticlePathRecognizer().Detect(frame);

        Assert.True(result.Found);
        Assert.True(result.ScreenOffsetX > 50);
        Assert.True(result.Confidence >= 0.6f);
        Assert.True(result.ParticleCount >= 6);
    }

    [Fact]
    public void Detect_WhenCoherentGoldenTrailIsLeft_ReturnsLeftGuidance()
    {
        using var source = CreateTrailFrame(190);
        using var frame = new ImageRegion(source, 0, 0);

        var result = new GoldenParticlePathRecognizer().Detect(frame);

        Assert.True(result.Found);
        Assert.True(result.ScreenOffsetX < -50);
    }

    [Fact]
    public void Detect_WhenTrailTurns_UsesLookAheadSegmentToFollowBend()
    {
        using var source = new Mat(360, 640, MatType.CV_8UC3, new Scalar(18, 24, 38));
        Point[] curvedTrail =
        [
            new(220, 305),
            new(225, 270),
            new(245, 235),
            new(300, 205),
            new(370, 178),
            new(440, 150),
            new(500, 115),
            new(485, 78)
        ];
        foreach (var point in curvedTrail)
        {
            DrawGoldenParticle(source, point.X, point.Y);
        }

        using var frame = new ImageRegion(source, 0, 0);

        var result = new GoldenParticlePathRecognizer().Detect(frame);

        Assert.True(result.Found);
        Assert.True(result.ScreenOffsetX > -90);
        Assert.True(result.BrightCoreCount >= 4);
    }

    [Fact]
    public void Detect_WhenOrdinaryGoldenFlowersHaveNoBrightCores_ReturnsNotFound()
    {
        using var source = new Mat(360, 640, MatType.CV_8UC3, new Scalar(18, 24, 38));
        for (var index = 0; index < 10; index++)
        {
            var x = 270 + index % 3 * 18;
            var y = 45 + index * 27;
            Cv2.Circle(source, new Point(x, y), 4, new Scalar(20, 180, 220), -1);
        }

        using var frame = new ImageRegion(source, 0, 0);

        var result = new GoldenParticlePathRecognizer().Detect(frame);

        Assert.False(result.Found);
    }

    [Fact]
    public void Detect_WhenOnlySparseGoldenPointsExist_ReturnsNotFound()
    {
        using var source = new Mat(360, 640, MatType.CV_8UC3, Scalar.Black);
        DrawGoldenParticle(source, 260, 80);
        DrawGoldenParticle(source, 310, 170);
        DrawGoldenParticle(source, 370, 250);
        using var frame = new ImageRegion(source, 0, 0);

        var result = new GoldenParticlePathRecognizer().Detect(frame);

        Assert.False(result.Found);
    }

    [Fact]
    public void Detect_WhenGoldenPointsHaveNoVerticalSpan_ReturnsNotFound()
    {
        using var source = new Mat(360, 640, MatType.CV_8UC3, Scalar.Black);
        for (var x = 260; x <= 400; x += 20)
        {
            DrawGoldenParticle(source, x, 180);
        }

        using var frame = new ImageRegion(source, 0, 0);

        var result = new GoldenParticlePathRecognizer().Detect(frame);

        Assert.False(result.Found);
    }

    [Theory]
    [InlineData(55)]
    [InlineData(590)]
    public void Detect_WhenVerticalGoldenTrailIsInSideHud_ReturnsNotFound(int centerX)
    {
        using var source = CreateTrailFrame(centerX);
        using var frame = new ImageRegion(source, 0, 0);

        var result = new GoldenParticlePathRecognizer().Detect(frame);

        Assert.False(result.Found);
    }

    private static Mat CreateTrailFrame(int centerX)
    {
        var source = new Mat(360, 640, MatType.CV_8UC3, new Scalar(18, 24, 38));
        var index = 0;
        for (var y = 45; y <= 300; y += 28)
        {
            var offset = index % 3 - 1;
            DrawGoldenParticle(source, centerX + offset * 5, y);
            index++;
        }

        // 模拟角色旁边的金色体力弧：它是面积较大且细长的 UI，不应决定路径方向。
        Cv2.Ellipse(
            source,
            new Point(330, 215),
            new Size(18, 48),
            0,
            -60,
            60,
            new Scalar(10, 180, 255),
            5);
        return source;
    }

    private static void DrawGoldenParticle(Mat source, int x, int y)
    {
        Cv2.Circle(source, new Point(x, y), 3, new Scalar(15, 215, 255), -1);
        Cv2.Line(
            source,
            new Point(x - 5, y),
            new Point(x + 5, y),
            new Scalar(25, 185, 255),
            1);
        Cv2.Line(
            source,
            new Point(x, y - 5),
            new Point(x, y + 5),
            new Scalar(25, 185, 255),
            1);
    }
}
