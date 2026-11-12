using System.Runtime.Versioning;

namespace FortniteChestTracker;

// The squad overlay: one column per member, their outfit above their chests (last match or session), the leader
// circled in lime with a star, "IN A MATCH" on those still playing.
[SupportedOSPlatform("windows")]
internal sealed partial class HudRenderer
{
    public HudFrame RenderSquad(SquadView v, float scale, bool edit = false, int minWidth = 0, float pop = 0)
    {
        if (_cache.Count > 160)
        {
            _cache.Clear();
        }
        int S(float n) => (int)MathF.Round(n * scale);
        var anton = Anton.Value ? "Anton" : "Impact";
        var fTag = Font(anton, S(13), 400);
        var fNum = Font(anton, S(36), 400);
        var fName = Font(anton, S(13), 400);
        var fLetter = Font(anton, S(30), 400);
        var fIcon = Font("Segoe MDL2 Assets", S(13), 400);

        var margin = S(18);
        var icon = S(64);
        var colW = S(92);
        var colGap = S(6);
        var members = v.Members;
        var title = (L.T("ESCOUADE · ", "SQUAD · ", "ESCUADRÓN · ") + (v.Period == "session"
            ? L.T("SESSION", "SESSION", "SESIÓN")
            : L.T("DERNIÈRE PARTIE", "LAST MATCH", "ÚLTIMA PARTIDA"))).ToUpper(L.Culture);
        var tagW = TagWidth(fTag, HudMode.Idle, title, S);
        var tagH = TagHeight(fTag);
        var lockSpace = edit ? S(40) : 0;

        var n = Math.Max(1, members.Count);
        var contentW = Math.Max(n * colW + (n - 1) * colGap, tagW + lockSpace);
        var w = Math.Max(minWidth, contentW + 2 * margin);
        var h = margin + tagH + S(14) + icon + S(12) + fNum.Cap + S(9) + fName.Cap + margin + S(4);

        var f = NewFrame(w, h);
        var cv = new Canvas(f);
        Backdrop(cv, w, h, margin, edit, S);
        Tag(cv, fTag, HudMode.Idle, title, margin, margin, tagW, S);

        // Leader: the most chests (no leader while nobody has any)
        var best = members.Where(m => !(m.Playing && v.Period == "last")).Select(m => m.Chests ?? 0).DefaultIfEmpty(0).Max();
        var columnsW = members.Count * colW + (members.Count - 1) * colGap;
        var x = margin + Math.Max(0, (w - 2 * margin - columnsW) / 2);
        var top = margin + tagH + S(14);
        var tile = Cached($"sqtile{icon}", () => Gfx.RoundRect(icon, icon, S(14)));
        var ring = Cached($"sqring{icon}", () => Gfx.Dilate(tile, S(3)));
        var shadow = Cached($"sqshadow{icon}", () => Gfx.Blur(Gfx.Dilate(tile, S(2)), S(6)));
        foreach (var m in members)
        {
            var ix = x + (colW - icon) / 2;
            var waiting = m.Playing && v.Period == "last";
            var leader = best > 0 && !waiting && (m.Chests ?? 0) == best;

            // ---- Outfit tile
            cv.Fill(shadow, ix - (shadow.W - icon) / 2, top - (shadow.H - icon) / 2 + S(3), Navy, 0.75f);
            cv.Fill(ring, ix - S(3), top - S(3), leader ? Lime : m.Me ? Aqua : Navy, leader || m.Me ? 1f : 0.9f);
            cv.FillV(tile, ix, top, Panel, Deep);
            if (SkinCache.Image(m.Skin) is { } skin)
            {
                cv.Draw(skin, ix, top, icon, icon, 1, tile);
            }
            else
            {
                var letter = m.Name.Length > 0 ? m.Name[..1].ToUpper(L.Culture) : "?";
                Text(cv, fLetter, letter, ix + (icon - Advance(fLetter, letter)) / 2, top + (icon - fLetter.Cap) / 2, Muted, Muted, S(2), 0);
            }
            if (leader)
            {
                var ss = S(22);
                var star = Cached($"sqstar{ss}", () => Gfx.Star(ss, ss));
                Outline(cv, star, ix + icon - ss + S(8), top - S(8), S(2));
                cv.FillV(star, ix + icon - ss + S(8), top - S(8), Col.Hex(0xfff2a8), Amber);
            }
            if (m.Treasure)
            {
                var found = L.T("TRÉSOR !", "TREASURE!", "¡TESORO!");
                var fw = TagWidth(fTag, HudMode.InProgress, found, S);
                Tag(cv, fTag, HudMode.InProgress, found, x + (colW - fw) / 2, top + icon - tagH / 2 - (m.Playing ? tagH + S(3) : 0), fw, S);
            }
            if (m.Playing)
            {
                var playing = L.T("EN PARTIE", "IN A MATCH", "EN PARTIDA");
                var pw = TagWidth(fTag, HudMode.Live, playing, S);
                Tag(cv, fTag, HudMode.Live, playing, x + (colW - pw) / 2, top + icon - tagH / 2, pw, S);
            }

            // ---- Chests
            var number = waiting ? "…" : m.Chests is { } c ? L.N(c) : "—";
            var numW = Advance(fNum, number);
            var numX = x + (colW - numW) / 2;
            var numTop = top + icon + S(12);
            if (m.Fresh && pop > 0)
            {
                var ink = Ink(fNum, number);
                var g = Cached($"sqglow{number}{fNum.Key}", () => Gfx.Blur(Gfx.Dilate(ink.M, S(4)), S(10)));
                var pad = (g.W - ink.M.W) / 2;
                cv.Fill(g, numX - ink.Pad - pad, numTop - ink.Pad - fNum.CapTop - pad, Lime, 0.9f * pop);
            }
            var (numTopCol, numBottomCol) = leader ? (Col.Hex(0xe9ffad), Lime) : m.Chests == null || waiting ? (Muted, Muted) : (White, Ice);
            Text(cv, fNum, number, numX, numTop, numTopCol, numBottomCol, S(3), S(2));

            // ---- Name (cut to the column)
            var name = m.Name;
            while (name.Length > 1 && Advance(fName, name) > colW - S(4))
            {
                name = name[..^2] + "…";
                if (name.Length <= 2) break;
            }
            var nameTop = numTop + fNum.Cap + S(9);
            var nameCol = m.Me ? Aqua : Muted;
            Text(cv, fName, name, x + (colW - Advance(fName, name)) / 2, nameTop, nameCol, nameCol, S(2), 0);

            x += colW + colGap;
        }

        if (edit)
        {
            LockButton(cv, f, fIcon, S);
        }
        return f;
    }

    // ---- Shared renderer for the page preview (any thread) -------------------------------------------------------

    /// <summary>PNG of the squad overlay (transparent background), drawn at <paramref name="scale"/>.</summary>
    public static byte[] RenderSquadPng(SquadView view, float scale)
    {
        lock (SharedLock)
        {
            _shared ??= new HudRenderer();
            return _shared.RenderSquad(view, scale).ToPng();
        }
    }
}
