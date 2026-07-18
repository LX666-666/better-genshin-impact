using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoQuest.Navigation;

public sealed record GoldenParticlePathDetection(
    bool Found,
    Rect Bounds,
    float ScreenOffsetX,
    float Confidence,
    int ParticleCount,
    float VerticalSpan,
    int BrightCoreCount = 0)
{
    public static GoldenParticlePathDetection NotFound { get; } =
        new(false, default, 0, 0, 0, 0);
}

public sealed record GoldenParticlePathRecognizerOptions
{
    public int MinimumHue { get; init; } = 15;

    public int MaximumHue { get; init; } = 42;

    public int MinimumSaturation { get; init; } = 90;

    public int MinimumValue { get; init; } = 175;

    public int MinimumBrightCoreValue { get; init; } = 245;

    public int MinimumBrightCorePixelsAt1080P { get; init; } = 1;

    public int MinimumBrightCoreCount { get; init; } = 4;

    public double MinimumBrightCoreSpanRatio { get; init; } = 0.08;

    public int MinimumParticleCount { get; init; } = 6;

    public double MinimumVerticalSpanRatio { get; init; } = 0.12;

    public double SearchTopRatio { get; init; } = 0.02;

    public double SearchBottomRatio { get; init; } = 0.92;

    /// <summary>
    /// 粒子只有进入屏幕中央走廊后才接管。两侧区域保留给任务图标基线旋转，
    /// 并排除角色列表、快捷图标等纵向金色 HUD。
    /// </summary>
    public double SearchLeftRatio { get; init; } = 0.18;

    public double SearchRightRatio { get; init; } = 0.82;

    public int MinimumComponentAreaAt1080P { get; init; } = 2;

    public int MaximumComponentAreaAt1080P { get; init; } = 360;

    public int MaximumComponentDimensionAt1080P { get; init; } = 44;

    public double MaximumComponentElongation { get; init; } = 2.1;

    public int SpatialLinkDistanceAt1080P { get; init; } = 260;

    public int PathLinkDistanceAt1080P { get; init; } = 420;

    public int LookAheadDistanceAt1080P { get; init; } = 180;

    public double PathStartMinimumYRatio { get; init; } = 0.35;

    public double PathStartMaximumOffsetRatio { get; init; } = 0.35;
}

public interface IGoldenParticlePathRecognizer
{
    GoldenParticlePathDetection Detect(ImageRegion frame);
}

/// <summary>
/// 识别任务导航键生成的金色粒子轨迹。只输出短程屏幕方向，不参与最终到达判定。
/// 高饱和金色掩码先按连通区域过滤体力弧等细长 UI，并要求多颗粒子具有高亮闪光核心；
/// 转向从角色附近的轨迹起点沿粒子链向前取预瞄点，以便在到达弯道前开始转向。
/// </summary>
public sealed class GoldenParticlePathRecognizer : IGoldenParticlePathRecognizer
{
    private const double ReservedTopRightStartRatio = 0.81;
    private const double ReservedTopRightHeightRatio = 0.12;
    private readonly GoldenParticlePathRecognizerOptions _options;

    public GoldenParticlePathRecognizer(GoldenParticlePathRecognizerOptions? options = null)
    {
        _options = options ?? new GoldenParticlePathRecognizerOptions();
    }

    public GoldenParticlePathDetection Detect(ImageRegion frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width <= 0 || frame.Height <= 0 || frame.SrcMat.Empty())
        {
            return GoldenParticlePathDetection.NotFound;
        }

