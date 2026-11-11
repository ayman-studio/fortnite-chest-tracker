using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FortniteChestTracker;

/// <summary>A drawn overlay: premultiplied RGBA pixels (0 … 1) and the spots the window needs.</summary>
internal sealed class HudFrame
{
    public HudFrame(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new float[width * height * 4];
    }

    public int Width { get; }
    public int Height { get; }
    public float[] Pixels { get; }

    /// <summary>Lock button (edit mode only), in pixels.</summary>
    public (int X, int Y, int W, int H) LockButton { get; set; }

    /// <summary>Where the hero number is drawn (for the "+1" animation).</summary>
    public (int X, int Y, int W, int H) Number { get; set; }

    /// <summary>Clears the pixels for a new drawing of the same size.</summary>
    public void Clear()
    {
        Array.Clear(Pixels);
        LockButton = default;
        Number = default;
    }

    /// <summary>Premultiplied BGRA bytes, as UpdateLayeredWindow wants them (into <paramref name="o"/> when big enough).</summary>
    public byte[] ToBgraPremultiplied(byte[]? o = null)
    {
        if (o == null || o.Length < Width * Height * 4)
        {
            o = new byte[Width * Height * 4];
        }
        for (int i = 0, n = Width * Height; i < n; i++)
        {
            var p = i * 4;
            o[p] = B(Pixels[p + 2]);
            o[p + 1] = B(Pixels[p + 1]);
            o[p + 2] = B(Pixels[p]);
            o[p + 3] = B(Pixels[p + 3]);
        }
        return o;
    }

    /// <summary>PNG with transparency (straight alpha).</summary>
    public byte[] ToPng()
    {
        var o = new byte[Width * Height * 4];
        for (int i = 0, n = Width * Height; i < n; i++)
        {
            var p = i * 4;
            var a = Pixels[p + 3];
            if (a <= 0.002f)
            {
                continue;
            }
            o[p] = B(Pixels[p + 2] / a);
            o[p + 1] = B(Pixels[p + 1] / a);
            o[p + 2] = B(Pixels[p] / a);
            o[p + 3] = B(a);
        }
        return Png.Encode(Width, Height, o, alpha: true);
    }

    private static byte B(float v) => (byte)(v <= 0 ? 0 : v >= 1 ? 255 : v * 255 + 0.5f);
}

