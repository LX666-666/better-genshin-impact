using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoQuest.Process;

public sealed record QuestProcessRunnerOptions
{
    public double DescriptionSimilarityThreshold { get; init; } = 0.9;

    public int DescriptionMatchAttempts { get; init; } = 13;

    public TimeSpan DescriptionMatchInterval { get; init; } = TimeSpan.FromSeconds(1);

    public int MaxBlockExecutions { get; init; } = 200;

    public int MaxSameDescriptionRetries { get; init; } = 2;

    public TimeSpan InstructionInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan BlockInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}
public sealed record QuestProcessExecutionContext(
    string ProcessFilePath,
    string MapName = "Teyvat",
    string MapMatchMethod = "TemplateMatch");

public interface IQuestDescriptionProvider
{
    Task<IReadOnlyList<string>> ReadDescriptionsAsync(CancellationToken ct);
}

public interface IQuestProcessRuntime
{
    Task PrepareForDescriptionDetectionAsync(CancellationToken ct);

    void RefreshQuestMarker();

    Task<QuestProcessStepResult> ExecuteAsync(
        QuestProcessInstruction instruction,
        QuestProcessExecutionContext context,
        CancellationToken ct);
}

/// <summary>
/// 读取左上角任务描述区域。区域坐标沿用插件的 1920x1080 基准 (75,240,280,43)。
/// </summary>
public sealed class QuestDescriptionProvider : IQuestDescriptionProvider
{
    public Task<IReadOnlyList<string>> ReadDescriptionsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var frame = TaskControl.CaptureToRectArea();
        var roi = ScaleDescriptionRegion(frame.Width, frame.Height).ClampTo(frame.SrcMat);
        if (roi.Width <= 0 || roi.Height <= 0)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var results = frame.FindMulti(new RecognitionObject
        {
            RecognitionType = RecognitionTypes.Ocr,
            RegionOfInterest = roi
        });

        try
        {
            return Task.FromResult<IReadOnlyList<string>>(
                results.Select(result => result.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToArray());
        }
        finally
        {
            results.ForEach(result => result.Dispose());
        }
    }

    internal static Rect ScaleDescriptionRegion(int width, int height) => new(
        (int)Math.Round(75d * width / 1920d),
        (int)Math.Round(240d * height / 1080d),
        Math.Max(1, (int)Math.Round(280d * width / 1920d)),
        Math.Max(1, (int)Math.Round(43d * height / 1080d)));
}

/// <summary>
/// C# 版 process.json 编排器：按任务描述相似度选择块、按同名块出现顺序执行，
/// 并保留插件的 13 次匹配、V 刷新和有限重试规则。
/// </summary>
public sealed class QuestProcessRunner
{
    private readonly IQuestDescriptionProvider _descriptionProvider;
    private readonly IQuestProcessRuntime _runtime;
    private readonly QuestProcessRunnerOptions _options;
    private readonly ILogger<QuestProcessRunner> _logger;

    public QuestProcessRunner(
        IQuestDescriptionProvider descriptionProvider,
        IQuestProcessRuntime runtime,
        QuestProcessRunnerOptions? options = null,
        ILogger<QuestProcessRunner>? logger = null)
    {
        _descriptionProvider = descriptionProvider ?? throw new ArgumentNullException(nameof(descriptionProvider));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = options ?? new QuestProcessRunnerOptions();
        _logger = logger ?? App.GetLogger<QuestProcessRunner>();
    }

