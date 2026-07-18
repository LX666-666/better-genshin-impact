using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoQuest.Process;

public enum QuestProcessCommandType
{
    MapTracking,
    KeyMouseScript,
    Dialogue,
    Interact,
    WaitForMainUi,
    TrackIcon,
    TrackCommission,
    Key,
    Wait,
    Complete,
    Pause,
    Fight,
    AutoPick,
    SwitchCharacters,
    ShowMessage,
    SetTime,
    Click,
    SwitchParty,
    ImageMatch,
    SwitchBodyType,
    ClickText,
    ReturnMainUi,
    Unknown
}
public sealed record QuestProcessInstruction(
    QuestProcessCommandType Type,
    string Argument,
    int SourceLine,
    string SourceCommand);

public sealed record QuestProcessBlock(
    string Description,
    string NormalizedDescription,
    bool IsDefault,
    IReadOnlyList<QuestProcessInstruction> Instructions);

public sealed record QuestProcessDefinition(
    string Author,
    string Description,
    IReadOnlyList<QuestProcessBlock> Blocks,
    string SourcePath = "");

public enum QuestProcessRunStatus
{
    Completed,
    Paused,
    HumanRequired,
    Cancelled,
    Failed
}

public sealed record QuestProcessRunResult(
    QuestProcessRunStatus Status,
    string Message,
    int ExecutedBlocks = 0,
    int ExecutedInstructions = 0)
{
    public bool IsSuccess => Status == QuestProcessRunStatus.Completed;
}

public enum QuestProcessStepStatus
{
    Continue,
    Complete,
    Pause,
    HumanRequired,
    Failed
}

public sealed record QuestProcessStepResult(QuestProcessStepStatus Status, string Message = "")
{
    public static QuestProcessStepResult Continue { get; } = new(QuestProcessStepStatus.Continue);

    public static QuestProcessStepResult Complete(string message = "流程声明任务完成") =>
        new(QuestProcessStepStatus.Complete, message);

    public static QuestProcessStepResult Pause(string message) =>
        new(QuestProcessStepStatus.Pause, message);

    public static QuestProcessStepResult HumanRequired(string message) =>
        new(QuestProcessStepStatus.HumanRequired, message);

    public static QuestProcessStepResult Failed(string message) =>
        new(QuestProcessStepStatus.Failed, message);
}
