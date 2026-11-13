# Changelog

## Unreleased

**Older matches (without replay)**
- The time played of the season is the one fortnite.gg gives (30 h stays 30 h), no longer the replays' time plus an
  estimate (a replay runs until you leave the match, so it counts more than Fortnite does: that gave 36 h).
- The season's chests without replay now count for the treasure hunt too (the hunt starts with the season): the
  chances of each treasure, the "chests since" line, the goal of the overlay and the friends' odds include them ("≈").
  The luck figures stay on the replays only (what those matches gave is unknown).

## 1.3.3 (2026-11-13)

**Updates**
- Fixed: updates failed every time from 1.3.1 and 1.3.2 ("This instance has already started one or more requests").
  Apps on 1.3.1 or 1.3.2 cannot get this fix by themselves: download this version once from the release page, the
  next updates work again.

## 1.3.2 (2026-11-13)

**Older matches (without replay)**
- Now only for the current season: copy your matches and hours played this season from fortnite.gg (all modes, both
  needed). They are compared with the replays of the same season (same game version), and the time of the matches
  without replay comes from fortnite.gg's minutes per match. The per-mode fields and the fortnite-api.com key are gone,
  and numbers saved by earlier versions (per mode, all time) are cleared: enter this season's again.
- Once added to the total, the time played and the match count include them too (home page and share picture, "≈"),
  not only the chests. When a new season starts, the page asks for the new season's numbers.
- Friends: the "Season" leaderboard (and the squad picture) adds the season's matches without replay of the players
  who added them, shown "≈".

**Overlay**
- The overlays are 80 % of their old size by default (an overlay still on 100 % goes to 80 % once; pick another size
  in the settings if you like).

## 1.3.1 (2026-11-13)

**Security**
- Updates are signed: the app only installs a new version signed with the release key (a release uploaded any other
  way is refused, even with a correct checksum).
- Friend invite links: the request is only sent when you click "Add" in the Friends tab, never by a web address, and a
  link can no longer pass other options to the app.
- The local interface refuses requests coming from other websites, even for a picture.
- The Friends key is no longer in the page data: it is shown only when you click "Show".
- Friends: requests by name go to the Epic account that really has that name; names and summaries are cleaned.
- Size limits on imports and downloads; the season calendar cannot be locked by a bogus version.

## 1.3.0 (2026-11-12)

**Friends**
- New Friends tab: add the friends who have the app (by Epic name, from your recent teammates or with your invite
  link), see their chests, their treasure odds, the treasures they found and their last match.
- Ranking of who opens the most chests: last match, session, season. Share it as a picture with everyone's outfit.
- Squad mode: invite a friend (or turn it on after a match played together) and everyone's chests show up a few
  seconds after each of their matches.
- Friends overlay: a second in-game overlay with each player's outfit above their chests, its own settings, and an
  OBS version.
- A notification when a friend finds a treasure, and a recap of the evening when Fortnite closes.
- Shared only with the friends you accept: Epic name, outfit, chests and treasure odds. Turning it off deletes them
  from the server.

**Matches**
- Rare items around you: the treasure hunt items other players had near you.

**App**
- On first start, the replays still on disk are read again for outfits and rare items (about 2 minutes).

## 1.2.0 (2026-11-11)

**Treasure hunt**
- Season card in Treasures: next Treasure Hour with a countdown, this week's prizes (checked off when you get them),
  14-day timer for the pirate token, end of the season.
- Your chances now count Treasure Hour boosts, leave Ranked out (no treasures there) and work week by week.
- Squad chance: your teammates' chests count too, only those opened near you (the replay sees no further).
- Notifications: 10 minutes before each Treasure Hour, and when a match gives you a treasure.
- The calendar updates itself from the website: no new version needed for new Treasure Hours or prizes.

**Overlay**
- Last match line: its chests, its length and the time of the last update, still shown during the next match.
- Treasure Hour banner while one is on, or when one starts within 12 hours.
- Choose the blocks shown, hide it during matches, move it to a corner in one click.

**App**
- The 3D island stays smooth (60 fps), even with Fortnite open; it can also be turned off.
- Export and import your history (backup, another PC).
- Shorter texts everywhere.
- On first start, the replays still on disk are read again for the squad chance (about 2 minutes).

## 1.1.1 (2026-11-10)