    public async Task<QuestProcessRunResult> RunAsync(
        QuestProcessDefinition process,
        QuestProcessExecutionContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(context);

        var executedBlocks = 0;
        var executedInstructions = 0;
        try
        {
            if (process.Blocks.Count == 0)
            {
                return new QuestProcessRunResult(QuestProcessRunStatus.Failed, "流程中没有可执行块");
            }

            var defaultBlock = process.Blocks.FirstOrDefault(block => block.IsDefault);
            var taskBlocks = process.Blocks.Where(block => !block.IsDefault).ToArray();
            if (taskBlocks.Length == 0)
            {
                if (defaultBlock == null)
                {
                    return new QuestProcessRunResult(QuestProcessRunStatus.Failed, "流程中没有默认块");
                }

                var step = await ExecuteBlockAsync(defaultBlock, context, ct);
                executedBlocks++;
                executedInstructions += step.ExecutedInstructions;
                return ConvertStepResult(
                    step.Result,
                    "默认流程执行完成",
                    executedBlocks,
                    executedInstructions);
            }

            var groups = taskBlocks
                .GroupBy(block => block.NormalizedDescription)
                .ToDictionary(group => group.Key, group => group.ToArray());
            var executionCounters = new Dictionary<string, int>();
            string? lastMatchedDescription = null;
            var sameDescriptionRetries = 0;

            while (executedBlocks < _options.MaxBlockExecutions)
            {
                ct.ThrowIfCancellationRequested();
                await _runtime.PrepareForDescriptionDetectionAsync(ct);
                var matchedDescription = await DetectDescriptionAsync(groups.Keys, ct);

                if (!string.Equals(matchedDescription, lastMatchedDescription, StringComparison.Ordinal))
                {
                    sameDescriptionRetries = 0;
                    lastMatchedDescription = matchedDescription;
                    if (matchedDescription != null)
                    {
                        executionCounters.TryGetValue(matchedDescription, out var count);
                        executionCounters[matchedDescription] = count + 1;
                    }
                }
                else
                {
                    sameDescriptionRetries++;
                }

                if (sameDescriptionRetries > _options.MaxSameDescriptionRetries)
                {
                    return new QuestProcessRunResult(
                        QuestProcessRunStatus.HumanRequired,
                        "同一任务描述连续执行后仍未变化",
                        executedBlocks,
                        executedInstructions);
                }

                QuestProcessBlock? block;
                if (matchedDescription != null && groups.TryGetValue(matchedDescription, out var candidates))
                {
                    var executionNumber = executionCounters[matchedDescription];
                    block = candidates[Math.Min(executionNumber - 1, candidates.Length - 1)];
                    _logger.LogInformation(
                        "[自动剧情流程] 匹配任务块 {Description}，执行同名块第 {Index} 个",
                        block.Description,
                        Math.Min(executionNumber, candidates.Length));
                }
                else
                {
                    block = defaultBlock;
                    if (block == null)
                    {
                        return new QuestProcessRunResult(
                            QuestProcessRunStatus.HumanRequired,
                            "未匹配任务描述且不存在默认块",
                            executedBlocks,
                            executedInstructions);
                    }

                    _logger.LogInformation("[自动剧情流程] 未匹配任务描述，执行默认块");
                }

                var step = await ExecuteBlockAsync(block, context, ct);
                executedBlocks++;
                executedInstructions += step.ExecutedInstructions;
                if (step.Result.Status != QuestProcessStepStatus.Continue)
                {
                    return ConvertStepResult(
                        step.Result,
                        "流程执行完成",
                        executedBlocks,
                        executedInstructions);
                }

                await Delay(_options.BlockInterval, ct);
            }

            return new QuestProcessRunResult(
                QuestProcessRunStatus.HumanRequired,
                "流程块执行次数达到上限",
                executedBlocks,
                executedInstructions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new QuestProcessRunResult(
                QuestProcessRunStatus.Cancelled,
                "流程已取消",
                executedBlocks,
                executedInstructions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "自动剧情流程执行失败");
            return new QuestProcessRunResult(
                QuestProcessRunStatus.Failed,
                ex.Message,
                executedBlocks,
                executedInstructions);
        }
    }

    private async Task<string?> DetectDescriptionAsync(
        IEnumerable<string> knownDescriptions,
        CancellationToken ct)
    {
        var descriptions = knownDescriptions.Where(value => value.Length > 0).ToArray();
        for (var attempt = 0; attempt < Math.Max(1, _options.DescriptionMatchAttempts); attempt++)
        {
            var recognizedTexts = await _descriptionProvider.ReadDescriptionsAsync(ct);
            var bestSimilarity = 0d;
            string? bestDescription = null;
            foreach (var recognizedText in recognizedTexts)
            {
                foreach (var description in descriptions)
                {
                    var similarity = QuestProcessTextMatcher.Similarity(recognizedText, description);
                    if (similarity > bestSimilarity)
                    {
                        bestSimilarity = similarity;
                        bestDescription = description;
                    }
                }
            }

            if (bestSimilarity >= _options.DescriptionSimilarityThreshold)
            {
                return bestDescription;
            }

            _runtime.RefreshQuestMarker();
            await Delay(_options.DescriptionMatchInterval, ct);
        }

        return null;
    }

    private async Task<(QuestProcessStepResult Result, int ExecutedInstructions)> ExecuteBlockAsync(
        QuestProcessBlock block,
        QuestProcessExecutionContext context,
        CancellationToken ct)
    {
        var executedInstructions = 0;
        foreach (var instruction in block.Instructions)
        {
            ct.ThrowIfCancellationRequested();
            var result = await _runtime.ExecuteAsync(instruction, context, ct);
            executedInstructions++;
            if (result.Status != QuestProcessStepStatus.Continue)
            {
                return (result, executedInstructions);
            }

            await Delay(_options.InstructionInterval, ct);
        }

        return (QuestProcessStepResult.Continue, executedInstructions);
    }

    private static QuestProcessRunResult ConvertStepResult(
        QuestProcessStepResult step,
        string continueMessage,
        int executedBlocks,
        int executedInstructions) => step.Status switch
        {
            QuestProcessStepStatus.Continue or QuestProcessStepStatus.Complete => new QuestProcessRunResult(
                QuestProcessRunStatus.Completed,
                string.IsNullOrWhiteSpace(step.Message) ? continueMessage : step.Message,
                executedBlocks,
                executedInstructions),
            QuestProcessStepStatus.Pause => new QuestProcessRunResult(
                QuestProcessRunStatus.Paused,
                step.Message,
                executedBlocks,
                executedInstructions),
            QuestProcessStepStatus.HumanRequired => new QuestProcessRunResult(
                QuestProcessRunStatus.HumanRequired,
                step.Message,
                executedBlocks,
                executedInstructions),
            _ => new QuestProcessRunResult(
                QuestProcessRunStatus.Failed,
                step.Message,
                executedBlocks,
                executedInstructions)
        };

    private static Task Delay(TimeSpan delay, CancellationToken ct) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct);
}
