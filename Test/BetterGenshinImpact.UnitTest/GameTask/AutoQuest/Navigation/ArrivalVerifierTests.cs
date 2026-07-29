using BetterGenshinImpact.GameTask.AutoQuest.Navigation;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoQuest.Navigation;

public class ArrivalVerifierTests
{
    [Fact]
    public void Verify_WhenOnlyDistanceIsNear_DoesNotArriveWithoutSecondSignal()
    {
        var verifier = new ArrivalVerifier();
        var result = verifier.Verify(Observation(distance: 2, markerFound: true));

        Assert.False(result.Arrived);
    }

    [Fact]
    public void Verify_WhenDistanceDropsIntoArrivalRange_ReturnsArrived()
    {
        var verifier = new ArrivalVerifier();
        var startedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        verifier.Verify(Observation(startedAt, distance: 20, markerFound: true));
        var result = verifier.Verify(Observation(startedAt.AddSeconds(2), distance: 2, markerFound: true));

        Assert.True(result.Arrived);
        Assert.True(result.DistanceProgressing);
    }

    [Fact]
    public void Verify_WhenMarkerDisappearsFarAway_DoesNotArrive()
    {
        var verifier = new ArrivalVerifier();

        var result = verifier.Verify(Observation(distance: 80, markerFound: false));

        Assert.False(result.Arrived);
    }

    [Fact]
    public void Verify_WhenInteractionAppearsNearTarget_ReturnsArrived()
    {
        var verifier = new ArrivalVerifier();

        var result = verifier.Verify(Observation(distance: 8, markerFound: true, interaction: true));

        Assert.True(result.Arrived);
    }

    [Fact]
    public void Verify_WhenMarkerDisappearsAfterRecentProgress_ReturnsArrived()
    {
        var verifier = new ArrivalVerifier();
        var startedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        verifier.Verify(Observation(startedAt, distance: 30, markerFound: true));
        var result = verifier.Verify(Observation(startedAt.AddSeconds(3), distance: 10, markerFound: false));

        Assert.True(result.Arrived);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Verify_WhenTalkOrQuestChangeAppears_ReturnsArrived(bool talk, bool questChanged)
    {
        var verifier = new ArrivalVerifier();

        var result = verifier.Verify(Observation(
            distance: null,
            markerFound: false,
            talk: talk,
            questChanged: questChanged));

        Assert.True(result.Arrived);
    }

    private static ArrivalObservation Observation(
        int? distance,
        bool markerFound,
        bool interaction = false,
        bool talk = false,
        bool questChanged = false) =>
        Observation(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), distance, markerFound, interaction, talk, questChanged);

    private static ArrivalObservation Observation(
        DateTimeOffset timestamp,
        int? distance,
        bool markerFound,
        bool interaction = false,
        bool talk = false,
        bool questChanged = false) =>
        new(timestamp, distance, markerFound, interaction, talk, questChanged);
}
