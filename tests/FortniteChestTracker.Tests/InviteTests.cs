namespace FortniteChestTracker.Tests;

public class InviteTests
{
    [Theory]
    [InlineData("fctracker://ami/Bravo_Loot", "Bravo_Loot")]
    [InlineData("fctracker://ami/Bravo_Loot/", "Bravo_Loot")]
    [InlineData("FCTRACKER://ami/Le%20Vrai%20Bravo", "Le Vrai Bravo")]
    [InlineData("fctracker:ami/Alpha.99", "Alpha.99")]
    public void A_link_gives_the_friend_name(string arg, string name) =>
        Assert.Equal(name, Invite.NameFromArgs(new[] { "--data", "x", arg }));

    [Theory]
    [InlineData("fctracker://ami/ab")]                 // too short for an Epic name
    [InlineData("fctracker://ami/%3Cscript%3E")]       // not a name
    [InlineData("fctracker://other/Bravo")]
    [InlineData("https://ami/Bravo")]
    [InlineData("fctracker://ami/a%2Fb%2Fc")]
    public void Anything_else_is_ignored(string arg) => Assert.Null(Invite.NameFromArgs(new[] { arg }));

    [Fact]
    public void A_link_brings_no_other_option()
    {
        // A crafted link must not add --data, --uninstall… to the command line
        Assert.Equal(new[] { "fctracker://ami/Bravo" }, Invite.Sanitize(new[] { "--invite", "fctracker://ami/Bravo", "--uninstall", "--quiet", "--data", @"\\evil\share" }));
        Assert.Empty(Invite.Sanitize(new[] { "--invite", "--uninstall", "--confirmed" }));
        Assert.Equal(new[] { "--data", "x" }, Invite.Sanitize(new[] { "--data", "x" }));
    }

    [Theory]
    [InlineData("Bravo‮evil")]
    [InlineData("a\nb\nc")]
    [InlineData("<b>name</b>")]
    public void Odd_names_are_refused(string name) => Assert.Null(Invite.CleanName(name));

    [Fact]
    public void The_link_is_on_the_website() =>
        Assert.Equal("https://fortnite-chest-tracker.com/ami/Le%20Vrai%20Bravo", Invite.LinkFor("Le Vrai Bravo"));
}
