# Market Ledger (Dalamud plugin)

The in-game half of [Market Ledger](https://www.kokos-tools.com/market-ledger) on kokos-tools.com.
The site works out what's worth making for gil; this plugin makes it, by asking the plugins you
already use to do the work.

> **Status: new and untested in the game.** It compiles against Dalamud 15 (API 15) and its game-free
> code is unit-tested, but nobody has carried out a real run with it yet. Expect rough edges, and say
> what's wrong.

## What it does

- **Carries out a run sent from the site**, step by step: takes materials from your retainers at a
  summoning bell, buys from vendors and on the market board, has **GatherBuddy Reborn** gather and
  **Artisan** craft, and entrusts what was made to a retainer. **Lifestream** and **vnavmesh** get
  you about. One of them works at a time, and the plugin checks your bags as it goes.
- **Spends only what you agreed.** Nothing is bought above a line's price, past the gil you agreed
  when you sent the run, or past your own market ceiling; every purchase question is checked first.
- **Pauses on anything unexpected**: combat, a duty, a cutscene, a zone change it didn't ask for, a
  tell, bags filling up, a price that's moved. Resume starts the step again from what your bags hold.
- **Tells the site about you**: your crafting and gathering levels, gil and bag space, what you hold
  (bags, crystals, saddlebags, each retainer as you open them, or through Allagan Tools), your
  currencies (scrips, tomestones, seals, MGP, gemstones and the like), and what your
  retainers list and sell, so the site plans around it and keeps your books.

It drives your character and spends gil, and automation is against Square Enix's terms of service:
use it where you're happy to.

## What it needs

**Artisan**, **GatherBuddy Reborn**, **vnavmesh** and **Lifestream**. Optional: **AutoRetainer** (kept
out of the way while a run goes) and **Allagan Tools** (what your retainers hold without opening each).
The Helpers tab says what's found and how each answers.

## Installing it

1. In game, `/xlsettings` → **Experimental** → **Custom Plugin Repositories** → add
   `https://www.kokos-tools.com/dalamud/repo.json` (Koko's repository: already there if you use
   another of Koko's plugins) → tick it → **Save and Close**.
2. `/xlplugins` → **All Plugins** → **Market Ledger** → **Install**.
3. `/ledger` opens it.

## Using it

1. Link it: on the site, Settings → Market Ledger → The plugin makes a token; paste it in the plugin's
   Settings.
2. Plan a run on the site and send it. It shows on the plugin's Home: **Start**.
3. Watch it on the Run tab or the small window over the game, or on the site. `/ledger pause`,
   `/ledger resume` and `/ledger stop` work too.

## Building it

```sh
dotnet test MarketLedger.Tests/MarketLedger.Tests.csproj
dotnet build MarketLedger.sln -c Release   # needs Dalamud (XIVLauncher's dev folder, or DALAMUD_HOME)
```

How it all works is in [DETAILS.md](DETAILS.md). Licence: AGPL-3.0; the Geist fonts are under the OFL.
