using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace BetterGenshinImpact.GameTask.AutoQuest.Process;

/// <summary>
/// C# 版自动剧情流程解析器。兼容插件的扩展文本格式、旧逐行格式和 JSON 数组格式。
/// </summary>
public sealed class QuestProcessParser
{
    private static readonly HashSet<string> DefaultBlockNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "默认",
        "無任務描述字符串",
        "无任务描述字符串",
        "nomatch",
        "default",
        "超时"
    };

    public QuestProcessDefinition Parse(string content, string sourcePath = "")
    {
        ArgumentNullException.ThrowIfNull(content);

        var trimmed = content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                return ParseJsonArray(trimmed, sourcePath);
            }
            catch (JsonException)
            {
                // 与原插件一致：JSON 解析失败后继续按文本流程解析。
            }
        }

        return ParseText(content, sourcePath);
    }

    private static QuestProcessDefinition ParseText(string content, string sourcePath)
    {
        var author = string.Empty;
        var description = string.Empty;
        var blocks = new List<MutableBlock>();
        MutableBlock? currentBlock = null;
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = StripComment(lines[index]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (TrySplitMetadata(line, out var metadataKey, out var metadataValue))
            {
                if (metadataKey.Equals("作者", StringComparison.OrdinalIgnoreCase))
                {
                    author = metadataValue;
                    continue;
                }

                if (metadataKey.Equals("描述", StringComparison.OrdinalIgnoreCase))
                {
                    description = metadataValue;
                    continue;
                }
            }

            if (IsBlockHeader(line))
            {
                var blockDescription = line[..^1].Trim();
                currentBlock = new MutableBlock(
                    blockDescription,
                    QuestProcessTextMatcher.Normalize(blockDescription),
                    DefaultBlockNames.Contains(blockDescription));
                blocks.Add(currentBlock);
                continue;
            }

            if (currentBlock == null)
            {
                currentBlock = new MutableBlock(
                    "默认",
                    QuestProcessTextMatcher.Normalize("默认"),
                    true);
                blocks.Add(currentBlock);
            }

            currentBlock.Instructions.Add(ParseInstruction(line, index + 1));
        }

        return BuildDefinition(author, description, blocks, sourcePath);
    }

    private static QuestProcessDefinition ParseJsonArray(string content, string sourcePath)
    {
        using var document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("流程 JSON 根节点必须是数组");
        }

        var block = new MutableBlock(
            "默认",
            QuestProcessTextMatcher.Normalize("默认"),
            true);
        var sourceLine = 1;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    block.Instructions.Add(ParseInstruction(element.GetString() ?? string.Empty, sourceLine));
                    break;
                case JsonValueKind.Object:
                    if (!element.TryGetProperty("type", out var typeElement) ||
                        typeElement.ValueKind != JsonValueKind.String)
                    {
                        block.Instructions.Add(new QuestProcessInstruction(
                            QuestProcessCommandType.Unknown,
                            element.GetRawText(),
                            sourceLine,
                            string.Empty));
                        break;
                    }

                    var sourceCommand = typeElement.GetString() ?? string.Empty;
                    var argument = element.TryGetProperty("data", out var dataElement)
                        ? JsonValueToArgument(dataElement)
                        : string.Empty;
                    block.Instructions.Add(new QuestProcessInstruction(
                        MapCommand(sourceCommand),
                        argument,
                        sourceLine,
                        sourceCommand));
                    break;
                default:
                    block.Instructions.Add(new QuestProcessInstruction(
                        QuestProcessCommandType.Unknown,
                        element.GetRawText(),
                        sourceLine,
                        string.Empty));
                    break;
            }

            sourceLine++;
        }

        return BuildDefinition(string.Empty, string.Empty, [block], sourcePath);
    }

    private static QuestProcessInstruction ParseInstruction(string line, int sourceLine)
    {
        var separator = line.IndexOfAny([' ', '\t']);
        var command = separator < 0 ? line : line[..separator];
        var argument = separator < 0 ? string.Empty : line[(separator + 1)..].Trim();

        if (command.Equals("F", StringComparison.OrdinalIgnoreCase))
        {
            return new QuestProcessInstruction(
                QuestProcessCommandType.Dialogue,
                argument,
                sourceLine,
                command);
        }

        var mapped = MapCommand(command);
        if (mapped == QuestProcessCommandType.Unknown && line.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            mapped = QuestProcessCommandType.MapTracking;
            argument = line;
        }

        if (mapped == QuestProcessCommandType.Key && argument.Length == 0)
        {
            argument = "F";
        }

        return new QuestProcessInstruction(mapped, argument, sourceLine, command);
    }

    private static QuestProcessCommandType MapCommand(string command) => command switch
    {
        "地图追踪" => QuestProcessCommandType.MapTracking,
        "键鼠脚本" => QuestProcessCommandType.KeyMouseScript,
        "对话" => QuestProcessCommandType.Dialogue,
        "交互" => QuestProcessCommandType.Interact,
        "等待返回主界面" => QuestProcessCommandType.WaitForMainUi,
        "追踪图标" => QuestProcessCommandType.TrackIcon,
        "追踪委托" => QuestProcessCommandType.TrackCommission,
        "按键" => QuestProcessCommandType.Key,
        "等待" => QuestProcessCommandType.Wait,
        "任务完成" => QuestProcessCommandType.Complete,
        "暂停" => QuestProcessCommandType.Pause,
        "战斗" => QuestProcessCommandType.Fight,
        "自动拾取" => QuestProcessCommandType.AutoPick,
        "切换" or "切换角色" => QuestProcessCommandType.SwitchCharacters,
        "提示" => QuestProcessCommandType.ShowMessage,
        "调时间" => QuestProcessCommandType.SetTime,
        "点击" => QuestProcessCommandType.Click,
        "切换队伍" => QuestProcessCommandType.SwitchParty,
        "图像匹配" => QuestProcessCommandType.ImageMatch,
        "切换角色体型" => QuestProcessCommandType.SwitchBodyType,
        "点击文字" => QuestProcessCommandType.ClickText,
        "返回主界面" => QuestProcessCommandType.ReturnMainUi,
        _ => QuestProcessCommandType.Unknown
    };

    private static string JsonValueToArgument(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => element.GetRawText()
    };

    private static bool TrySplitMetadata(string line, out string key, out string value)
    {
        var chinese = line.IndexOf('：');
        var english = line.IndexOf(':');
        var delimiter = chinese < 0
            ? english
            : english < 0
                ? chinese
                : Math.Min(chinese, english);
        if (delimiter <= 0)
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        key = line[..delimiter].Trim();
        value = line[(delimiter + 1)..].Trim();
        return true;
    }

    private static bool IsBlockHeader(string line) =>
        (line.EndsWith('：') || line.EndsWith(':')) &&
        !line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
        !line.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string StripComment(string line)
    {
        var cut = line.Length;
        var hash = line.IndexOf('#');
        if (hash >= 0)
        {
            cut = Math.Min(cut, hash);
        }

        var slash = line.IndexOf("//", StringComparison.Ordinal);
        if (slash >= 0 && !line[..slash].EndsWith("http:", StringComparison.OrdinalIgnoreCase) &&
            !line[..slash].EndsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            cut = Math.Min(cut, slash);
        }

        return line[..cut];
    }

    private static QuestProcessDefinition BuildDefinition(
        string author,
        string description,
        IEnumerable<MutableBlock> blocks,
        string sourcePath) =>
        new(
            author,
            description,
            blocks.Select(block => new QuestProcessBlock(
                    block.Description,
                    block.NormalizedDescription,
                    block.IsDefault,
                    block.Instructions.ToArray()))
                .ToArray(),
            sourcePath);

    private sealed class MutableBlock(
        string description,
        string normalizedDescription,
        bool isDefault)
    {
        public string Description { get; } = description;

        public string NormalizedDescription { get; } = normalizedDescription;

        public bool IsDefault { get; } = isDefault;

        public List<QuestProcessInstruction> Instructions { get; } = [];
    }
}