/// <summary>
/// Draws the overlay as a floating game HUD: no window frame, only the elements with their outline, shadow and a soft
/// dark halo behind them, so it reads over any part of the game. Plain C# pixels + GDI for the glyphs (Anton is
/// embedded), nothing to install. One instance per thread.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HudRenderer : IDisposable
{
    // Colours of the page (night blue, lime, aqua, coral).
    private static readonly Col Navy = Col.Hex(0x071036), Deep = Col.Hex(0x0b1a4a), Panel = Col.Hex(0x12266c), Lime = Col.Hex(0xc6ff3d),
        LimeDark = Col.Hex(0x7fc21a), Aqua = Col.Hex(0x3ee6ff), Coral = Col.Hex(0xff5f7e), Amber = Col.Hex(0xffbf3a), White = Col.Hex(0xffffff),
        Ice = Col.Hex(0xd8e6ff), Muted = Col.Hex(0xbcd0f5), Lavender = Col.Hex(0xcdb6ff);

    private static readonly Lazy<bool> Anton = new(LoadAnton);
    private static readonly Lazy<Img?> ChestImg = new(() => Img.Load("FortniteChestTracker.hud.chest.png"));
    private static readonly Lazy<Img?> TokenImg = new(() => Img.Load("FortniteChestTracker.hud.token.png"));

    private readonly Dictionary<string, HudFont> _fonts = new();
    private HudFrame? _reuse;

    /// <summary>A frame of this size: the previous one cleared when the size is the same (no big allocation per frame).</summary>
    private HudFrame NewFrame(int w, int h)
    {
        if (_reuse is { } f && f.Width == w && f.Height == h)
        {
            f.Clear();
            return f;
        }
        return _reuse = new HudFrame(w, h);
    }
    private readonly Dictionary<string, Mask> _cache = new();

    /// <summary>Renders the overlay.</summary>
    /// <param name="scale">Display scaling × size chosen by the player (1 = 100 % at 96 dpi).</param>
    /// <param name="edit">Unlocked: dashed frame, grab area and lock button.</param>
    /// <param name="minWidth">The overlay never gets narrower than this (pixels), so it does not jump between matches.</param>
    /// <param name="pop">Animation after a chest (1 → 0), 0 = none; <paramref name="popCount"/> = chests added.</param>
    public HudFrame Render(HudView v, float scale, bool edit = false, int minWidth = 0, float pop = 0, int popCount = 0)
    {
        if (_cache.Count > 160)
        {
            _cache.Clear();
        }
        if (v.Compact)
        {
            return RenderCompact(v, scale, edit, minWidth, pop, popCount);
        }
        int S(float n) => (int)MathF.Round(n * scale);
        var anton = Anton.Value ? "Anton" : "Impact";

        var fBig = Font(anton, S(62), 400);
        var fWord = Font(anton, S(25), 400);
        var fTag = Font(anton, S(13), 400);
        var fGoal = Font(anton, S(17), 400);
        var fPct = Font(anton, S(24), 400);
        var fStat = Font(anton, S(21), 400);
        var fSmall = Font("Segoe UI", S(11), 700);
        var fSite = Font("Segoe UI", S(11), 600);
        var fIcon = Font("Segoe MDL2 Assets", S(13), 400);

        var margin = S(22);
        var icon = S(74);

        // ---- Measure
        var number = v.Chests is { } c ? L.N(c) : v.Mode == HudMode.InProgress ? "?" : "—";
        var word = L.T("COFFRES", "CHESTS", "COFRES");
        var tag = v.Mode switch
        {
            HudMode.Live => L.T("EN DIRECT", "LIVE", "EN DIRECTO"),
            HudMode.InProgress => L.T("PARTIE EN COURS", "MATCH IN PROGRESS", "PARTIDA EN CURSO"),
            _ => L.T("DERNIÈRE PARTIE", "LAST MATCH", "ÚLTIMA PARTIDA"),
        };
        var numW = Advance(fBig, number);
        var tagW = TagWidth(fTag, v.Mode, tag, S);
        var wordW = Advance(fWord, word);
        var heroW = icon + S(10) + numW + S(14) + Math.Max(tagW, wordW);

        var stats = new (string Value, string Label, Col Color)[]
        {
            (v.Total, L.T("TOTAL", "TOTAL", "TOTAL"), White),
            (L.N(v.Session), L.T("SESSION", "SESSION", "SESIÓN"), White),
            ("★ " + L.N(v.Treasures), L.T("TRÉSORS", "TREASURES", "TESOROS"), Lavender),
        };
        // The last counted match stays readable while another one is played, with the time of the last update
        var lastRow = v.LastChests is { } lastChests ? new (string Value, string Label, Col Color)[]
        {
            ($"{L.N(lastChests)} {word}", L.T("DERNIÈRE PARTIE", "LAST MATCH", "ÚLTIMA PARTIDA"), White),
            (v.LastDuration ?? "—", L.T("DURÉE", "DURATION", "DURACIÓN"), White),
            (v.UpdatedAt ?? "—", L.T("MISE À JOUR", "UPDATED", "ACTUALIZADO"), White),
        } : null;
        int RowWidth((string Value, string Label, Col Color)[] row) => row.Sum(s => Math.Max(Advance(fStat, s.Value), Advance(fSmall, s.Label))) + 2 * S(34);
        var statsW = Math.Max(v.ShowStats ? RowWidth(stats) : 0, lastRow != null ? RowWidth(lastRow) : 0);
        if (lastRow != null) // equal columns: the first one ("1 234 COFFRES") must fit before its separator
        {
            statsW = Math.Max(statsW, 3 * (Math.Max(Advance(fStat, lastRow[0].Value), Advance(fSmall, lastRow[0].Label)) + S(10)));
        }

        var contentW = Math.Max(Math.Max(heroW, statsW), S(300));
        var w = Math.Max(minWidth, contentW + 2 * margin);
        contentW = w - 2 * margin;
        var x0 = margin;
        var right = w - margin;

        var heroH = icon;
        var fEvent = Font(anton, S(14), 400);
        var eventH = v.Event != null ? S(12) + fEvent.Cap + S(10) : 0;
        var goalH = v.Goal != null ? S(16) + S(26) + S(13) + S(8) + fSmall.Cap : 0;
        var statsH = S(16) + fStat.Cap + S(7) + fSmall.Cap;
        var site = string.IsNullOrWhiteSpace(v.Site) ? null : v.Site.Trim();
        var siteH = site != null ? S(14) + fSite.Cap : 0;
        if (v.Event != null)
        {
            contentW = Math.Max(contentW, Advance(fEvent, v.Event) + S(40));
            w = Math.Max(minWidth, contentW + 2 * margin);
            contentW = w - 2 * margin;
            right = w - margin;
        }
        var h = margin + heroH + eventH + goalH + (v.ShowStats ? statsH : 0) + (lastRow != null ? statsH : 0) + siteH + margin + S(4);

        var f = NewFrame(w, h);
        var cv = new Canvas(f);

        Backdrop(cv, w, h, margin, edit, S);

        // ---- Hero: chest, big number, tag + "COFFRES"
        var y = margin;
        var bounce = pop > 0 ? MathF.Sin(pop * MathF.PI) * S(7) : 0;
        if (ChestImg.Value is { } chest)
        {
            var glow = Cached($"chestglow{icon}", () => Gfx.Blur(Gfx.RoundRect(icon * 0.8f, icon * 0.8f, icon * 0.4f), S(12)));
            cv.Fill(glow, x0 + (icon - glow.W) / 2, y + (icon - glow.H) / 2 + S(4), v.Mode == HudMode.Live ? Lime : Aqua, 0.28f + 0.5f * pop);
            cv.Draw(chest, x0 - S(4), y - S(4) - bounce, icon + S(8), icon + S(8));
        }
        var nx = x0 + icon + S(10);
        var numTop = y + (heroH - fBig.Cap) / 2 + S(2);
        var numTopCol = v.Mode == HudMode.Live ? Col.Hex(0xe9ffad) : White;
        var numBottomCol = v.Mode == HudMode.Live ? Lime : v.Chests == null ? Muted : Ice;
        if (pop > 0)
        {
            var ink = Ink(fBig, number);
            var g = Cached($"popglow{number}{fBig.Key}", () => Gfx.Blur(Gfx.Dilate(ink.M, S(4)), S(10)));
            var pad = (g.W - ink.M.W) / 2;
            cv.Fill(g, nx - ink.Pad - pad, numTop - ink.Pad - fBig.CapTop - pad, Lime, 0.9f * pop);
        }
        Text(cv, fBig, number, nx, numTop, numTopCol, numBottomCol, S(4), S(3));
        f.Number = (nx, numTop, numW, fBig.Cap);

        var tx = nx + numW + S(14);
        Tag(cv, fTag, v.Mode, tag, tx, numTop, tagW, S);
        Text(cv, fWord, word, tx, numTop + fBig.Cap - fWord.Cap, White, Ice, S(3), S(2));

        // "+1" floating up after a chest
        if (pop > 0 && popCount > 0)
        {
            var plus = "+" + L.N(popCount);
            var rise = (1 - pop) * S(26);
            Text(cv, fPct, plus, nx + numW - S(4), (int)(numTop - S(14) - rise), Col.Hex(0xe9ffad), Lime, S(3), S(2), Math.Min(1, pop * 1.6f));
        }
        y += heroH;

        // ---- Treasure Hour: a pill under the hero (lime while it is on)
        if (v.Event is { } ev)
        {
            y += S(12);
            var pillH = fEvent.Cap + S(10);
            var pillW = Advance(fEvent, ev) + S(30);
            var pill = Cached($"evpill{pillW}x{pillH}", () => Gfx.RoundRect(pillW, pillH, pillH / 2f));
            cv.Fill(pill, x0, y - S(5), v.EventOn ? Lime : Col.Hex(0x0b1a4a), v.EventOn ? 0.95f : 0.85f);
            var bolt = v.EventOn ? Col.Hex(0x0b1a4a) : Lime;
            var mark = Cached($"evbolt{fEvent.Cap}", () => Gfx.Poly(S(9), fEvent.Cap, new (float, float)[] { (S(6), 0f), (0f, fEvent.Cap * 0.58f), (S(4), fEvent.Cap * 0.58f), (S(3), (float)fEvent.Cap), (S(9), fEvent.Cap * 0.38f), (S(5), fEvent.Cap * 0.38f) }));
            cv.Fill(mark, x0 + S(10), y, bolt, 1f);
            if (v.EventOn)
            {
                Text(cv, fEvent, ev, x0 + S(23), y, Col.Hex(0x0b1a4a), Col.Hex(0x0b1a4a), 0, 0);
            }
            else
            {
                Text(cv, fEvent, ev, x0 + S(23), y, White, Ice, S(2), 0);
            }
            y += fEvent.Cap + S(5);
        }

        // ---- Goal: token, name, chance, segmented "dig" bar, chests / odds
        if (v.Goal is { } goal)
        {
            y += S(16);
            var gi = S(28);
            var gx = x0;
            if (goal.Key == "odds_pirate" && TokenImg.Value is { } token)
            {
                cv.Draw(token, gx - S(3), y + (fGoal.Cap - gi) / 2 - S(3), gi + S(6), gi + S(6));
                gx += gi + S(8);
            }
            else
            {
                var star = Cached($"star{gi}", () => Gfx.Star(gi, gi));
                Outline(cv, star, gx, y + (fGoal.Cap - gi) / 2, S(2));
                cv.FillV(star, gx, y + (fGoal.Cap - gi) / 2, Col.Hex(0xfff2a8), Amber);
                gx += gi + S(8);
            }
            Text(cv, fGoal, goal.Name.ToUpper(L.Culture), gx, y, White, Ice, S(3), S(2));
            var pct = HudModel.Percent(goal.Chance);
            Text(cv, fPct, pct, right - Advance(fPct, pct), y + fGoal.Cap - fPct.Cap, Col.Hex(0xe9ffad), Lime, S(3), S(2));
            y += fGoal.Cap + S(13);

            Bar(cv, x0, y, contentW, S(13), goal.Progress, S);
            y += S(13) + S(8);
            var caption = $"{L.N(goal.Chests)} / {L.N(goal.Odds)} {L.T("coffres", "chests", "cofres")}";
            Text(cv, fSmall, caption, x0, y, Muted, Muted, S(2), 0);
            var odds = L.T($"1 sur {L.N(goal.Odds)}", $"1 in {L.N(goal.Odds)}", $"1 de {L.N(goal.Odds)}");
            Text(cv, fSmall, odds, right - Advance(fSmall, odds), y, Muted, Muted, S(2), 0, 0.8f);
            y += fSmall.Cap;
        }

        // ---- Stats: total · session · treasures, then the last match; three columns split by slanted lime strokes
        var colW = contentW / 3f;
        void StatRow((string Value, string Label, Col Color)[] row)
        {
            y += S(16);
            for (var i = 0; i < row.Length; i++)
            {
                var (value, label, color) = row[i];
                var cx = x0 + colW * i;
                var align = i == 0 ? 0 : i == 1 ? 1 : 2;
                int Left(int width) => align switch { 0 => (int)cx, 1 => (int)(cx + (colW - width) / 2), _ => (int)(cx + colW - width) };
                Text(cv, fStat, value, Left(Advance(fStat, value)), y, color, color == White ? Ice : color, S(3), S(2));
                Text(cv, fSmall, label, Left(Advance(fSmall, label)), y + fStat.Cap + S(7), Muted, Muted, S(2), 0);
                if (i > 0)
                {
                    var hgt = fStat.Cap + S(7) + fSmall.Cap;
                    var sx = (int)cx;
                    var stroke = Cached($"stroke{hgt}", () => Gfx.Poly(S(6), hgt, new (float, float)[] { (S(4), 0f), (S(6), 0f), (S(2), (float)hgt), (0f, (float)hgt) }));
                    cv.Fill(stroke, sx - S(3), y, Lime, 0.55f);
                }
            }
            y += fStat.Cap + S(7) + fSmall.Cap;
        }
        if (v.ShowStats)
        {
            StatRow(stats);
        }
        if (lastRow != null)
        {
            StatRow(lastRow);
        }

        // ---- Website
        if (site != null)
        {
            y += S(14);
            Text(cv, fSite, site, (w - Advance(fSite, site)) / 2, y, Muted, Muted, S(2), 0, 0.85f);
        }

        if (edit)
        {
            LockButton(cv, f, fIcon, S);
        }
        return f;
    }

    /// <summary>
    /// Compact layout: one line (chest, big number, tag, goal chance) and a thin dig bar under it. About half the size
    /// of the full HUD, for players who want the least on screen.
    /// </summary>
    private HudFrame RenderCompact(HudView v, float scale, bool edit, int minWidth, float pop, int popCount)
    {
        int S(float n) => (int)MathF.Round(n * scale);
        var anton = Anton.Value ? "Anton" : "Impact";
        var fBig = Font(anton, S(46), 400);
        var fTag = Font(anton, S(11), 400);
        var fPct = Font(anton, S(18), 400);
        var fPlus = Font(anton, S(19), 400);
        var fSite = Font("Segoe UI", S(10), 600);
        var fIcon = Font("Segoe MDL2 Assets", S(13), 400);

        var margin = S(16);
        var icon = S(52);
        var number = v.Chests is { } c ? L.N(c) : v.Mode == HudMode.InProgress ? "?" : "—";
        var tag = v.Mode switch
        {
            HudMode.Live => L.T("EN DIRECT", "LIVE", "EN DIRECTO"),
            HudMode.InProgress => L.T("EN COURS", "IN PROGRESS", "EN CURSO"),
            _ => L.T("DERNIÈRE", "LAST", "ÚLTIMA"),
        };
        var numW = Advance(fBig, number);
        var tagW = TagWidth(fTag, v.Mode, tag, S);
        var gi = S(20);
        var pct = v.Goal != null ? HudModel.Percent(v.Goal.Chance) : null;
        var goalW = pct != null ? gi + S(6) + Advance(fPct, pct) : 0;
        var site = string.IsNullOrWhiteSpace(v.Site) ? null : v.Site.Trim();

        // Small line: the last counted match (still readable during the next one) and the time of the last update
        var fInfo = Font("Segoe UI", S(10), 700);
        var lastLine = v.LastChests is { } lastChests
            ? L.T($"Dernière : {L.N(lastChests)} coffres", $"Last: {L.N(lastChests)} chests", $"Última: {L.N(lastChests)} cofres") + (v.LastDuration != null ? " · " + v.LastDuration : "")
            : null;
        var updated = v.UpdatedAt != null ? L.T("MàJ ", "Updated ", "Act. ") + v.UpdatedAt : null;
        var fEvent = Font(anton, S(12), 400);

        // + room on the right for the padlock of the edit mode (the width stays the same locked or not)
        var contentW = Math.Max(icon + S(8) + numW + S(12) + Math.Max(tagW + S(6), goalW) + S(20), S(210));
        if (site != null)
        {
            contentW = Math.Max(contentW, Advance(fSite, site));
        }
        if (lastLine != null)
        {
            contentW = Math.Max(contentW, Advance(fInfo, lastLine) + (updated != null ? S(14) + Advance(fInfo, updated) : 0));
        }
        if (v.Event != null)
        {
            contentW = Math.Max(contentW, Advance(fEvent, v.Event) + S(12));
        }
        var w = Math.Max(minWidth, contentW + 2 * margin);
        contentW = w - 2 * margin;
        var barH = S(7);
        var h = margin + icon + (v.Goal != null ? S(9) + barH : 0) + (v.Event != null ? S(9) + fEvent.Cap : 0) + (lastLine != null ? S(9) + fInfo.Cap : 0) + (site != null ? S(9) + fSite.Cap : 0) + margin + S(2);

        var f = NewFrame(w, h);
        var cv = new Canvas(f);
        Backdrop(cv, w, h, margin, edit, S);

        var x0 = margin;
        var y = margin;
        var bounce = pop > 0 ? MathF.Sin(pop * MathF.PI) * S(5) : 0;
        if (ChestImg.Value is { } chest)
        {
            var glow = Cached($"chestglow{icon}", () => Gfx.Blur(Gfx.RoundRect(icon * 0.8f, icon * 0.8f, icon * 0.4f), S(10)));
            cv.Fill(glow, x0 + (icon - glow.W) / 2, y + (icon - glow.H) / 2 + S(3), v.Mode == HudMode.Live ? Lime : Aqua, 0.28f + 0.5f * pop);
            cv.Draw(chest, x0 - S(3), y - S(3) - bounce, icon + S(6), icon + S(6));
        }
        var nx = x0 + icon + S(8);
        var numTop = y + (icon - fBig.Cap) / 2 + S(1);
        if (pop > 0)
        {
            var ink = Ink(fBig, number);
            var g = Cached($"popglow{number}{fBig.Key}", () => Gfx.Blur(Gfx.Dilate(ink.M, S(3)), S(8)));
            var pad = (g.W - ink.M.W) / 2;
            cv.Fill(g, nx - ink.Pad - pad, numTop - ink.Pad - fBig.CapTop - pad, Lime, 0.9f * pop);
        }
        Text(cv, fBig, number, nx, numTop, v.Mode == HudMode.Live ? Col.Hex(0xe9ffad) : White,
            v.Mode == HudMode.Live ? Lime : v.Chests == null ? Muted : Ice, S(3), S(3));
        f.Number = (nx, numTop, numW, fBig.Cap);

        // Right of the number: the tag on top, the goal chance under it (bottom aligned with the number)
        var tx = nx + numW + S(12);
        Tag(cv, fTag, v.Mode, tag, tx, numTop - S(1), tagW, S);
        if (v.Goal is { } goal)
        {
            var gy = numTop + fBig.Cap - fPct.Cap;
            var gx = tx;
            if (goal.Key == "odds_pirate" && TokenImg.Value is { } token)
            {
                cv.Draw(token, gx - S(2), gy + (fPct.Cap - gi) / 2 - S(2), gi + S(4), gi + S(4));
            }
            else
            {
                var star = Cached($"star{gi}", () => Gfx.Star(gi, gi));
                Outline(cv, star, gx, gy + (fPct.Cap - gi) / 2, S(2));
                cv.FillV(star, gx, gy + (fPct.Cap - gi) / 2, Col.Hex(0xfff2a8), Amber);
            }
            Text(cv, fPct, pct!, gx + gi + S(6), gy, Col.Hex(0xe9ffad), Lime, S(3), S(2));
        }
        if (pop > 0 && popCount > 0)
        {
            var rise = (1 - pop) * S(20);
            Text(cv, fPlus, "+" + L.N(popCount), nx + numW - S(6), (int)(numTop - S(10) - rise), Col.Hex(0xe9ffad), Lime, S(3), S(2), Math.Min(1, pop * 1.6f));
        }
        y += icon;

        if (v.Goal is { } g2)
        {
            y += S(9);
            Bar(cv, x0, y, contentW, barH, g2.Progress, S, segments: 16);
            y += barH;
        }
        if (v.Event is { } ev)
        {
            y += S(9);
            Text(cv, fEvent, ev, x0, y, v.EventOn ? Col.Hex(0xe9ffad) : White, v.EventOn ? Lime : Ice, S(3), S(2));
            y += fEvent.Cap;
        }
        if (lastLine != null)
        {
            y += S(9);
            Text(cv, fInfo, lastLine, x0, y, White, Ice, S(2), 0);
            if (updated != null)
            {
                Text(cv, fInfo, updated, x0 + contentW - Advance(fInfo, updated), y, Muted, Muted, S(2), 0);
            }
            y += fInfo.Cap;
        }
        if (site != null)
        {
            y += S(9);
            Text(cv, fSite, site, (w - Advance(fSite, site)) / 2, y, Muted, Muted, S(2), 0, 0.85f);
        }
        if (edit)
        {
            LockButton(cv, f, fIcon, S, size: 22);
        }
        return f;
    }

    /// <summary>Lock button (edit mode), top right.</summary>
    private void LockButton(Canvas cv, HudFrame f, HudFont fIcon, Func<float, int> S, int size = 28)
    {
        var w = f.Width;
        {
            var bs = S(size);
            var bx = w - bs - S(size / 3.5f);
            var by = S(size / 3.5f);
            var b = Cached($"btn{bs}", () => Gfx.RoundRect(bs, bs, bs / 2f));
            cv.Fill(Cached($"btnsh{bs}", () => Gfx.Blur(Gfx.Dilate(b, S(2)), S(3))), bx - S(2) - S(3) * 2, by - S(2) - S(3) * 2 + S(1), Navy, 0.7f);
            cv.Fill(b, bx, by, Lime);
            var glyph = ""; // open padlock
            var gw = Advance(fIcon, glyph);
            var ink = Ink(fIcon, glyph);
            cv.Fill(ink.M, bx + (bs - gw) / 2 - ink.Pad, by + (bs - ink.Cell) / 2 - ink.Pad, Navy);
            f.LockButton = (bx, by, bs, bs);
        }
    }

    /// <summary>Soft dark halo behind everything (no hard edge, so it does not look like a window), strong behind the
    /// chest and the numbers and fading out to the right like a game quest tracker; in edit mode a grab area and a
    /// dashed lime frame.</summary>
    private void Backdrop(Canvas cv, int w, int h, int margin, bool edit, Func<float, int> S)
    {
        var bw = w - 2 * margin + S(16);
        var bh = h - 2 * margin + S(14);
        var halo = Cached($"halo{w}x{h}", () => Gfx.FadeRight(Gfx.Blur(Gfx.RoundRect(bw, bh, Math.Min(S(30), bh / 2)), S(16)), 0.45f, 0.18f));
        cv.Fill(halo, margin - S(8) - (halo.W - bw) / 2, margin - S(7) - (halo.H - bh) / 2, Navy, 0.66f);
        if (edit)
        {
            var area = Cached($"area{w}x{h}", () => Gfx.RoundRect(w - S(4), h - S(4), S(18)));
            cv.Fill(area, S(2), S(2), Panel, 0.42f);
            DashedFrame(cv, S(2), S(2), w - S(4), h - S(4), S(18), Math.Max(1, S(2)), S(10), S(7), Lime, 0.9f);
        }
    }

    // Tag proportions follow the size of its letters (same look in both layouts).
    private static int TagPad(HudFont f) => (int)MathF.Round(f.Cap * 0.9f);
    private static int TagDot(HudFont f, HudMode mode) => mode == HudMode.Live ? (int)MathF.Round(f.Cap * 1.2f) : 0;
    private static int TagHeight(HudFont f) => f.Cap + 2 * (int)MathF.Round(f.Cap * 0.6f);

    private int TagWidth(HudFont fTag, HudMode mode, string tag, Func<float, int> S) =>
        Advance(fTag, tag) + 2 * TagPad(fTag) + TagDot(fTag, mode) + S(6);

    /// <summary>Slanted tag: coral "● LIVE", amber "MATCH IN PROGRESS", blue "LAST MATCH".</summary>
    private void Tag(Canvas cv, HudFont fTag, HudMode mode, string tag, int tx, int tagY, int tagW, Func<float, int> S)
    {
        var tagPad = TagPad(fTag);
        var dot = TagDot(fTag, mode);
        var tagH = TagHeight(fTag);
        var (tagBg, tagFg, tagAlpha) = mode switch
        {
            HudMode.Live => (Coral, White, 1f),
            HudMode.InProgress => (Amber, Navy, 1f),
            _ => (Col.Hex(0x2a4aa8), White, 0.95f),
        };
        var slant = S(5);
        var tagShape = Cached($"tag{tagW}x{tagH}", () => Gfx.Poly(tagW + slant, tagH, new (float, float)[] { (slant, 0f), (tagW + slant, 0f), (tagW, tagH), (0f, tagH) }));
        cv.Fill(Cached($"tagsh{tagW}x{tagH}", () => Gfx.Blur(tagShape, S(3))), tx - S(3) * 2 + S(1), tagY - S(3) * 2 + S(2), Navy, 0.6f);
        cv.Fill(tagShape, tx, tagY, tagBg, tagAlpha);
        var tagTextX = tx + tagPad + slant / 2;
        if (dot > 0)
        {
            var ds = Math.Max(3, (int)MathF.Round(fTag.Cap * 0.7f));
            var d = Cached($"dot{ds}", () => Gfx.RoundRect(ds, ds, ds / 2f));
            cv.Fill(d, tagTextX, tagY + (tagH - d.H) / 2, White);
            tagTextX += dot;
        }
        Text(cv, fTag, tag, tagTextX, tagY + (tagH - fTag.Cap) / 2, tagFg, tagFg, 0, 0);
    }

    // ---- Pieces --------------------------------------------------------------------------------------------------

    /// <summary>Segmented, slanted progress bar (like a shield bar): lime segments, the current one partly filled.</summary>
    private void Bar(Canvas cv, int x, int y, int width, int height, double progress, Func<float, int> S, int segments = 12)
    {
        var gap = Math.Max(2, S(height >= S(10) ? 4 : 3));
        var slant = Math.Max(2, height * 5 / 13);
        var segW = (width - slant - gap * (segments - 1)) / (float)segments;
        var seg = Cached($"seg{segW:0.0}x{height}", () => Gfx.Poly((int)MathF.Ceiling(segW + slant), height,
            new (float, float)[] { (slant, 0f), (segW + slant, 0f), (segW, (float)height), (0f, (float)height) }));
        var shadow = Cached($"segsh{segW:0.0}x{height}", () => Gfx.Blur(Gfx.Dilate(seg, S(2)), S(2)));
        var filled = progress * segments;
        if (progress > 0 && filled < 0.06)
        {
            filled = 0.06; // a sliver, so the first chests show
        }
        for (var i = 0; i < segments; i++)
        {
            var sx = x + (int)MathF.Round(i * (segW + gap));
            cv.Fill(shadow, sx - (shadow.W - seg.W) / 2, y - (shadow.H - seg.H) / 2 + S(1), Navy, 0.75f);
            cv.Fill(seg, sx, y, Deep, 0.95f);
            var part = Math.Clamp(filled - i, 0, 1);
            if (part > 0)
            {
                var cut = (float)(part * segW + slant);
                cv.FillV(seg, sx, y, Col.Hex(0xe9ffad), LimeDark, 1, clipRight: sx + cut);
            }
        }
    }

    /// <summary>Dashes along the straight sides of a rounded rectangle.</summary>
    private void DashedFrame(Canvas cv, int x, int y, int w, int h, int radius, int thick, int dash, int gap, Col color, float alpha)
    {
        var hDash = Cached($"hd{dash}x{thick}", () => Gfx.RoundRect(dash, thick, thick / 2f));
        var vDash = Cached($"vd{thick}x{dash}", () => Gfx.RoundRect(thick, dash, thick / 2f));
        for (var px = x + radius; px + dash <= x + w - radius; px += dash + gap)
        {
            cv.Fill(hDash, px, y, color, alpha);
            cv.Fill(hDash, px, y + h - thick, color, alpha);
        }
        for (var py = y + radius; py + dash <= y + h - radius; py += dash + gap)
        {
            cv.Fill(vDash, x, py, color, alpha);
            cv.Fill(vDash, x + w - thick, py, color, alpha);
        }
    }

    /// <summary>Text with a navy outline and a soft drop shadow, filled with a vertical gradient. Y = top of the capitals.</summary>
    private void Text(Canvas cv, HudFont font, string text, int x, int capTop, Col top, Col bottom, int outline, int shadow, float alpha = 1)
    {
        var ink = Ink(font, text);
        var ox = x - ink.Pad;
        var oy = capTop - ink.Pad - font.CapTop;
        if (outline > 0)
        {
            var o = Cached($"o{outline}|{font.Key}|{text}", () => Gfx.Dilate(ink.M, outline));
            if (shadow > 0)
            {
                var sh = Cached($"s{outline}|{font.Key}|{text}", () => Gfx.Blur(o, shadow));
                var sp = (sh.W - o.W) / 2;
                cv.Fill(sh, ox - outline - sp, oy - outline - sp + shadow, Navy, 0.7f * alpha);
            }
            cv.Fill(o, ox - outline, oy - outline, Navy, alpha);
        }
        cv.FillV(ink.M, ox, oy, top, bottom, alpha, fromY: capTop, toY: capTop + font.Cap);
    }

    private void Outline(Canvas cv, Mask m, int x, int y, int r)
    {
        var o = Cached($"ol{r}|{m.GetHashCode()}", () => Gfx.Dilate(m, r));
        cv.Fill(o, x - r, y - r, Navy);
    }

    // ---- Glyphs (GDI draws them in white on black, the grey levels are the coverage) ------------------------------

    private sealed record TextInk(Mask M, int Pad, int Cell);

    private sealed class HudFont
    {
        public required IntPtr Handle;
        public required string Key;
        public int CapTop, Cap; // capital letters: top inside the text cell, height
    }

    private HudFont Font(string face, int px, int weight)
    {
        var key = $"{face}/{px}/{weight}";
        if (_fonts.TryGetValue(key, out var f))
        {
            return f;
        }
        var handle = CreateFontW(-Math.Max(4, px), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 4 /* ANTIALIASED_QUALITY: grey levels */, 0, face);
        f = new HudFont { Handle = handle, Key = key };
        _fonts[key] = f;
        var ink = Ink(f, "H");
        int top = -1, bottom = -1;
        for (var yy = 0; yy < ink.M.H && top < 0; yy++)
            for (var xx = 0; xx < ink.M.W; xx++)
                if (ink.M.A[yy * ink.M.W + xx] > 0.5f) { top = yy; break; }
        for (var yy = ink.M.H - 1; yy >= 0 && bottom < 0; yy--)
            for (var xx = 0; xx < ink.M.W; xx++)
                if (ink.M.A[yy * ink.M.W + xx] > 0.5f) { bottom = yy; break; }
        f.CapTop = Math.Max(0, top - ink.Pad);
        f.Cap = Math.Max(1, bottom - top + 1);
        _cache.Remove($"t|{key}|H"); // drawn with the padding of a font without metrics yet
        return f;
    }

    private int Advance(HudFont font, string text) => Measure(font, text).Width;

    private (int Width, int Height) Measure(HudFont font, string text)
    {
        var dc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            SelectObject(dc, font.Handle);
            var r = new RECT();
            DrawTextW(dc, text.Length == 0 ? " " : text, -1, ref r, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
            return (r.Right - r.Left, r.Bottom - r.Top);
        }
        finally
        {
            DeleteDC(dc);
        }
    }

    private TextInk Ink(HudFont font, string text)
    {
        var key = $"t|{font.Key}|{text}";
        if (_cache.TryGetValue(key, out var cached))
        {
            return new TextInk(cached, Pad(font), Measure(font, text).Height);
        }
        var (w, h) = Measure(font, text);
        var pad = Pad(font);
        var bw = w + 2 * pad;
        var bh = h + 2 * pad;
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = bw, biHeight = -bh, biPlanes = 1, biBitCount = 32 };
        var bmp = CreateDIBSection(dc, ref bi, 0, out var bits, IntPtr.Zero, 0);
        var mask = new Mask(bw, bh);
        try
        {
            var oldBmp = SelectObject(dc, bmp);
            var oldFont = SelectObject(dc, font.Handle);
            SetBkMode(dc, 1);
            SetTextColor(dc, 0xFFFFFF);
            var r = new RECT { Left = pad, Top = pad, Right = pad + w + pad, Bottom = pad + h };
            DrawTextW(dc, text, -1, ref r, DT_SINGLELINE | DT_NOPREFIX);
            GdiFlush();
            var px = new byte[bw * bh * 4];
            Marshal.Copy(bits, px, 0, px.Length);
            for (var i = 0; i < bw * bh; i++)
            {
                mask.A[i] = px[i * 4 + 1] / 255f;
            }
            SelectObject(dc, oldFont);
            SelectObject(dc, oldBmp);
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(dc);
        }
        _cache[key] = mask;
        return new TextInk(mask, pad, h);
    }

    private static int Pad(HudFont font) => 4 + font.Cap / 6;

    private Mask Cached(string key, Func<Mask> make)
    {
        if (!_cache.TryGetValue(key, out var m))
        {
            m = make();
            _cache[key] = m;
        }
        return m;
    }

    /// <summary>Makes the embedded Anton font usable by this process (nothing is installed on the PC).</summary>
    private static bool LoadAnton()
    {
        try
        {
            using var stream = typeof(HudRenderer).Assembly.GetManifestResourceStream("FortniteChestTracker.wwwroot.fonts.Anton-Regular.ttf");
            if (stream == null)
            {
                return false;
            }
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            var mem = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, mem, bytes.Length);
            uint count = 0;
            var handle = AddFontMemResourceEx(mem, (uint)bytes.Length, IntPtr.Zero, ref count);
            Marshal.FreeHGlobal(mem); // GDI keeps its own copy
            return handle != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var f in _fonts.Values)
        {
            DeleteObject(f.Handle);
        }
        _fonts.Clear();
        _cache.Clear();
    }

    // ---- Shared renderer for the page and the OBS file (any thread) ----------------------------------------------

    private static readonly object SharedLock = new();
    private static HudRenderer? _shared;

    /// <summary>PNG of the overlay (transparent background), drawn at <paramref name="scale"/>.</summary>
    public static byte[] RenderPng(HudView view, float scale)
    {
        lock (SharedLock)
        {
            _shared ??= new HudRenderer();
            return _shared.Render(view, scale).ToPng();
        }
    }

    // ---- Win32 ------------------------------------------------------------------------------------------------

    private const uint DT_SINGLELINE = 0x20, DT_CALCRECT = 0x400, DT_NOPREFIX = 0x800;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32.dll")] private static extern IntPtr AddFontMemResourceEx(IntPtr font, uint size, IntPtr reserved, ref uint fonts);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);
}

