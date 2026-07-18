using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoQuest.Process;

public sealed record QuestProcessTaskParam(
    string ProcessFilePath,
    string MapName = "Teyvat",
    string MapMatchMethod = "TemplateMatch");

/// <summary>
/// 可由后续 UI、调度器或自动任务状态机直接调用的 C# 自动剧情流程入口。
/// </summary>
public sealed class QuestProcessTask : ISoloTask<QuestProcessRunResult>
{
    private readonly QuestProcessTaskParam _param;
    private readonly QuestProcessLoader _loader;
    private readonly QuestProcessRunner _runner;
    private readonly ILogger<QuestProcessTask> _logger;

    public QuestProcessTask(
        QuestProcessTaskParam param,
        QuestProcessLoader? loader = null,
        QuestProcessRunner? runner = null,
        ILogger<QuestProcessTask>? logger = null)
    {
        _param = param ?? throw new ArgumentNullException(nameof(param));
        _loader = loader ?? new QuestProcessLoader();
        _runner = runner ?? new QuestProcessRunner(
            new QuestDescriptionProvider(),
            new QuestProcessRuntime());
        _logger = logger ?? App.GetLogger<QuestProcessTask>();
    }

    public string Name => "自动剧情流程";

    async Task ISoloTask.Start(CancellationToken ct)
    {
        await Start(ct);
    }

    public async Task<QuestProcessRunResult> Start(CancellationToken ct)
    {
        try
        {
            var process = await _loader.LoadAsync(_param.ProcessFilePath, ct);
            _logger.LogInformation(
                "[自动剧情流程] 加载完成：{Path}，共 {BlockCount} 个流程块",
                process.SourcePath,
                process.Blocks.Count);
            return await _runner.RunAsync(
                process,
                new QuestProcessExecutionContext(
                    process.SourcePath,
                    _param.MapName,
                    _param.MapMatchMethod),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new QuestProcessRunResult(QuestProcessRunStatus.Cancelled, "流程已取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "自动剧情流程启动失败");
            return new QuestProcessRunResult(QuestProcessRunStatus.Failed, ex.Message);
        }
    }
}
