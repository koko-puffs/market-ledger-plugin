# Publishing the Market Ledger plugin

How to release the plugin and let players install it from Koko's custom plugin repository.

## 1. The repository

1. On GitHub, **koko-puffs/market-ledger-plugin** (public, so `raw.githubusercontent.com` and release
   downloads work for everyone). `LICENSE` is **AGPL-3.0**, the norm for Dalamud plugins; the bundled
   fonts keep their own OFL licence.
2. In the repo: **Settings → Actions → General → Workflow permissions → Read and write
   permissions** → Save. The release workflow needs it to create releases and update `repo.json`.
3. `repo.json` starts as an empty list (`[]`): the first release makes the plugin's entry.

The **Build** workflow now runs on every push: it downloads the current Dalamud, runs the tests, builds
the plugin and keeps the zip as an artifact.

## 2. Release a version

1. Bump `<Version>` in `MarketLedger/MarketLedger.csproj` if you like (the workflow overrides it
   with the tag anyway).
2. Tag and push:

   ```sh
   git tag v0.1.0
   git push origin v0.1.0
   ```

   (Or Actions → Release → Run workflow, with a version.)
3. The **Release** workflow builds with DalamudPackager, creates the GitHub release **v0.1.0** with
   `MarketLedger.zip`, then commits an updated `repo.json` to `main`: `AssemblyVersion`,
   `DalamudApiLevel`, the three `DownloadLink*` URLs pointing at that release's zip, and `LastUpdate`.
4. Check it:
   - The release page has `MarketLedger.zip` (with `MarketLedger.dll`, `MarketLedger.json`, the
     licences).
   - `https://raw.githubusercontent.com/koko-puffs/market-ledger-plugin/main/repo.json` shows the new
     version and links (raw.githubusercontent caches for a few minutes), and so, within about ten
     minutes more, does `https://www.kokos-tools.com/dalamud/repo.json` (see below).
   - `https://raw.githubusercontent.com/koko-puffs/market-ledger-plugin/main/MarketLedger/images/icon.png`
     loads (it's the installer's icon).

## Koko's plugin repository

Players add one address, `https://www.kokos-tools.com/dalamud/repo.json`, and get every plugin Koko
makes, now and later. It's served by the kokos-tools site (`api/dalamud-repo.js` there): it reads each
plugin's own `repo.json` from GitHub (this repository's included: the release workflow keeps it up to
date, as above) and hands Dalamud them all as one list, cached for about ten minutes. This plugin is
already in its `PLUGIN_REPOS`: until it has a release (its `repo.json` is `[]`), the site lists the
others. Players change nothing.

## 3. Install it in the game

Players (and you, for testing):

1. In game, type `/xlsettings` (or Dalamud Settings from the system menu).
2. **Experimental** tab → **Custom Plugin Repositories** → paste
   `https://www.kokos-tools.com/dalamud/repo.json` → press **+**
   → make sure it's ticked (enabled) → **Save and Close**. (If you added this plugin's own
   `raw.githubusercontent.com/…/repo.json` earlier, remove that one, or Dalamud lists the plugin twice.)
3. `/xlplugins` → **All Plugins** → search **Market Ledger** → **Install**.
4. `/ledger` opens it.

For testing a build before releasing: Dalamud Settings → Experimental → **Dev Plugin Locations**, add
the full path to `MarketLedger/bin/x64/Debug/MarketLedger.dll`, then enable it under Dev Tools →
Installed Dev Plugins.

## 4. Things to know

- **Not vetted.** Custom repositories aren't reviewed by goatcorp (Dalamud's maintainers). Dalamud
  shows a warning when you add one; players install at their own trust. Say plainly in the repo's
  README what the plugin does.
- **Square Enix's terms of service.** Any third-party tool is against them, and this one acts in
  the game: it moves your character, teleports, visits other worlds, talks to NPCs, buys on the
  market board and from vendors, and takes from and gives to retainers, by itself, and drives
  Artisan and GatherBuddy Reborn, which automate crafting and gathering. That's the kind of plugin
  Dalamud's official repository doesn't take (it rules out automating gameplay and the market
  board), so it lives in Koko's custom repository only. Say so where it's offered, and don't talk
  about it in the game.
- **It spends gil.** It never buys above a line's price or past the gil agreed when a run is sent;
  keep those checks (`Core/Market.cs`, tested) whenever buying changes.
- **After every game patch** the API level may go up and the game's code moves:
  - When Dalamud releases a new API (e.g. 16), update `Dalamud.NET.Sdk/15.0.0` in
    `MarketLedger.csproj` to the new SDK version, fix what the compiler flags, and release; the
    workflow fills `DalamudApiLevel` from the build. Until then Dalamud won't load the old plugin.
  - The plugin uses no byte patterns or hooks of its own: FFXIVClientStructs' structs and functions
    come with each Dalamud release. The windows' names and click values (`Automation/`) may change
    with a patch: a run then pauses saying which window didn't open or which click didn't work.
  - The helper plugins (Artisan, GatherBuddy Reborn, vnavmesh, Lifestream, AutoRetainer, Allagan
    Tools) update on their own: the Helpers tab shows when one stops answering.
  - During Dalamud's testing period for a patch, `stg/latest.zip` (see `DALAMUD_ZIP` in
    `release.yml`) has the next API, for building ahead of time.
