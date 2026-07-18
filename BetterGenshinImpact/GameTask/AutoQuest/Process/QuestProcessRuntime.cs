using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.Core.Recorder;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoQuest.Navigation;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.AutoQuest.Process;

public sealed record QuestProcessRuntimeOptions
{
    public int DefaultMainUiTimeoutSeconds { get; init; } = 600;

    public TimeSpan DialogueStartGracePeriod { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan DialogueTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public string CombatPartyName { get; init; } = string.Empty;

    public string ElementPartyName { get; init; } = string.Empty;
}
/// <summary>
/// 将自动剧情加载器指令适配到 BetterGI 已有 C# 任务。无法可靠无损转换的角色体型等指令
/// 明确返回 HumanRequired，不会静默跳过或继续误操作。
/// </summary>
public sealed class QuestProcessRuntime : IQuestProcessRuntime
{
    private readonly IQuestProcessIconTracker _iconTracker;
    private readonly QuestProcessRuntimeOptions _options;
    private readonly ILogger<QuestProcessRuntime> _logger;
    private readonly ReturnMainUiTask _returnMainUiTask = new();

    public QuestProcessRuntime(
        IQuestProcessIconTracker? iconTracker = null,
        QuestProcessRuntimeOptions? options = null,
        ILogger<QuestProcessRuntime>? logger = null)
    {
        _iconTracker = iconTracker ?? new QuestProcessIconTracker();
        _options = options ?? new QuestProcessRuntimeOptions();
        _logger = logger ?? App.GetLogger<QuestProcessRuntime>();
    }

    public async Task PrepareForDescriptionDetectionAsync(CancellationToken ct)
    {
        await _returnMainUiTask.Start(ct);
    }

    public void RefreshQuestMarker()
    {
        Simulation.SendInput.SimulateAction(GIActions.QuestNavigation);
    }

    public async Task<QuestProcessStepResult> ExecuteAsync(
        QuestProcessInstruction instruction,
        QuestProcessExecutionContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        _logger.LogInformation(
            "[自动剧情流程] 执行第 {Line} 行：{Command} {Argument}",
            instruction.SourceLine,
            instruction.SourceCommand,
            instruction.Argument);

        return instruction.Type switch
        {
            QuestProcessCommandType.MapTracking => await ExecuteMapTrackingAsync(instruction, context, ct),
            QuestProcessCommandType.KeyMouseScript => await ExecuteKeyMouseScriptAsync(instruction, context, ct),
            QuestProcessCommandType.Dialogue => await ExecuteDialogueAsync(instruction.Argument, ct),
            QuestProcessCommandType.Interact => await ExecuteInteractionAsync(instruction.Argument, ct),
            QuestProcessCommandType.WaitForMainUi => await ExecuteWaitForMainUiAsync(instruction.Argument, ct),
            QuestProcessCommandType.TrackIcon => await ExecuteTrackIconAsync(instruction.Argument, context, ct),
            QuestProcessCommandType.TrackCommission => await ExecuteTrackCommissionAsync(instruction.Argument, context, ct),
            QuestProcessCommandType.Key => await ExecuteKeyAsync(instruction.Argument, ct),
            QuestProcessCommandType.Wait => await ExecuteWaitAsync(instruction.Argument, ct),
            QuestProcessCommandType.Complete => QuestProcessStepResult.Complete(),
            QuestProcessCommandType.Pause => QuestProcessStepResult.Pause(
                string.IsNullOrWhiteSpace(instruction.Argument) ? "流程请求人工处理" : instruction.Argument),
            QuestProcessCommandType.Fight => await ExecuteFightAsync(ct),
            QuestProcessCommandType.AutoPick => ExecuteAutoPick(instruction.Argument),
            QuestProcessCommandType.SwitchCharacters => QuestProcessStepResult.HumanRequired(
                $"角色编队指令尚需人工确认：{instruction.Argument}"),
            QuestProcessCommandType.ShowMessage => ExecuteMessage(instruction.Argument),
            QuestProcessCommandType.SetTime => await ExecuteSetTimeAsync(instruction.Argument, ct),
            QuestProcessCommandType.Click => ExecuteClick(instruction.Argument),
            QuestProcessCommandType.SwitchParty => await ExecuteSwitchPartyAsync(instruction.Argument, ct),
            QuestProcessCommandType.ImageMatch => ExecuteImageMatch(instruction, context),
            QuestProcessCommandType.SwitchBodyType => QuestProcessStepResult.HumanRequired(
                $"角色体型切换尚需人工确认：{instruction.Argument}"),
            QuestProcessCommandType.ClickText => ExecuteClickText(instruction.Argument),
            QuestProcessCommandType.ReturnMainUi => await ExecuteReturnMainUiAsync(ct),
            _ => QuestProcessStepResult.HumanRequired(
                $"不支持的流程指令：{instruction.SourceCommand}（第 {instruction.SourceLine} 行）")
        };
    }

