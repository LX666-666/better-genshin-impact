using BetterGenshinImpact.GameTask.AutoQuest.Navigation;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoQuest.Process;

public interface IQuestMarkerFollowerFactory
{
    IQuestMarkerFollower Create(QuestMarkerTemplateProfile profile);
}

public sealed class QuestMarkerFollowerFactory : IQuestMarkerFollowerFactory
{
    private readonly bool _enableGoldenParticleGuidance;

    public QuestMarkerFollowerFactory(bool enableGoldenParticleGuidance = false)
    {
        _enableGoldenParticleGuidance = enableGoldenParticleGuidance;
    }

    public IQuestMarkerFollower Create(QuestMarkerTemplateProfile profile) =>
        new QuestMarkerFollower(
            profile,
            _enableGoldenParticleGuidance
                ? new QuestMarkerFollowerOptions { EnableGoldenParticleGuidance = true }
                : null);
}

public interface IQuestProcessIconTracker
{
    Task<NavigationResult> TrackIconAsync(
        string iconType,
        string mapName,
        string mapMatchMethod,
        CancellationToken ct);

    Task<NavigationResult> TrackCommissionAsync(
        string specification,
        string mapName,
        string mapMatchMethod,
        CancellationToken ct);
}

/// <summary>
/// 将 JS 插件按文件名筛选图标的规则转换为显式 C# 模板分组，
/// 再交给带到达确认、V 重捕获和卡死恢复的 QuestMarkerFollower 执行。
/// </summary>
public sealed class QuestProcessIconTracker : IQuestProcessIconTracker
{
    private readonly IQuestMarkerFollowerFactory _followerFactory;

    public QuestProcessIconTracker(IQuestMarkerFollowerFactory? followerFactory = null)
    {
        _followerFactory = followerFactory ?? new QuestMarkerFollowerFactory();
    }

    public Task<NavigationResult> TrackIconAsync(
        string iconType,
        string mapName,
        string mapMatchMethod,
        CancellationToken ct) =>
        FollowAsync(ResolveIconProfile(iconType), mapName, mapMatchMethod, ct);

    public Task<NavigationResult> TrackCommissionAsync(
        string specification,
        string mapName,
        string mapMatchMethod,
        CancellationToken ct) =>
        FollowAsync(ResolveCommissionProfile(specification), mapName, mapMatchMethod, ct);

    public static QuestMarkerTemplateProfile ResolveIconProfile(string? iconType)
    {
        var normalized = NormalizeIconType(iconType);
        return normalized switch
        {
            "question" or "感叹号" or "start" => QuestMarkerTemplateProfile.Start,
            "task" or "问号" => QuestMarkerTemplateProfile.CommissionTask,
            "into" or "enter" or "进入" => QuestMarkerTemplateProfile.Enter,
            "finish" or "完成" => QuestMarkerTemplateProfile.Finish,
            _ => QuestMarkerTemplateProfile.TrackedTarget
        };
    }

    public static QuestMarkerTemplateProfile ResolveCommissionProfile(string? specification)
    {
        var normalized = NormalizeIconType(specification);
        if (normalized.Contains("question", StringComparison.Ordinal) ||
            normalized.Contains("问号", StringComparison.Ordinal))
        {
            return QuestMarkerTemplateProfile.CommissionQuestion;
        }

        if (normalized.Contains("task", StringComparison.Ordinal) ||
            normalized.Contains("感叹号", StringComparison.Ordinal))
        {
            return QuestMarkerTemplateProfile.CommissionTask;
        }

        return QuestMarkerTemplateProfile.TrackedTarget;
    }

    private async Task<NavigationResult> FollowAsync(
        QuestMarkerTemplateProfile profile,
        string mapName,
        string mapMatchMethod,
        CancellationToken ct)
    {
        var follower = _followerFactory.Create(profile);
        return await follower.FollowAsync(
            new QuestMarkerFollowRequest(mapName, mapMatchMethod),
            ct);
    }

    private static string NormalizeIconType(string? iconType) =>
        string.IsNullOrWhiteSpace(iconType)
            ? "bigmap"
            : iconType.Trim().ToLowerInvariant();
}
