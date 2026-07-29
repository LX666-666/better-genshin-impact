using BetterGenshinImpact.GameTask.AutoQuest.Navigation;
using BetterGenshinImpact.GameTask.AutoQuest.Process;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoQuest.Process;

public class QuestProcessIconTrackerTests
{
    [Theory]
    [InlineData("Bigmap", QuestMarkerTemplateProfile.TrackedTarget)]
    [InlineData("Question", QuestMarkerTemplateProfile.Start)]
    [InlineData("Task", QuestMarkerTemplateProfile.CommissionTask)]
    [InlineData("Into", QuestMarkerTemplateProfile.Enter)]
    [InlineData("Finish", QuestMarkerTemplateProfile.Finish)]
    public void ResolveIconProfile_UsesPluginIconNaming(
        string iconType,
        QuestMarkerTemplateProfile expected)
    {
        Assert.Equal(expected, QuestProcessIconTracker.ResolveIconProfile(iconType));
    }

    [Theory]
    [InlineData("NPC Question", QuestMarkerTemplateProfile.CommissionQuestion)]
    [InlineData("NPC Task", QuestMarkerTemplateProfile.CommissionTask)]
    [InlineData("NPC", QuestMarkerTemplateProfile.TrackedTarget)]
    public void ResolveCommissionProfile_UsesCommissionTemplates(
        string specification,
        QuestMarkerTemplateProfile expected)
    {
        Assert.Equal(expected, QuestProcessIconTracker.ResolveCommissionProfile(specification));
    }

    [Fact]
    public async Task TrackIconAsync_CreatesFollowerForResolvedProfile()
    {
        var factory = new RecordingFollowerFactory();
        var tracker = new QuestProcessIconTracker(factory);

        var result = await tracker.TrackIconAsync(
            "Into",
            "Teyvat",
            "TemplateMatch",
            CancellationToken.None);

        Assert.Equal(NavigationStatus.Arrived, result.Status);
        Assert.Equal(QuestMarkerTemplateProfile.Enter, factory.Profile);
    }

    private sealed class RecordingFollowerFactory : IQuestMarkerFollowerFactory
    {
        public QuestMarkerTemplateProfile Profile { get; private set; }

        public IQuestMarkerFollower Create(QuestMarkerTemplateProfile profile)
        {
            Profile = profile;
            return new ArrivedFollower();
        }
    }

    private sealed class ArrivedFollower : IQuestMarkerFollower
    {
        public Task<NavigationResult> FollowAsync(QuestMarkerFollowRequest request, CancellationToken ct) =>
            Task.FromResult(NavigationResult.Arrived());
    }
}