    private static async Task<QuestProcessStepResult> ExecuteMapTrackingAsync(
        QuestProcessInstruction instruction,
        QuestProcessExecutionContext context,
        CancellationToken ct)
    {
        var path = ResolveProcessPath(context.ProcessFilePath, instruction.Argument);
        var task = PathingTask.BuildFromFilePath(path);
        if (task == null)
        {
            return QuestProcessStepResult.Failed($"地图路线文件解析失败：{path}");
        }

        var executor = new PathExecutor(ct)
        {
            PartyConfig = new Core.Config.PathingPartyConfig
            {
                AutoFightEnabled = false,
                AutoPickEnabled = true,
                AutoSkipEnabled = true
            }
        };
        await executor.Pathing(task);
        return executor.SuccessEnd
            ? QuestProcessStepResult.Continue
            : QuestProcessStepResult.Failed($"地图路线未完整执行：{path}");
    }

    private static async Task<QuestProcessStepResult> ExecuteKeyMouseScriptAsync(
        QuestProcessInstruction instruction,
        QuestProcessExecutionContext context,
        CancellationToken ct)
    {
        var path = ResolveProcessPath(context.ProcessFilePath, instruction.Argument);
        var json = await File.ReadAllTextAsync(path, ct);
        await KeyMouseMacroPlayer.PlayMacro(json, ct, false);
        return QuestProcessStepResult.Continue;
    }

    private async Task<QuestProcessStepResult> ExecuteDialogueAsync(string targetName, CancellationToken ct)
    {
        RefreshQuestMarker();
        var clicked = !string.IsNullOrWhiteSpace(targetName) &&
                      await TryAltClickTextAsync(targetName, new Rect(1150, 300, 350, 400), ct);
        if (!clicked)
        {
            Simulation.SendInput.SimulateAction(GIActions.PickUpOrInteract);
        }

        try
        {
            TaskTriggerDispatcher.Instance().AddTrigger("AutoSkip", null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "自动剧情触发器当前不可动态添加，沿用现有触发器配置");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var sawTalk = false;
        while (DateTimeOffset.UtcNow - startedAt < _options.DialogueTimeout)
        {
            ct.ThrowIfCancellationRequested();
            using var frame = TaskControl.CaptureToRectArea();
            sawTalk |= Bv.IsInTalkUi(frame);
            if (sawTalk && Bv.IsInMainUi(frame))
            {
                return QuestProcessStepResult.Continue;
            }

            if (!sawTalk &&
                DateTimeOffset.UtcNow - startedAt >= _options.DialogueStartGracePeriod &&
                Bv.IsInMainUi(frame))
            {
                return QuestProcessStepResult.HumanRequired("未能触发目标对话");
            }

            await Task.Delay(500, ct);
        }

        return QuestProcessStepResult.HumanRequired("等待对话结束超时");
    }

    private async Task<QuestProcessStepResult> ExecuteInteractionAsync(string targetName, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(targetName) &&
            await TryAltClickTextAsync(targetName, new Rect(0, 0, 1920, 1080), ct))
        {
            return QuestProcessStepResult.Continue;
        }

        Simulation.SendInput.SimulateAction(GIActions.PickUpOrInteract);
        return QuestProcessStepResult.Continue;
    }