        Mat? converted = null;
        try
        {
            var source = frame.SrcMat;
            if (source.Channels() == 4)
            {
                converted = new Mat();
                Cv2.CvtColor(source, converted, ColorConversionCodes.BGRA2BGR);
                source = converted;
            }
            else if (source.Channels() != 3)
            {
                converted = new Mat();
                Cv2.CvtColor(source, converted, ColorConversionCodes.GRAY2BGR);
                source = converted;
            }

            using var hsv = new Mat();
            using var mask = new Mat();
            using var brightCoreMask = new Mat();
            Cv2.CvtColor(source, hsv, ColorConversionCodes.BGR2HSV);
            Cv2.InRange(
                hsv,
                new Scalar(_options.MinimumHue, _options.MinimumSaturation, _options.MinimumValue),
                new Scalar(_options.MaximumHue, 255, 255),
                mask);
            Cv2.InRange(
                hsv,
                new Scalar(
                    _options.MinimumHue,
                    _options.MinimumSaturation,
                    _options.MinimumBrightCoreValue),
                new Scalar(_options.MaximumHue, 255, 255),
                brightCoreMask);
            ApplySearchMask(mask);
            ApplySearchMask(brightCoreMask);

            var particles = FindParticleComponents(mask, brightCoreMask);
            if (particles.Count < _options.MinimumParticleCount)
            {
                return GoldenParticlePathDetection.NotFound with { ParticleCount = particles.Count };
            }

            var cluster = SelectBestSpatialTrail(particles, frame.Height);
            if (cluster.Count < _options.MinimumParticleCount)
            {
                return GoldenParticlePathDetection.NotFound with { ParticleCount = cluster.Count };
            }

            var minimumVerticalSpan = frame.Height * _options.MinimumVerticalSpanRatio;
            var verticalSpan = cluster.Max(point => point.CenterY) - cluster.Min(point => point.CenterY);
            if (verticalSpan < minimumVerticalSpan)
            {
                return GoldenParticlePathDetection.NotFound with
                {
                    ParticleCount = cluster.Count,
                    VerticalSpan = (float)verticalSpan
                };
            }

            var steeringPoint = SelectLookAheadSteeringPoint(cluster, frame.Width, frame.Height);
            var targetX = steeringPoint.CenterX;
            var bounds = GetBounds(cluster);
            var confidence = CalculateConfidence(cluster, verticalSpan, frame.Height);
            var brightCoreCount = cluster.Count(point => point.BrightCorePixelCount > 0);
            return new GoldenParticlePathDetection(
                true,
                bounds,
                (float)(targetX - frame.Width / 2d),
                confidence,
                cluster.Count,
                (float)verticalSpan,
                brightCoreCount);
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private void ApplySearchMask(Mat mask)
    {
        var top = Math.Clamp((int)Math.Round(mask.Height * _options.SearchTopRatio), 0, mask.Height);
        var bottom = Math.Clamp((int)Math.Round(mask.Height * _options.SearchBottomRatio), 0, mask.Height);
        if (top > 0)
        {
            using var topRegion = mask.RowRange(0, top);
            topRegion.SetTo(Scalar.Black);
        }

        if (bottom < mask.Height)
        {
            using var bottomRegion = mask.RowRange(bottom, mask.Height);
            bottomRegion.SetTo(Scalar.Black);
        }

        var left = Math.Clamp((int)Math.Round(mask.Width * _options.SearchLeftRatio), 0, mask.Width);
        var right = Math.Clamp((int)Math.Round(mask.Width * _options.SearchRightRatio), 0, mask.Width);
        if (left > 0)
        {
            using var leftRegion = mask.ColRange(0, left);
            leftRegion.SetTo(Scalar.Black);
        }

        if (right < mask.Width)
        {
            using var rightRegion = mask.ColRange(right, mask.Width);
            rightRegion.SetTo(Scalar.Black);
        }

        var reservedX = Math.Clamp(
            (int)Math.Round(mask.Width * ReservedTopRightStartRatio),
            0,
            mask.Width);
        var reservedBottom = Math.Clamp(
            (int)Math.Round(mask.Height * ReservedTopRightHeightRatio),
            0,
            mask.Height);
        if (reservedX < mask.Width && reservedBottom > 0)
        {
            using var reserved = new Mat(
                mask,
                new Rect(reservedX, 0, mask.Width - reservedX, reservedBottom));
            reserved.SetTo(Scalar.Black);
        }
    }

    private List<ParticleComponent> FindParticleComponents(Mat mask, Mat brightCoreMask)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            mask,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity8,
            MatType.CV_32S);

        var scale = Math.Max(0.1, mask.Height / 1080d);
        var minimumArea = Math.Max(
            1,
            (int)Math.Round(_options.MinimumComponentAreaAt1080P * scale * scale));
        var maximumArea = Math.Max(
            minimumArea,
            (int)Math.Round(_options.MaximumComponentAreaAt1080P * scale * scale));
        var maximumDimension = Math.Max(
            3,
            (int)Math.Round(_options.MaximumComponentDimensionAt1080P * scale));
        var minimumBrightCorePixels = Math.Max(
            1,
            (int)Math.Round(_options.MinimumBrightCorePixelsAt1080P * scale * scale));
        var result = new List<ParticleComponent>();
        for (var index = 1; index < count; index++)
        {
            using var row = stats.Row(index);
            if (!row.GetArray<int>(out var values))
            {
                continue;
            }

            var x = values[0];
            var y = values[1];
            var width = values[2];
            var height = values[3];
            var area = values[4];
            if (area < minimumArea || area > maximumArea ||
                width > maximumDimension || height > maximumDimension)
            {
                continue;
            }

            var shorterSide = Math.Max(1, Math.Min(width, height));
            var elongation = Math.Max(width, height) / (double)shorterSide;
            if (elongation > _options.MaximumComponentElongation)
            {
                continue;
            }

            var bounds = new Rect(x, y, width, height);
            var brightCorePixelCount = CountBrightCorePixels(
                labels,
                brightCoreMask,
                index,
                bounds);
            result.Add(new ParticleComponent(
                bounds,
                x + width / 2d,
                y + height / 2d,
                brightCorePixelCount >= minimumBrightCorePixels
                    ? brightCorePixelCount
                    : 0));
        }

