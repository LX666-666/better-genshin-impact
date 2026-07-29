using BetterGenshinImpact.GameTask.AutoQuest.Process;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoQuest.Process;

public class QuestProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_DefaultOnlyProcess_ExecutesOnceAndCompletes()
    {
        var process = new QuestProcessParser().Parse("""
                                                     提示 开始
                                                     等待 1
                                                     """);
        var runtime = new RecordingRuntime();
        var runner = CreateRunner(new SequenceDescriptionProvider([]), runtime);

        var result = await runner.RunAsync(
            process,
            new QuestProcessExecutionContext(@"C:\quest\process.json"),
            CancellationToken.None);

        Assert.Equal(QuestProcessRunStatus.Completed, result.Status);
        Assert.Equal(1, result.ExecutedBlocks);
        Assert.Equal(2, result.ExecutedInstructions);
        Assert.Equal(2, runtime.Instructions.Count);
    }

    [Fact]
    public async Task RunAsync_WhenDescriptionMatches_ExecutesMatchedBlock()
    {
        var process = new QuestProcessParser().Parse("""
                                                     与纳西妲对话：
                                                     追踪图标 Bigmap
                                                     任务完成

                                                     默认：
                                                     暂停 未匹配
                                                     """);
        var runtime = new RecordingRuntime();
        var runner = CreateRunner(
            new SequenceDescriptionProvider(["与 纳西妲 对话！"]),
            runtime);

        var result = await runner.RunAsync(
            process,
            new QuestProcessExecutionContext(@"C:\quest\process.json"),
            CancellationToken.None);

        Assert.Equal(QuestProcessRunStatus.Completed, result.Status);
        Assert.Equal(
            [QuestProcessCommandType.TrackIcon, QuestProcessCommandType.Complete],
            runtime.Instructions.Select(instruction => instruction.Type));
    }

    [Fact]
    public async Task RunAsync_WhenDescriptionNeverChanges_StopsAfterBoundedRetries()
    {
        var process = new QuestProcessParser().Parse("""
                                                     与纳西妲对话：
                                                     提示 再试
                                                     """);
        var runtime = new RecordingRuntime();
        var runner = CreateRunner(
            new SequenceDescriptionProvider(["与纳西妲对话"]),
            runtime,
            new QuestProcessRunnerOptions
            {
                DescriptionMatchAttempts = 1,
                DescriptionMatchInterval = TimeSpan.Zero,
                InstructionInterval = TimeSpan.Zero,
                BlockInterval = TimeSpan.Zero,
                MaxSameDescriptionRetries = 2,
                MaxBlockExecutions = 100
            });

        var result = await runner.RunAsync(
            process,
            new QuestProcessExecutionContext(@"C:\quest\process.json"),
            CancellationToken.None);

        Assert.Equal(QuestProcessRunStatus.HumanRequired, result.Status);
        Assert.Equal(3, result.ExecutedBlocks);
        Assert.Equal(3, runtime.Instructions.Count);
    }

    [Fact]
    public async Task RunAsync_WhenNoDescriptionMatches_RefreshesVAndUsesDefaultBlock()
    {
        var process = new QuestProcessParser().Parse("""
                                                     匹配目标：
                                                     暂停 不应执行

                                                     默认：
                                                     任务完成
                                                     """);
        var runtime = new RecordingRuntime();
        var runner = CreateRunner(
            new SequenceDescriptionProvider(["完全不同"]),
            runtime,
            new QuestProcessRunnerOptions
            {
                DescriptionMatchAttempts = 2,
                DescriptionMatchInterval = TimeSpan.Zero,
                InstructionInterval = TimeSpan.Zero,
                BlockInterval = TimeSpan.Zero
            });

        var result = await runner.RunAsync(
            process,
            new QuestProcessExecutionContext(@"C:\quest\process.json"),
            CancellationToken.None);

        Assert.Equal(QuestProcessRunStatus.Completed, result.Status);
        Assert.Equal(2, runtime.RefreshCount);
        Assert.Single(runtime.Instructions);
        Assert.Equal(QuestProcessCommandType.Complete, runtime.Instructions[0].Type);
    }

    private static QuestProcessRunner CreateRunner(
        IQuestDescriptionProvider provider,
        IQuestProcessRuntime runtime,
        QuestProcessRunnerOptions? options = null) =>
        new(
            provider,
            runtime,
            options ?? new QuestProcessRunnerOptions
            {
                DescriptionMatchAttempts = 1,
                DescriptionMatchInterval = TimeSpan.Zero,
                InstructionInterval = TimeSpan.Zero,
                BlockInterval = TimeSpan.Zero
            },
            NullLogger<QuestProcessRunner>.Instance);

    private sealed class SequenceDescriptionProvider(IReadOnlyList<string> descriptions)
        : IQuestDescriptionProvider
    {
        public Task<IReadOnlyList<string>> ReadDescriptionsAsync(CancellationToken ct) =>
            Task.FromResult(descriptions);
    }

    private sealed class RecordingRuntime : IQuestProcessRuntime
    {
        public List<QuestProcessInstruction> Instructions { get; } = [];

        public int RefreshCount { get; private set; }

        public Task PrepareForDescriptionDetectionAsync(CancellationToken ct) => Task.CompletedTask;

        public void RefreshQuestMarker() => RefreshCount++;

        public Task<QuestProcessStepResult> ExecuteAsync(
            QuestProcessInstruction instruction,
            QuestProcessExecutionContext context,
            CancellationToken ct)
        {
            Instructions.Add(instruction);
            return Task.FromResult(instruction.Type switch
            {
                QuestProcessCommandType.Complete => QuestProcessStepResult.Complete(),
                QuestProcessCommandType.Pause => QuestProcessStepResult.Pause(instruction.Argument),
                _ => QuestProcessStepResult.Continue
            });
        }
    }
}
