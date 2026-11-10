namespace FortniteChestTracker.Tests;

public class MilestoneTests
{
    [Fact]
    public void Last_reached_and_next_milestone()
    {
        Assert.Equal(new Milestones.State(Milestones.Chests, 2_359, 1_000, 2_500), Milestones.Of(Milestones.Chests, 2_359));
        Assert.Equal(new Milestones.State(Milestones.Chests, 99, null, 100), Milestones.Of(Milestones.Chests, 99));
        Assert.Equal(new Milestones.State(Milestones.Chests, 100, 100, 250), Milestones.Of(Milestones.Chests, 100));
        Assert.Equal(new Milestones.State(Milestones.Treasures, 0, null, 1), Milestones.Of(Milestones.Treasures, 0));
        Assert.Null(Milestones.Of(Milestones.Best, 500).Next); // all reached
    }

    [Fact]
    public void Milestones_of_a_history()
    {
        var data = new TrackerData
        {
            Matches =
            {
                new MatchRecord { ChestsOpened = 31, TreasureFinds = { new TreasureFind() } },
                new MatchRecord { ChestsOpened = 12 },
            },
        };
        var states = Milestones.Of(data).ToDictionary(s => s.Key);
        Assert.Equal(43, states[Milestones.Chests].Value);
        Assert.Equal(30, states[Milestones.Best].Reached);
        Assert.Equal(2, states[Milestones.Matches].Value);
        Assert.Null(states[Milestones.Matches].Reached);
        Assert.Equal(1, states[Milestones.Treasures].Reached);
        Assert.DoesNotContain(Milestones.Of(new TrackerData()), s => s.Reached != null);
    }

    [Fact]
    public void Only_new_milestones_are_announced()
    {
        L.Set("en");
        var before = new[] { Milestones.Of(Milestones.Chests, 2_480), Milestones.Of(Milestones.Best, 28), Milestones.Of(Milestones.Treasures, 0) };
        var after = new[] { Milestones.Of(Milestones.Chests, 2_511), Milestones.Of(Milestones.Best, 28), Milestones.Of(Milestones.Treasures, 1) };
        var crossed = Milestones.Crossed(before, after);
        Assert.Equal([Milestones.Chests, Milestones.Treasures], crossed.Select(s => s.Key));
        Assert.Equal("Milestone reached: 2,500 chests opened!", Milestones.Announce(crossed[0]));
        Assert.Equal("Milestone reached: your first treasure!", Milestones.Announce(crossed[1]));
        Assert.Empty(Milestones.Crossed(after, after));
    }
}