    private async Task<QuestProcessStepResult> ExecuteWaitForMainUiAsync(string argument, CancellationToken ct)
    {
        var timeoutSeconds = ParsePositiveInt(argument, _options.DefaultMainUiTimeoutSeconds);
        for (var second = 0; second < timeoutSeconds; second++)
        {
            ct.ThrowIfCancellationRequested();
            using var frame = TaskControl.CaptureToRectArea();
            if (Bv.IsInMainUi(frame))
            {
                return QuestProcessStepResult.Continue;
            }

            await Task.Delay(1000, ct);
        }

        _logger.LogWarning("等待返回主界面超时，按插件行为继续后续流程");
        return QuestProcessStepResult.Continue;
    }

    private async Task<QuestProcessStepResult> ExecuteTrackIconAsync(
        string iconType,
        QuestProcessExecutionContext context,
        CancellationToken ct)
    {
        var navigation = await _iconTracker.TrackIconAsync(
            iconType,
            context.MapName,
            context.MapMatchMethod,
            ct);
        return ConvertNavigationResult(navigation);
    }

    private async Task<QuestProcessStepResult> ExecuteTrackCommissionAsync(
        string specification,
        QuestProcessExecutionContext context,
        CancellationToken ct)
    {
        var navigation = await _iconTracker.TrackCommissionAsync(
            specification,
            context.MapName,
            context.MapMatchMethod,
            ct);
        return ConvertNavigationResult(navigation);
    }

    private static async Task<QuestProcessStepResult> ExecuteKeyAsync(string argument, CancellationToken ct)
    {
        var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var action = parts.Length > 0 ? parts[0] : "点按";
        var keyPartIndex = action is "长按" or "松开" or "按下" or "点按" ? 1 : 0;
        if (parts.Length <= keyPartIndex)
        {
            return QuestProcessStepResult.Failed($"按键指令缺少键值：{argument}");
        }

        var keys = parts[keyPartIndex]
            .Replace('，', ',')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseKey)
            .ToArray();
        var duration = action == "长按" && parts.Length > keyPartIndex + 1
            ? ParsePositiveInt(parts[keyPartIndex + 1], 5000)
            : 5000;

        if (action == "按下")
        {
            foreach (var key in keys)
            {
                Simulation.SendInput.Keyboard.KeyDown(key);
            }

            return QuestProcessStepResult.Continue;
        }

        if (action == "松开")
        {
            foreach (var key in keys)
            {
                Simulation.SendInput.Keyboard.KeyUp(key);
            }

            return QuestProcessStepResult.Continue;
        }

        if (action == "长按")
        {
            try
            {
                foreach (var key in keys)
                {
                    Simulation.SendInput.Keyboard.KeyDown(key);
                }

                await Task.Delay(duration, ct);
            }
            finally
            {
                foreach (var key in keys)
                {
                    Simulation.SendInput.Keyboard.KeyUp(key);
                }
            }

            return QuestProcessStepResult.Continue;
        }

        foreach (var key in keys)
        {
            Simulation.SendInput.Keyboard.KeyPress(key);
            await Task.Delay(100, ct);
        }

        return QuestProcessStepResult.Continue;
    }

    private static async Task<QuestProcessStepResult> ExecuteWaitAsync(string argument, CancellationToken ct)
    {
        await Task.Delay(ParsePositiveInt(argument, 5000), ct);
        return QuestProcessStepResult.Continue;
    }

    private static async Task<QuestProcessStepResult> ExecuteFightAsync(CancellationToken ct)
    {
        await new AutoFightTask(new AutoFightParam()).Start(ct);
        return QuestProcessStepResult.Continue;
    }