/// <summary>A colour (0 … 1).</summary>
internal readonly record struct Col(float R, float G, float B)
{
    public static Col Hex(int rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
    public static Col Mix(Col a, Col b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
}

/// <summary>Coverage (0 … 1) of a shape or a text.</summary>
internal sealed class Mask
{
    public Mask(int w, int h)
    {
        W = Math.Max(1, w);
        H = Math.Max(1, h);
        A = new float[W * H];
    }

    public int W { get; }
    public int H { get; }
    public float[] A { get; }
}

/// <summary>A picture (premultiplied RGBA 0 … 1).</summary>
internal sealed class Img
{
    public required int W, H;
    public required float[] P;

    public static Img? Load(string resource)
    {
        using var stream = typeof(Img).Assembly.GetManifestResourceStream(resource);
        if (stream == null)
        {
            return null;
        }
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        if (Png.Decode(ms.ToArray()) is not { } png)
        {
            return null;
        }
        var p = new float[png.Width * png.Height * 4];
        for (var i = 0; i < png.Width * png.Height; i++)
        {
            var a = png.Rgba[i * 4 + 3] / 255f;
            p[i * 4] = png.Rgba[i * 4] / 255f * a;
            p[i * 4 + 1] = png.Rgba[i * 4 + 1] / 255f * a;
            p[i * 4 + 2] = png.Rgba[i * 4 + 2] / 255f * a;
            p[i * 4 + 3] = a;
        }
        return new Img { W = png.Width, H = png.Height, P = p };
    }
}

/// <summary>Draws masks and pictures into a frame ("source over").</summary>
internal sealed class Canvas
{
    private readonly HudFrame _f;

    public Canvas(HudFrame f) => _f = f;

    public void Fill(Mask m, int ox, int oy, Col c, float alpha = 1) => FillV(m, ox, oy, c, c, alpha);

    /// <summary>Fills a mask with a vertical gradient (between <paramref name="fromY"/> and <paramref name="toY"/>, default the mask).</summary>
    public void FillV(Mask m, int ox, int oy, Col top, Col bottom, float alpha = 1, int? fromY = null, int? toY = null, float clipRight = float.MaxValue)
    {
        var W = _f.Width;
        var H = _f.Height;
        var p = _f.Pixels;
        var y0 = fromY ?? oy;
        var span = Math.Max(1, (toY ?? oy + m.H) - y0);
        for (var my = 0; my < m.H; my++)
        {
            var y = oy + my;
            if (y < 0 || y >= H)
            {
                continue;
            }
            var c = Col.Mix(top, bottom, Math.Clamp((y - y0) / (float)span, 0, 1));
            for (var mx = 0; mx < m.W; mx++)
            {
                var x = ox + mx;
                if (x < 0 || x >= W)
                {
                    continue;
                }
                var a = m.A[my * m.W + mx] * alpha;
                if (clipRight < float.MaxValue)
                {
                    a *= Math.Clamp(clipRight - x, 0, 1);
                }
                if (a <= 0)
                {
                    continue;
                }
                var i = (y * W + x) * 4;
                var k = 1 - a;
                p[i] = c.R * a + p[i] * k;
                p[i + 1] = c.G * a + p[i + 1] * k;
                p[i + 2] = c.B * a + p[i + 2] * k;
                p[i + 3] = a + p[i + 3] * k;
            }
        }
    }

    /// <summary>Draws a picture scaled into a rectangle (each pixel averages the source pixels it covers).</summary>
    public void Draw(Img img, float x, float y, float w, float h, float alpha = 1)
    {
        var W = _f.Width;
        var H = _f.Height;
        var p = _f.Pixels;
        var sx = img.W / w;
        var sy = img.H / h;
        var n = Math.Clamp((int)MathF.Ceiling(Math.Max(sx, sy)), 1, 6);
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y), x1 = (int)MathF.Ceiling(x + w), y1 = (int)MathF.Ceiling(y + h);
        for (var py = Math.Max(0, y0); py < Math.Min(H, y1); py++)
        {
            for (var px = Math.Max(0, x0); px < Math.Min(W, x1); px++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (var j = 0; j < n; j++)
                {
                    var v = (py + (j + 0.5f) / n - y) * sy;
                    if (v < 0 || v >= img.H) continue;
                    for (var i = 0; i < n; i++)
                    {
                        var u = (px + (i + 0.5f) / n - x) * sx;
                        if (u < 0 || u >= img.W) continue;
                        var s = ((int)v * img.W + (int)u) * 4;
                        r += img.P[s];
                        g += img.P[s + 1];
                        b += img.P[s + 2];
                        a += img.P[s + 3];
                    }
                }
                var inv = alpha / (n * n);
                a *= inv;
                if (a <= 0)
                {
                    continue;
                }
                var d = (py * W + px) * 4;
                var k = 1 - a;
                p[d] = r * inv + p[d] * k;
                p[d + 1] = g * inv + p[d + 1] * k;
                p[d + 2] = b * inv + p[d + 2] * k;
                p[d + 3] = a + p[d + 3] * k;
            }
        }
    }
}

/// <summary>Shapes and effects on masks.</summary>
internal static class Gfx
{
    /// <summary>Rounded rectangle with smooth edges.</summary>
    public static Mask RoundRect(float w, float h, float radius)
    {
        var m = new Mask((int)MathF.Ceiling(w), (int)MathF.Ceiling(h));
        radius = Math.Min(radius, Math.Min(w, h) / 2);
        float hx = w / 2, hy = h / 2;
        for (var y = 0; y < m.H; y++)
        {
            for (var x = 0; x < m.W; x++)
            {
                var qx = MathF.Abs(x + 0.5f - hx) - (hx - radius);
                var qy = MathF.Abs(y + 0.5f - hy) - (hy - radius);
                var d = MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0)) + MathF.Min(MathF.Max(qx, qy), 0) - radius;
                m.A[y * m.W + x] = Math.Clamp(0.5f - d, 0, 1);
            }
        }
        return m;
    }

    /// <summary>Any polygon (even-odd), smoothed with 4×4 samples per pixel.</summary>
    public static Mask Poly(float w, float h, (float X, float Y)[] pts)
    {
        var m = new Mask((int)MathF.Ceiling(w), (int)MathF.Ceiling(h));
        const int n = 4;
        for (var y = 0; y < m.H; y++)
        {
            for (var x = 0; x < m.W; x++)
            {
                var hits = 0;
                for (var j = 0; j < n; j++)
                {
                    for (var i = 0; i < n; i++)
                    {
                        if (Inside(pts, x + (i + 0.5f) / n, y + (j + 0.5f) / n)) hits++;
                    }
                }
                m.A[y * m.W + x] = hits / (float)(n * n);
            }
        }
        return m;
    }

    /// <summary>Five-pointed star filling a w × h box.</summary>
    public static Mask Star(int w, int h)
    {
        var pts = new (float, float)[10];
        for (var i = 0; i < 10; i++)
        {
            var r = (i % 2 == 0 ? 0.5f : 0.22f);
            var ang = -MathF.PI / 2 + i * MathF.PI / 5;
            pts[i] = (w / 2f + MathF.Cos(ang) * r * w, h * 0.54f + MathF.Sin(ang) * r * h);
        }
        return Poly(w, h, pts);
    }

    private static bool Inside((float X, float Y)[] pts, float x, float y)
    {
        var inside = false;
        for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
        {
            if ((pts[i].Y > y) != (pts[j].Y > y) && x < (pts[j].X - pts[i].X) * (y - pts[i].Y) / (pts[j].Y - pts[i].Y) + pts[i].X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>Keeps the left part, then fades linearly to <paramref name="end"/> × the coverage at the right edge.</summary>
    public static Mask FadeRight(Mask m, float from, float end)
    {
        var start = m.W * from;
        for (var x = 0; x < m.W; x++)
        {
            var k = x <= start ? 1 : 1 - (1 - end) * (x - start) / (m.W - start);
            for (var y = 0; y < m.H; y++)
            {
                m.A[y * m.W + x] *= k;
            }
        }
        return m;
    }

    /// <summary>Grows a mask by a round radius (outlines). The result is bigger by r on each side.</summary>
    public static Mask Dilate(Mask src, float r)
    {
        var R = (int)MathF.Ceiling(r);
        var o = new Mask(src.W + 2 * R, src.H + 2 * R);
        var offsets = new List<(int Dx, int Dy, float W)>();
        for (var dy = -R; dy <= R; dy++)
        {
            for (var dx = -R; dx <= R; dx++)
            {
                var w = Math.Clamp(r + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0, 1);
                if (w > 0) offsets.Add((dx, dy, w));
            }
        }
        for (var y = 0; y < src.H; y++)
        {
            for (var x = 0; x < src.W; x++)
            {
                var a = src.A[y * src.W + x];
                if (a <= 0.01f)
                {
                    continue;
                }
                foreach (var (dx, dy, w) in offsets)
                {
                    var i = (y + R + dy) * o.W + x + R + dx;
                    var v = a * w;
                    if (v > o.A[i]) o.A[i] = v;
                }
            }
        }
        return o;
    }

    /// <summary>Soft blur (three box passes ≈ gaussian). The result is bigger by 2r on each side.</summary>
    public static Mask Blur(Mask src, float r)
    {
        var P = (int)MathF.Ceiling(r * 2);
        var o = new Mask(src.W + 2 * P, src.H + 2 * P);
        for (var y = 0; y < src.H; y++)
        {
            Array.Copy(src.A, y * src.W, o.A, (y + P) * o.W + P, src.W);
        }
        var b = Math.Max(1, (int)MathF.Round(r / 1.6f));
        var tmp = new float[Math.Max(o.W, o.H)];
        for (var pass = 0; pass < 3; pass++)
        {
            for (var y = 0; y < o.H; y++) BoxLine(o.A, y * o.W, 1, o.W, b, tmp);
            for (var x = 0; x < o.W; x++) BoxLine(o.A, x, o.W, o.H, b, tmp);
        }
        return o;
    }

    private static void BoxLine(float[] a, int start, int step, int count, int r, float[] tmp)
    {
        float sum = 0;
        var inv = 1f / (2 * r + 1);
        for (var i = 0; i < count; i++) tmp[i] = a[start + i * step];
        for (var i = 0; i <= r && i < count; i++) sum += tmp[i];
        for (var i = 0; i < count; i++)
        {
            a[start + i * step] = sum * inv;
            var add = i + r + 1;
            var sub = i - r;
            if (add < count) sum += tmp[add];
            if (sub >= 0) sum -= tmp[sub];
        }
    }
}