        return result;
    }

    private List<ParticleComponent> SelectBestSpatialTrail(
        IReadOnlyList<ParticleComponent> particles,
        int frameHeight)
    {
        var scale = Math.Max(0.1, frameHeight / 1080d);
        var linkDistance = Math.Max(12, _options.SpatialLinkDistanceAt1080P * scale);
        var linkDistanceSquared = linkDistance * linkDistance;
        var visited = new bool[particles.Count];
        var eligibleClusters = new List<List<ParticleComponent>>();
        for (var start = 0; start < particles.Count; start++)
        {
            if (visited[start])
            {
                continue;
            }

            var indexes = new Queue<int>();
            var cluster = new List<ParticleComponent>();
            indexes.Enqueue(start);
            visited[start] = true;
            while (indexes.Count > 0)
            {
                var current = indexes.Dequeue();
                cluster.Add(particles[current]);
                for (var candidate = 0; candidate < particles.Count; candidate++)
                {
                    if (visited[candidate] ||
                        DistanceSquared(particles[current], particles[candidate]) > linkDistanceSquared)
                    {
                        continue;
                    }

                    visited[candidate] = true;
                    indexes.Enqueue(candidate);
                }
            }

            var brightCorePoints = cluster
                .Where(point => point.BrightCorePixelCount > 0)
                .ToArray();
            var brightCoreSpan = brightCorePoints.Length == 0
                ? 0
                : brightCorePoints.Max(point => point.CenterY) -
                  brightCorePoints.Min(point => point.CenterY);
            if (cluster.Count >= _options.MinimumParticleCount &&
                brightCorePoints.Length >= _options.MinimumBrightCoreCount &&
                brightCoreSpan >= frameHeight * _options.MinimumBrightCoreSpanRatio)
            {
                eligibleClusters.Add(cluster);
            }
        }

        return eligibleClusters
                   .OrderByDescending(cluster => CalculateTrailSelectionScore(cluster, frameHeight))
                   .FirstOrDefault()
               ?? [];
    }

    private ParticleComponent SelectLookAheadSteeringPoint(
        IReadOnlyList<ParticleComponent> cluster,
        int frameWidth,
        int frameHeight)
    {
        var brightCorePoints = cluster
            .Where(point => point.BrightCorePixelCount > 0)
            .ToArray();
        if (brightCorePoints.Length == 0)
        {
            return cluster.OrderByDescending(point => point.CenterY).First();
        }

        var screenCenterX = frameWidth / 2d;
        var start = brightCorePoints
                        .Where(point =>
                            point.CenterY >= frameHeight * _options.PathStartMinimumYRatio &&
                            Math.Abs(point.CenterX - screenCenterX) <=
                            frameWidth * _options.PathStartMaximumOffsetRatio)
                        .OrderByDescending(point => point.CenterY)
                        .FirstOrDefault()
                    ?? brightCorePoints.OrderByDescending(point => point.CenterY).First();

        var scale = Math.Max(0.1, frameHeight / 1080d);
        var linkDistance = Math.Max(12, _options.PathLinkDistanceAt1080P * scale);
        var linkDistanceSquared = linkDistance * linkDistance;
        var distances = Enumerable.Repeat(double.PositiveInfinity, brightCorePoints.Length).ToArray();
        var visited = new bool[brightCorePoints.Length];
        var startIndex = Array.IndexOf(brightCorePoints, start);
        distances[startIndex] = 0;
        for (var step = 0; step < brightCorePoints.Length; step++)
        {
            var currentIndex = -1;
            var currentDistance = double.PositiveInfinity;
            for (var candidate = 0; candidate < brightCorePoints.Length; candidate++)
            {
                if (!visited[candidate] && distances[candidate] < currentDistance)
                {
                    currentIndex = candidate;
                    currentDistance = distances[candidate];
                }
            }

            if (currentIndex < 0)
            {
                break;
            }

            visited[currentIndex] = true;
            for (var candidate = 0; candidate < brightCorePoints.Length; candidate++)
            {
                if (visited[candidate])
                {
                    continue;
                }

                var distanceSquared = DistanceSquared(
                    brightCorePoints[currentIndex],
                    brightCorePoints[candidate]);
                if (distanceSquared > linkDistanceSquared)
                {
                    continue;
                }

                var candidateDistance = currentDistance + Math.Sqrt(distanceSquared);
                if (candidateDistance < distances[candidate])
                {
                    distances[candidate] = candidateDistance;
                }
            }
        }

        var lookAheadDistance = Math.Max(20, _options.LookAheadDistanceAt1080P * scale);
        var minimumUsefulDistance = lookAheadDistance * 0.35;
        var bestIndex = -1;
        var bestScore = double.PositiveInfinity;
        for (var index = 0; index < brightCorePoints.Length; index++)
        {
            var distance = distances[index];
            if (double.IsPositiveInfinity(distance) || distance < minimumUsefulDistance)
            {
                continue;
            }

            var backwardsPenalty = Math.Max(0, brightCorePoints[index].CenterY - start.CenterY) * 2;
            var score = Math.Abs(distance - lookAheadDistance) + backwardsPenalty;
            if (score < bestScore)
            {
                bestScore = score;
                bestIndex = index;
            }
        }

        if (bestIndex >= 0)
        {
            return brightCorePoints[bestIndex];
        }

        var farthestIndex = Enumerable.Range(0, brightCorePoints.Length)
            .Where(index => !double.IsPositiveInfinity(distances[index]))
            .OrderByDescending(index => distances[index])
            .FirstOrDefault();
        return brightCorePoints[farthestIndex];
    }

    private float CalculateConfidence(
        IReadOnlyList<ParticleComponent> cluster,
        double verticalSpan,
        int frameHeight)
    {
        var countScore = Math.Clamp(
            cluster.Count / (double)Math.Max(_options.MinimumParticleCount * 2, 1),
            0,
            1);
        var spanScore = Math.Clamp(verticalSpan / Math.Max(frameHeight * 0.45, 1), 0, 1);
        var proximityScore = Math.Clamp(
            cluster.Max(point => point.CenterY) /
            Math.Max(frameHeight * _options.SearchBottomRatio, 1),
            0,
            1);
        var brightCoreCount = cluster.Count(point => point.BrightCorePixelCount > 0);
        var brightCoreScore = Math.Clamp(
            brightCoreCount / (double)Math.Max(_options.MinimumBrightCoreCount * 2, 1),
            0,
            1);
        return (float)Math.Clamp(
            countScore * 0.25 + spanScore * 0.25 + proximityScore * 0.2 +
            brightCoreScore * 0.3,
            0,
            1);
    }

    private double CalculateTrailSelectionScore(
        IReadOnlyList<ParticleComponent> cluster,
        int frameHeight)
    {
        var countScore = Math.Clamp(
            cluster.Count / (double)Math.Max(_options.MinimumParticleCount * 3, 1),
            0,
            1);
        var verticalSpan = cluster.Max(point => point.CenterY) - cluster.Min(point => point.CenterY);
        var spanScore = Math.Clamp(verticalSpan / Math.Max(frameHeight * 0.45, 1), 0, 1);
        var proximityScore = Math.Clamp(
            cluster.Max(point => point.CenterY) /
            Math.Max(frameHeight * _options.SearchBottomRatio, 1),
            0,
            1);
        var brightCoreCount = cluster.Count(point => point.BrightCorePixelCount > 0);
        var brightCoreScore = Math.Clamp(
            brightCoreCount / (double)Math.Max(_options.MinimumBrightCoreCount * 2, 1),
            0,
            1);
        return countScore * 0.2 + spanScore * 0.2 + proximityScore * 0.25 +
               brightCoreScore * 0.35;
    }

    private static int CountBrightCorePixels(
        Mat labels,
        Mat brightCoreMask,
        int labelIndex,
        Rect bounds)
    {
        using var labelRegion = new Mat(labels, bounds);
        using var currentLabelMask = new Mat();
        Cv2.InRange(
            labelRegion,
            new Scalar(labelIndex),
            new Scalar(labelIndex),
            currentLabelMask);
        using var coreRegion = new Mat(brightCoreMask, bounds);
        using var intersection = new Mat();
        Cv2.BitwiseAnd(currentLabelMask, coreRegion, intersection);
        return Cv2.CountNonZero(intersection);
    }

    private static double DistanceSquared(ParticleComponent left, ParticleComponent right)
    {
        var deltaX = left.CenterX - right.CenterX;
        var deltaY = left.CenterY - right.CenterY;
        return deltaX * deltaX + deltaY * deltaY;
    }

    private static Rect GetBounds(IReadOnlyList<ParticleComponent> particles)
    {
        var left = particles.Min(point => point.Bounds.Left);
        var top = particles.Min(point => point.Bounds.Top);
        var right = particles.Max(point => point.Bounds.Right);
        var bottom = particles.Max(point => point.Bounds.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    private sealed record ParticleComponent(
        Rect Bounds,
        double CenterX,
        double CenterY,
        int BrightCorePixelCount);
}
