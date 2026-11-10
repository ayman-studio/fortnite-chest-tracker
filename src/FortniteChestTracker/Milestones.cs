namespace FortniteChestTracker;

/// <summary>
/// Round numbers to reach: chests opened in total, chests in the best match, matches played and treasures found. The page
/// shows the last one reached and the next one; crossing one while the counter runs is announced like a counted match.
/// </summary>
public static class Milestones
{
    public const string Chests = "chests";
    public const string Best = "best";
    public const string Matches = "matches";
    public const string Treasures = "treasures";

    public static IReadOnlyDictionary<string, int[]> Steps { get; } = new Dictionary<string, int[]>
    {
        [Chests] = [100, 250, 500, 1_000, 2_500, 5_000, 10_000, 25_000, 50_000, 100_000, 250_000, 500_000, 1_000_000],
        [Best] = [10, 15, 20, 25, 30, 35, 40, 45, 50, 60, 75, 100],
        [Matches] = [10, 25, 50, 100, 250, 500, 1_000, 2_500, 5_000, 10_000],
        [Treasures] = [1, 5, 10, 25, 50, 100, 250, 500, 1_000],
    };

    /// <param name="Reached">Last milestone reached (null = not even the first one).</param>
    /// <param name="Next">Next milestone (null = all reached).</param>
    public sealed record State(string Key, int Value, int? Reached, int? Next);

    public static State Of(string key, int value)
    {
        var steps = Steps[key];
        var reached = steps.Where(s => s <= value).Select(s => (int?)s).LastOrDefault();
        var next = steps.Where(s => s > value).Select(s => (int?)s).FirstOrDefault();
        return new State(key, value, reached, next);
    }

    /// <summary>The four milestones of a history (the profile shown).</summary>
    public static IReadOnlyList<State> Of(TrackerData view) =>
    [
        Of(Chests, view.Matches.Sum(m => m.ChestsOpened)),
        Of(Best, view.Matches.Count > 0 ? view.Matches.Max(m => m.ChestsOpened) : 0),
        Of(Matches, view.Matches.Count),
        Of(Treasures, view.Matches.Sum(m => m.TreasureFinds.Count)),
    ];

    /// <summary>Milestones reached in <paramref name="after"/> that were not in <paramref name="before"/>.</summary>
    public static IReadOnlyList<State> Crossed(IReadOnlyList<State> before, IReadOnlyList<State> after) => after
        .Where(a => a.Reached is int now && (before.FirstOrDefault(b => b.Key == a.Key)?.Reached ?? 0) < now)
        .ToList();

    /// <summary>What the player is told when a milestone is crossed.</summary>
    public static string Announce(State s)
    {
        var n = L.N(s.Reached ?? s.Value);
        return s.Key switch
        {
            Chests => L.T($"Palier atteint : {n} coffres ouverts !", $"Milestone reached: {n} chests opened!", $"¡Hito alcanzado: {n} cofres abiertos!"),
            Best => L.T($"Nouveau palier : {n} coffres en une seule partie !", $"New milestone: {n} chests in a single match!", $"¡Nuevo hito: {n} cofres en una sola partida!"),
            Matches => L.T($"Palier atteint : {n} parties comptées !", $"Milestone reached: {n} matches counted!", $"¡Hito alcanzado: {n} partidas contadas!"),
            _ => s.Reached == 1
                ? L.T("Palier atteint : ton premier trésor !", "Milestone reached: your first treasure!", "¡Hito alcanzado: tu primer tesoro!")
                : L.T($"Palier atteint : {n} trésors trouvés !", $"Milestone reached: {n} treasures found!", $"¡Hito alcanzado: {n} tesoros encontrados!"),
        };
    }
}
