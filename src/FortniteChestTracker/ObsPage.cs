using System.Text;

namespace FortniteChestTracker;

/// <summary>
/// overlay.html in the data folder, for OBS (browser source): the very same HUD as the game overlay, as a transparent
/// picture (drawn at 2× and shown at 1× so it stays sharp), refreshed every few seconds.
/// </summary>
public static class ObsPage
{
    /// <summary>Rewrites the page (called on every overlay update). Never throws: the page is a convenience.</summary>
    public static void Write(Store store, HudView view)
    {
        try
        {
            // Written next to it, then swapped in: OBS never reads a half-written page.
            var tmp = store.OverlayPath + ".tmp";
            File.WriteAllText(tmp, Html(view), new UTF8Encoding(false));
            File.Move(tmp, store.OverlayPath, overwrite: true);
        }
        catch
        {
            // never stop the tracker for it
        }
    }

    /// <summary>overlay-friends.html: the squad overlay for OBS (empty, transparent, while there is no squad).</summary>
    public static void WriteSquad(Store store, SquadView view)
    {
        try
        {
            var tmp = store.SquadOverlayPath + ".tmp";
            var png = view.Members.Count > 1 && OperatingSystem.IsWindows() ? HudRenderer.RenderSquadPng(view, 2f) : null;
            File.WriteAllText(tmp, Page(png, L.T("Overlay amis", "Friends overlay", "Overlay de amigos")), new UTF8Encoding(false));
            File.Move(tmp, store.SquadOverlayPath, overwrite: true);
        }
        catch
        {
            // never stop the tracker for it
        }
    }

    public static string Html(HudView v) =>
        Page(OperatingSystem.IsWindows() ? HudRenderer.RenderPng(v, 2f) : null, L.T("Overlay coffres", "Chest overlay", "Overlay de cofres"));

    private static string Page(byte[]? png, string title)
    {
        var picture = "";
        if (png != null)
        {
            var width = png.Length > 24 ? (png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19]) / 2 : 0;
            picture = $"<img alt=\"\" width=\"{width}\" src=\"data:image/png;base64,{Convert.ToBase64String(png)}\">";
        }
        return $$"""
            <!doctype html><html lang="{{L.Code}}"><head><meta charset="utf-8"><meta http-equiv="refresh" content="5">
            <title>{{title}}</title>
            <style>html,body{margin:0;background:transparent}img{display:block}</style>
            </head><body>{{picture}}</body></html>
            """;
    }
}