    private QuestProcessStepResult ExecuteAutoPick(string argument)
    {
        if (argument.Equals("关闭", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("当前 C# 流程不会清空全局触发器；自动拾取关闭指令暂不改变全局状态");
            return QuestProcessStepResult.Continue;
        }

        try
        {
            TaskTriggerDispatcher.Instance().AddTrigger("AutoPick", null);
            return QuestProcessStepResult.Continue;
        }
        catch (Exception ex)
        {
            return QuestProcessStepResult.Failed($"启用自动拾取失败：{ex.Message}");
        }
    }

    private QuestProcessStepResult ExecuteMessage(string message)
    {
        _logger.LogInformation("[自动剧情提示] {Message}", message);
        return QuestProcessStepResult.Continue;
    }

    private static async Task<QuestProcessStepResult> ExecuteSetTimeAsync(string argument, CancellationToken ct)
    {
        if (!TryParseTime(argument, out var hour, out var minute))
        {
            return QuestProcessStepResult.HumanRequired("调时间指令需要 C# 格式参数，例如：调时间 08:00");
        }

        await new SetTimeTask().Start(hour, minute, ct);
        return QuestProcessStepResult.Continue;
    }

    private static QuestProcessStepResult ExecuteClick(string argument)
    {
        if (!TryParseNumbers(argument, 2, out var values))
        {
            return QuestProcessStepResult.Failed($"点击坐标格式错误：{argument}");
        }

        GameCaptureRegion.GameRegion1080PPosClick(values[0], values[1]);
        return QuestProcessStepResult.Continue;
    }

    private async Task<QuestProcessStepResult> ExecuteSwitchPartyAsync(string argument, CancellationToken ct)
    {
        var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return QuestProcessStepResult.HumanRequired("切换队伍指令缺少队伍类型或名称");
        }

        var partyName = parts[0] switch
        {
            "战斗" => _options.CombatPartyName,
            "元素采集" => _options.ElementPartyName,
            _ => parts[0]
        };
        if (string.IsNullOrWhiteSpace(partyName))
        {
            return QuestProcessStepResult.HumanRequired($"尚未配置 {parts[0]} 队伍名称");
        }

        var switched = await new SwitchPartyTask().Start(partyName, ct);
        return switched
            ? QuestProcessStepResult.Continue
            : QuestProcessStepResult.Failed($"切换队伍失败：{partyName}");
    }

    private static QuestProcessStepResult ExecuteImageMatch(
        QuestProcessInstruction instruction,
        QuestProcessExecutionContext context)
    {
        var path = ResolveProcessPath(context.ProcessFilePath, instruction.Argument);
        using var template = Cv2.ImRead(path, ImreadModes.Color);
        if (template.Empty())
        {
            return QuestProcessStepResult.Failed($"图像模板读取失败：{path}");
        }

        var recognition = new RecognitionObject
        {
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = template,
            Use3Channels = true,
            Threshold = 0.8
        }.InitTemplate();
        using var frame = TaskControl.CaptureToRectArea();
        using var result = frame.Find(recognition);
        if (!result.IsExist())
        {
            return QuestProcessStepResult.HumanRequired($"未找到流程图像：{instruction.Argument}");
        }

        result.Click();
        return QuestProcessStepResult.Continue;
    }

