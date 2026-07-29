using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoQuest.Process;

public sealed class QuestProcessLoader
{
    private readonly QuestProcessParser _parser;

    public QuestProcessLoader(QuestProcessParser? parser = null)
    {
        _parser = parser ?? new QuestProcessParser();
    }

    public async Task<QuestProcessDefinition> LoadAsync(string processFilePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(processFilePath))
        {
            throw new ArgumentException("流程文件路径不能为空", nameof(processFilePath));
        }

        var fullPath = Path.GetFullPath(processFilePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("未找到自动剧情流程文件", fullPath);
        }

        var content = await File.ReadAllTextAsync(fullPath, ct);
        return _parser.Parse(content, fullPath);
    }

    public IReadOnlyList<string> Discover(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(rootDirectory, "process.json", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