- The 3D island no longer stutters for a few seconds when the app opens.
- About: credit to Ayman Studio, with a link to [@ayman_studioo](https://x.com/ayman_studioo) on X.
- Licence: the code is MIT, but the exe includes a GPL-3.0 replay decompressor, so the exe is distributed under the
  GPL-3.0. Its full text is now in the licences (About → Licences).

## 1.1.0 (2026-11-10)

- New name everywhere: the app is now `FortniteChestTracker.exe`. An installed 1.0.x copy updates itself as usual,
  then renames itself; your history, settings, "open with Fortnite" and shortcuts carry over on their own.
- Website: [fortnite-chest-tracker.com](https://fortnite-chest-tracker.com/), also shown under the HUD and on the
  share image.
- The overlay preview (settings and home) now sits on a blurred island picture instead of the drawn plain.
- Licences of the bundled parts are one click away in About.
- Removed: the HTML recap, the CSV export and the command-line options. Everything is in the interface.

## 1.0.3 (2026-11-10)

- The HUD plays "+N" when a match is counted: Fortnite's replay can only be read once the match is over, so that is
  when the chests arrive, all at once. (The "+1 for each chest during the match" announced before could not happen:
  the replay is encrypted until the end. The README, the website and its HUD demo now say how it really works.)

## 1.0.2 (2026-11-10)

- Small windows (1280×720, 1366×768): nothing cut any more (chest types, treasures list, older matches estimate);
  one notice at a time with "+N" for the others.
- Big screens (1920 wide and up): the interface scales up instead of looking tiny.
- Without a graphics card (software rendering): the 3D island and the animations switch to a light mode so the PC
  and the page stay responsive.
- Keyboard: windows keep the focus, Escape closes the share window, visible focus on the tabs.
- Polish: Spanish numbers always grouped, plurals, wording, icons, an empty Statistics page message, "Reinstall"
  when another copy is installed, the matches tab says when only the latest 300 are listed.

## 1.0.1 (2026-11-10)

- About a third less memory while the counter waits between matches.
- A treasure that did not come from a golden chest is shown as "Treasure (gizmo or reward)": the replays do not say
  which one it is (same generic grant for both). Your luck is computed on all treasures together in that case.

## 1.0.0 (2026-11-10)

First release of **Fortnite - Chest Tracker**.

**Counting**
- Reads the replays Fortnite saves after each match (read-only): chests opened or destroyed, treasure hunt prizes
  (golden chests with their prize id, every treasure received with the chest it came from), ammo boxes and other
  containers apart. Battle Royale only; several accounts on one PC get their own profiles.
- Safe history: `data.json` flushed to disk with the previous version kept as `.bak` (used if the file is broken or
  missing); a locked file is retried; a timeout or a locked replay is tried again later instead of being marked
  unreadable.
- Match times stored as UTC (they were shown one hour off in France).
- **Replay archive** (optional): a copy of every counted replay, before Fortnite deletes it.

**Web interface** (local page, `http://127.0.0.1:5317`, updates by itself)
- Five tabs: Home, Matches, Statistics, Treasures, Settings (keys 1–5). Fortnite-like look with its own colours,
  Anton titles, calm animations, Kenney cursors. First-launch setup in the page.
- Home: total and time played, the treasure goal (pirate token by default, any treasure, or Auto) with Epic's drop
  rates and Treasure Hours boosts, the last match, and a 3D island (three.js, Kenney models) where a miner digs
  towards the treasure while a pirate who gave up sits in the old tunnel. Characters and animals react to clicks.
- Matches: per match, chests by type, eliminations, duration and the **looting timeline** (chests per minute, first
  chest, chests in the first 5 minutes).
- Statistics: average, best match, best day, Victory Royales, chests per day, chest types, **do chests help you win?**
  (placement by number of chests opened) and **milestones**, announced when reached.
- Treasures: rates, your chance and the expected count for each (weekly treasures at most once a week), **your luck
  so far** ("luckier than X% of players").
- Share picture (stats + the island, goal of your choice), to download or copy.
- Older matches estimate (fortnite.gg or fortnite-api.com × your own average per mode), can be added to the total.
- Tray icon instead of a console window; "Open with Fortnite" starts the tracker with the game and closes it after.
- **Install on this PC** (optional, offered at first launch): Start menu and desktop shortcuts, an entry in Windows'
  installed apps, clean uninstall (your data is kept).
- Local server hardened: Host header checked, secret token and same origin for every command, bounded requests,
  strict CSP.

**In-game HUD**
- A floating, per-pixel transparent HUD (no window frame): chests of the last match (counted once the match is over),
  the goal chance and dig bar, total / session / treasures. **Full** or **Compact** layout, size and opacity, edit
  mode (drag, padlock), Ctrl+Shift+O / Ctrl+Shift+L, sharp on every screen (per-monitor DPI), light on the game
  (no allocation per frame, 30 fps animation). The OBS page and the previews show the same picture.

**Website** https://fortnite-chest-tracker.com/ — FR/EN/ES pages, live 3D island, HUD demo, odds
calculator, gallery, FAQ.

**Updates and build**
- Updates from this repository's releases only, with the SHA-256 digest checked; offered, automatic or off. A failed
  update puts the previous exe back.
- xUnit tests (119, 3 of them on real replays via `FCT_TEST_REPLAYS`); GitHub Actions tests every push and
  publishes the exe for `v*` tags.