    private static QuestProcessStepResult ExecuteClickText(string argument)
    {
        var values = argument.Replace('，', ',').Split(',', StringSplitOptions.TrimEntries);
        if (values.Length < 5 ||
            !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
            !int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ||
            !int.TryParse(values[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(values[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
        {
            return QuestProcessStepResult.Failed($"点击文字参数格式错误：{argument}");
        }

        var target = string.Join(',', values.Skip(4)).Trim();
        using var frame = TaskControl.CaptureToRectArea();
        var roi = ScaleReferenceRect(new Rect(x, y, width, height), frame.Width, frame.Height)
            .ClampTo(frame.SrcMat);
        var results = frame.FindMulti(new RecognitionObject
        {
            RecognitionType = RecognitionTypes.Ocr,
            RegionOfInterest = roi
        });

        try
        {
            var match = results.FirstOrDefault(result =>
                result.Text.Contains(target, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                return QuestProcessStepResult.HumanRequired($"未找到文字：{target}");
            }

            match.Click();
            return QuestProcessStepResult.Continue;
        }
        finally
        {
            results.ForEach(result => result.Dispose());
        }
    }

    private async Task<QuestProcessStepResult> ExecuteReturnMainUiAsync(CancellationToken ct)
    {
        await _returnMainUiTask.Start(ct);
        return QuestProcessStepResult.Continue;
    }

    private static QuestProcessStepResult ConvertNavigationResult(NavigationResult result) => result.Status switch
    {
        NavigationStatus.Arrived => QuestProcessStepResult.Continue,
        NavigationStatus.Cancelled => QuestProcessStepResult.Failed("图标追踪已取消"),
        NavigationStatus.NeedReplan or NavigationStatus.HumanRequired =>
            QuestProcessStepResult.HumanRequired(result.Message),
        _ => QuestProcessStepResult.Failed(result.Message)
    };

    private static string ResolveProcessPath(string processFilePath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("流程指令缺少相对文件路径");
        }

        var processDirectory = Path.GetDirectoryName(Path.GetFullPath(processFilePath))
                               ?? throw new InvalidOperationException("无法确定流程文件目录");
        var fullPath = Path.GetFullPath(Path.Combine(processDirectory, relativePath.Trim()));
        var directoryPrefix = processDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                              Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"流程子文件越出任务目录：{relativePath}");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("流程子文件不存在", fullPath);
        }

        return fullPath;
    }

    private static User32.VK ParseKey(string key)
    {
        var normalized = key.Trim().ToUpperInvariant();
        if (normalized.StartsWith("VK_", StringComparison.Ordinal))
        {
            normalized = normalized[3..];
        }

        var special = normalized switch
        {
            "ESC" or "ESCAPE" => User32.VK.VK_ESCAPE,
            "SPACE" => User32.VK.VK_SPACE,
            "ENTER" or "RETURN" => User32.VK.VK_RETURN,
            "SHIFT" => User32.VK.VK_SHIFT,
            "CTRL" or "CONTROL" => User32.VK.VK_CONTROL,
            "ALT" or "MENU" => User32.VK.VK_MENU,
            "TAB" => User32.VK.VK_TAB,
            _ => default
        };
        if (special != default)
        {
            return special;
        }

        if (Enum.TryParse<User32.VK>($"VK_{normalized}", true, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"不支持的按键：{key}");
    }

    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;

    private static bool TryParseNumbers(string text, int count, out double[] values)
    {
        var parts = text.Replace('，', ',').Split(',', StringSplitOptions.TrimEntries);
        values = new double[count];
        if (parts.Length < count)
        {
            return false;
        }

        for (var index = 0; index < count; index++)
        {
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseTime(string text, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;
        var parts = text.Replace('：', ':')
            .Split([':', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 1 &&
               int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hour) &&
               (parts.Length == 1 ||
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minute)) &&
               hour is >= 0 and <= 23 &&
               minute is >= 0 and <= 59;
    }

    private static Rect ScaleReferenceRect(Rect reference, int width, int height) => new(
        (int)Math.Round(reference.X * width / 1920d),
        (int)Math.Round(reference.Y * height / 1080d),
        Math.Max(1, (int)Math.Round(reference.Width * width / 1920d)),
        Math.Max(1, (int)Math.Round(reference.Height * height / 1080d)));

    private static async Task<bool> TryAltClickTextAsync(
        string target,
        Rect referenceRegion,
        CancellationToken ct)
    {
        using var frame = TaskControl.CaptureToRectArea();
        var roi = ScaleReferenceRect(referenceRegion, frame.Width, frame.Height).ClampTo(frame.SrcMat);
        var results = frame.FindMulti(new RecognitionObject
        {
            RecognitionType = RecognitionTypes.Ocr,
            RegionOfInterest = roi
        });

        try
        {
            var match = results.FirstOrDefault(result =>
                result.Text.Contains(target, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                return false;
            }

            Simulation.SendInput.Keyboard.KeyDown(User32.VK.VK_MENU);
            try
            {
                await Task.Delay(200, ct);
                match.Click();
                await Task.Delay(200, ct);
            }
            finally
            {
                Simulation.SendInput.Keyboard.KeyUp(User32.VK.VK_MENU);
            }

            return true;
        }
        finally
        {
            results.ForEach(result => result.Dispose());
        }
    }
}
