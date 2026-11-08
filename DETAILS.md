# Market Ledger (Dalamud plugin): the details

How the plugin works, where things are, and why. The site's side (the planner that writes a run,
the database functions, the pages) is in kokos-tools' DETAILS.md, under Market Ledger; the shapes
both sides share are the site's `src/features/market-ledger/model/types.ts` and this plugin's
`MarketLedger/Core/RunFile.cs`.

## The idea

The site works out what's worth making for gil (prices, your levels, what you hold) and writes it
as a **run**: steps in order, each with the items it gets and how many must be in your bags once
it's done. The plugin carries a run out, but it does the work itself only where nobody else does:
it asks the plugins you already use. **Artisan** crafts, **GatherBuddy Reborn** gathers,
**Lifestream** travels and **vnavmesh** walks; the plugin's own clicks take things from your
retainers at a summoning bell, buy from vendors and on the market board, and entrust what was made
(Housing Studio's plugin's errand code, which recorded each of those clicks in the game). Only one
of them drives at a time. Every step's progress is read from your bags, so a step stopped halfway
starts again from what your bags hold, and the site sees the same numbers.

The other way round, the plugin tells the site who you are (your crafting and gathering jobs'
levels, gil, free slots), what you hold, and what your retainers list and sell.

## Things to know before using it

- **It drives your character and spends gil.** It moves you, teleports, visits other worlds, talks
  to NPCs, buys and takes from retainers. Nothing is bought above a line's price (`maxPrice` on the
  market, the vendor's price) or past the gil agreed on the site when the run was sent (`maxGil`),
  your own market ceiling, or your gil; every purchase question is checked against the price
  chosen before it's answered. It pauses on anything it didn't expect.
- **Automation is against Square Enix's terms of service**, like every third-party tool, and this
  one acts in the game. Use it where you're happy to; don't talk about it in the game.
- **Nothing of it has been tried in the game yet.** It compiles against Dalamud 15 and its Core is
  unit-tested; the clicks come from Housing Studio's plugin, where they were recorded working, but
  this plugin's runs are new. The clicks not yet seen working anywhere (opening a retainer's item
  menu, entrusting) hold the run until you press Resume, until you turn on **Trust the new clicks**.

## A run (`Core/RunFile.cs`)

The run file mirrors types.ts exactly, in camelCase JSON: `format` "market-ledger-run", `version`
1, `id` (the site's uuid), `name`, `createdAt`, `character`, `targets`, `maxGil`, `steps`,
`estimate`. Each step has an `id` and a `kind`:

| kind | lines | done by |
| --- | --- | --- |
| `withdraw` | item, amount, need, `retainer` (a retainer's name, or "Saddlebag") | the plugin, at a bell |
| `vendor` | item, amount, need, `price`, `npc`, `zone` | Lifestream, vnavmesh, the plugin |
| `market` | item, amount, need, `maxPrice`, `world` (null: any), `hq` | Lifestream, vnavmesh, the plugin |
| `gather` | item, amount, need, `job`, `zone`, `timed` (spawns, duration) | GatherBuddy Reborn |
| `craft` | one `line`, with `recipe`, `job`, `crafts` | Artisan |
| `entrust` | item, amount, need (what's **left** in your bags after) | the plugin, at a bell |

`RunFile.Parse` checks it all and says in words what's wrong (another format or version, a kind it
doesn't know, a recipe Artisan can't take (above 65,535), two steps with one id, a line with no item
or count). `RunProgress` is what goes back (`status`, `step`, `steps[{id, state, done, of, note}]`,
`spent`, `startedAt`, `updatedAt`, the newest 40 `log` lines newest first, `message`, and, when there's
something to say, `doing` (what it's doing now, in words) and `heldForYou` (true while it holds for you
in the game)); statuses and states are written in the site's lower-case words (`RunWords`).

**The counts** (`Core/Steps.cs`, `StepMath`, tested). A line's `need` is how many of the item must be
in your bags once its step is done (for an entrust line, how many are left after); `amount` is how
many the step itself gets. So done = what's in your bags past `need − amount` (for entrust, what's
gone below `need + amount`), and a step is done when every line's need is met. A craft's syntheses
left are what's missing over its yield (`amount ÷ crafts`), never more than the step planned. "In
your bags" counts HQ with NQ, as the site counts a need; a market line that wants HQ (`hq: true`) buys
HQ listings only, and one without `hq` takes either.

## The run (`Session/RunEngine*.cs`)

Steps on the framework thread, one `Tick` a frame, as Housing Studio's errand runner does.

- **Start** (from the window, the HUD, or the inbox) checks first that you're logged in and free, with
  the character the run was planned for (a run naming another of your characters' keys is refused: its
  bags, retainers and gil aren't this one's), and that the helpers the run needs are installed. Then the
  plugin **takes the run from the site** (`ml_plugin_run_update` with "accepted": the site gives a run
  sent to one plugin, once, and not one stopped there meanwhile); only when the site says yes is a run
  in hand stopped (the window asks you before; its last word still reaches the site) and this one
  begun. Something that came up during the asking (combat, say) leaves it in hand, paused.
  AutoRetainer is held (its "suppressed" state) when Settings → Hold AutoRetainer is on (and at a bell
  whatever the setting), and let be when the run ends.
- **After this run**: a run you marked so starts (taken from the site the same way) when the run in hand
  is done; not when it was stopped or failed.
- **Unloading the plugin** (or the game closing) stops whatever the step started and leaves the run
  paused ("The plugin was unloaded."); the site hears so (the plugin waits 2 s at most). The run's id is
  kept in the settings (`RunInHand`), and on the first inbox after loading the plugin picks it up again,
  paused, from the progress the site kept (steps through, skipped or failed stay so; gil spent still
  counts against `maxGil`); Resume carries on from your bags. When the site doesn't send the run back
  (its inbox's `active` entries carry `run` and `progress` once the site has them), the site is told it's
  stopped. Runs another install has in hand are never touched. While it holds a run, the plugin tells the
  site at least every 45 s whatever the run's status (see `ml_plugin_run_update` below).
- **Each step**: if your bags already have it, it's done at once. Otherwise:
  - *gather* and *craft* are handed to their plugin (below), and watched.
  - *withdraw*, *vendor*, *market* and *entrust* are lists of parts (getting to a bell, opening a
    retainer, buying one line…), each part small actions in a `StepQueue` (`Core/StepQueue.cs`, from
    Housing Studio): every wait has a timeout, every click is logged, at least 0.3 s between clicks.
    When the parts are through and a line is still short, the run pauses and says what's short.
- **Pause** (you, the site, `/ledger pause`, or anything unexpected) stops whatever the step started
  (walking, travel, auto-gather, Artisan's Endurance) and closes the windows it opened; **Resume**
  starts the step again from your bags. While the run **waits for you** (a click not yet seen working,
  a list to import into GatherBuddy Reborn, something to do by hand), Resume (the window's Continue,
  the HUD's, or `/ledger resume`, all in the game) is the go-ahead; the site's resume isn't (it's said
  in the progress): what's held is held for someone watching the game.
  **Skip** marks the step skipped and goes on. **Stop** ends the run and lets go of everything.
- **A craft that falls short** (Artisan stops with fewer made than needed, or makes none: the site
  leaves out materials nobody can get) marks its step failed with a note and the run goes on; the
  end says how many steps fell short.
- **Safety, every frame**: combat, a duty or a duty pop, a cutscene, logging out, being knocked out,
  another character logged in, a zone change no travel asked for (gathering may change zones:
  GatherBuddy Reborn travels) pause the run with the reason. Something going wrong in the plugin's own
  code fails the run (everything let go of first), rather than trying again every frame. A tell pauses it (Settings → Pause on a tell, on by default). Bags: no
  purchase or retrieve that would eat into the slots you keep free (Settings, 5 by default), and
  gathering pauses when your bags come down to them. Before each purchase: your gil, the run's gil
  left (`maxGil` less everything spent), your market ceiling (Settings, 0: none), the line's own
  gil (its amount at `maxPrice`, tax included, kept for the line across pauses and resumes); after it,
  what was really spent (a listing dearer than shown pauses the run). A purchase is counted at no less
  than its price even while the game hasn't taken the gil off yet. (The site's `maxGil` is the lines'
  amounts at `maxPrice` without the board's 5%, so a run buying everything at its very `maxPrice` runs
  out a little early: the safe side.)
- **The site's commands** (pause, resume, skip, stop), from `ml_plugin_run_update`'s answer (the
  site hands each over once), are obeyed when Settings → Let the site pause and stop is on; when it's
  off, the next progress's `message` says the command was left. A run the site refuses progress for as
  no longer its own (hint `run`: stopped or deleted there) is stopped here too.

### Gathering (`RunEngine.Helpers.cs`, `Ipc/GatherBuddy.cs`)

GatherBuddy Reborn's auto-gather gathers from its enabled **auto-gather lists**; a list's quantity is
how many to own in all (`AutoGather.Gather.cs`: it gathers while `GetTotalCount() < Quantity`), which
is what a run line's `need` is. What its IPC offers decides how the list gets there:

- **With list IPC** (the fork HenryBaylis/GatherBuddyReborn, branch `feature/autogather-list-ipc`, its
  IpcVersion 5; found by asking `GetAutoGatherLists`): the plugin makes a list for the run ("Market
  Ledger · <run>", `SetAutoGatherList` with item → need), turns your own enabled lists off meanwhile,
  turns the run's on (`SetAutoGatherListEnabled`; GatherBuddy's own perception and bait checks may
  refuse, and the run pauses saying so), then auto-gather on (`SetAutoGatherEnabled(true)`). When the
  run ends the list is deleted and your lists are turned back on.
- **Without** (upstream GatherBuddy Reborn today, IpcVersion 2: auto-gather on/off and its status, no
  lists): the step waits for you with the list as text to copy, in the TeamCraft format its Auto-Gather
  tab's **Import from TeamCraft** reads ("280x Ra'Kaznar Ore", one a line). You make a list, import it,
  leave only it on, and press Resume; the plugin turns auto-gather on. The Helpers tab says which way
  it's going. (Its `/gather` commands only mark a node and teleport; they don't gather a count, so
  they're not used.)

Then the plugin watches your bags: done when every line's need is met (auto-gather off). Auto-gather
waiting (`IsAutoGatherWaiting`: a timed node's window isn't open) shows as the run *waiting*, with
the window's Eorzea time when the line has one ("Waiting for Claro Walnut Log's window at 16:00 ET
(in 4 min)"). **The run waits inside the gather step** for a timed node's window: no other step goes
meanwhile (the site puts each timed node in a step of its own, so the wait is that node's). Auto-gather
waiting with no timed window still to open (GatherBuddy's "No available items to gather": it can't
gather them at your level, or it counts your retainers' items too, its **Check Retainer Inventories**
setting with Allagan Tools, while the run counts your bags only) pauses the run after 90 s, saying so;
so does auto-gather turning itself off for 5 s with lines still short (with GatherBuddy's own status),
or GatherBuddy not answering at all for 30 s. GatherBuddy's status lines go to the run's log as they
change. (GatherBuddy counts normal quality only; the run counts HQ too, so it may gather a little more.)

### Crafting (`RunEngine.Helpers.cs`, `Ipc/Artisan.cs`)

Auto-gather off first; Artisan idle first (`IsBusy`, waited for up to 3 minutes). Then
`Artisan.CraftItem(recipe, syntheses left)`, which selects the recipe and starts its Endurance mode
for that many. Watched through your bags; Artisan going idle for 4 s with the line short ends the step
as fallen short (above); Artisan busy for 10 minutes without one more made (stuck on a window) pauses
the run. Pausing or stopping uses `SetEnduranceStatus(false)` (the craft under way
finishes). `GetStopRequest` is read: true (Artisan entering a duty, or another plugin asked) pauses
the run. **Why not `SetStopRequest` to stop it**: in Artisan's source (`IPC.cs`, `StopCraftingRequest`)
setting it true turns Endurance off, the same as `SetEnduranceStatus(false)`, but the flag stays set,
and setting it back to false *resumes* crafting (`ResumeCrafting`), and Artisan clears it by itself
after a duty when you have "resume after duty" on: a stop request left behind could start the
run's recipe again later. So the plugin stops Endurance directly.

### Retainers (`RunEngine.Retainers.cs`)

A summoning bell within 60 yalms, else Limsa Lominsa's (its aetheryte, Hawkers' Alley), walked to.
AutoRetainer held (and, if it's mid-cycle, waited for up to 15 s). The bell, the retainer by name in
its list (`RetainerList` [i:2, u:row]), their greeting clicked through, **Entrust or withdraw
items**, then for each line each stack's item menu and **Retrieve from Retainer** (the whole stack,
never "Retrieve Quantity"), until the line's need is met or the retainer has none left. Withdraw lines
naming the saddlebags ("Saddlebag", the place name the plugin reports) wait for you to take them
out by hand (their window isn't one the plugin drives). Entrust goes to the first retainer in the
bell's list: each whole stack whose size doesn't take the bags below the line's need, from your bag
window's item menu, **Entrust to Retainer**; a stack bigger than what should go is left for you.
Opening a retainer's item menu and entrusting are guesses (held for you until trusted); done by hand
meanwhile, the run notices (the count moved) and carries on. A retainer's crystals are looked for too
(its crystal container); should the game ask how many to take (crystals may), the run pauses for you to
take them by hand. Crystals go to the crystal bag, so they're counted there and need no free bag slot.

### Vendors and the market (`RunEngine.Buying.cs`, `Game/Vendors.cs`)

**Vendors**: the game's gil shops and who sells what are read once in the background when the plugin
loads (`Vendors`, from Housing Studio's VendorData: GilShopItem, each NPC's ENpcData through
TopicSelect, PreHandler and CustomTalk, their Level spots, and the residential wards' layout files for
the housing districts' vendors; items behind a quest or achievement left out). A vendor line goes to
the NPC and zone the site named when one matches, else the cheapest NPC somewhere known outside the
residential districts, then in them, whose aetheryte you've attuned to. Lifestream teleports there
(or, for a residential district, its city's Residential District Aethernet to ward 1), vnavmesh walks
to them (the path ending within reach, `PathfindAndMoveCloseTo`; one behind a counter, whose own spot the
floor doesn't reach, is talked to from within 4.5 yalms, after a walk to the nearest floor that reaches
it, `NearestPointReachable`; a walk not moving for 12 s is asked again), the plugin talks to them, picks the shop in their menu by name, and buys in lots of up to 99
(`Shop` [i:0, row, count, empty]), the yes/no answered only when it names the price and the item.

**The market**: lines grouped by world (yours, and "any", first); for another world, Lifestream's
world visit from Limsa's aetheryte, and back to your home world after. Only a world Lifestream says is
on your data centre (`CanVisitSameDC`) is visited: its `ChangeWorld` would cross data centres too. Limsa's boards, the board
searched by the item's name (`ItemSearch` [i:9, …, s:name, s:name, …]), its listings opened and
checked to be that item's, read as the game receives them (Dalamud's market board service,
`Game/BoardListings.cs`), and bought cheapest first while `MarketRule.WhyNot` allows (`Core/Market.cs`,
tested), each purchase's question checked to name the listing's cost (`BoardQuestionMatches`). A
listing gone before the question came is passed for the next, three times at most.

## Helpers (`Ipc/*.cs`, `LedgerSession.CheckHelpers`)

One wrapper per plugin; every call goes through `IpcGuard` (`Core/IpcCheck.cs`, tested): not
installed, another version's signature or a throw mean "can't", and each call's first failure is
logged once (at debug). The Helpers tab shows each: installed (Dalamud's installed plugins, by
internal name), its version, and how its IPC answered (`HelperIpc`: No, Partly, Ok, from a probe of
its read-only calls); checked when the plugin loads, every 30 s while no run goes, and on "Check again".

The IPC names, verified in each plugin's source (clones of 3–4 Nov 2026):

| plugin (internal name) | calls used | where in its source |
| --- | --- | --- |
| Artisan (`Artisan`) | `Artisan.CraftItem` (ushort recipe, int amount), `Artisan.IsBusy` → bool, `Artisan.IsListRunning` → bool, `Artisan.IsListPaused` → bool, `Artisan.SetListPause` (bool), `Artisan.GetEnduranceStatus` → bool, `Artisan.SetEnduranceStatus` (bool), `Artisan.GetStopRequest` → bool | `Artisan/IPC/IPC.cs`, `IPC.Init`, lines 41–52 (commit a6118e6). `SetStopRequest` (line 49) exists and isn't used (above). |
| GatherBuddy Reborn (`GatherBuddyReborn`) | `GatherBuddyReborn.Version` → int, `.IsAutoGatherEnabled` → bool, `.SetAutoGatherEnabled` (bool), `.IsAutoGatherWaiting` → bool, `.GetAutoGatherStatusText` → string | `GatherBuddy/Plugin/GatherBuddyIpc.cs` (published by its EzIPC, `Plugin/EzIpc.cs`, prefix its InternalName; upstream main, commit a204f2f) |
| … with list IPC | `.GetAutoGatherLists` → Dictionary&lt;string, bool&gt;, `.SetAutoGatherList` (string, Dictionary&lt;uint, uint&gt;, string?) → int, `.SetAutoGatherListEnabled` (string, bool) → bool, `.DeleteAutoGatherList` (string) → bool | the same file on HenryBaylis/GatherBuddyReborn `feature/autogather-list-ipc` (commit 240b220), and `AutoGather/Lists/AutoGatherListsManager.External.cs`. (Upstream's EzIPC publishes functions of at most one argument; the fork's, up to four, which these need.) |
| vnavmesh (`vnavmesh`) | `vnavmesh.Nav.IsReady`, `.SimpleMove.PathfindAndMoveTo` (Vector3, bool) → bool, `.SimpleMove.PathfindAndMoveCloseTo` (Vector3, bool, float range) → bool (an older vnavmesh without it: walked to the spot), `.SimpleMove.PathfindInProgress`, `.Query.Mesh.NearestPointReachable` (Vector3, float, float) → Vector3?, `.Path.IsRunning`, `.Path.Stop`, `.Nav.PathfindCancelAll` (only while a path is being worked out: it reloads the mesh) | `vnavmesh/IPCProvider.cs`, lines 17, 25, 37, 42–43, 53–55 (commit 6fc8072; master, November 2026) |
| Lifestream (`Lifestream`) | `Lifestream.Teleport` (uint, byte) → bool, `.AethernetTeleport` (string) → bool, `.ChangeWorld` (string) → bool, `.CanVisitSameDC` (string) → bool, `.GoToHousingAddress` (AddressBookEntryTuple), `.IsBusy` → bool, `.Abort`, `.ExecuteCommand` (string; the "/li" fallback, only when the call itself isn't there: an answer of false is a no) | `Lifestream/IPC/IPCProvider.cs`, lines 35, 59, 65, 71, 78, 102, 139, 241 (commit b363201); the tuple in `Lifestream/Data/AddressBookEntry.cs` |
| AutoRetainer (`AutoRetainer`) | `AutoRetainer.GetSuppressed` → bool, `.SetSuppressed` (bool), `.GetOfflineCharacterData` (ulong) → its OfflineCharacterData (subscribed as object: Dalamud hands it over as JSON; `RetainerData`, `Name`, `RetainerID`, fields of AutoRetainerAPI's `Configuration/OfflineCharacterData.cs` and `OfflineRetainerData.cs`), `AutoRetainer.PluginState.IsBusy` → bool | `AutoRetainer/Modules/IPC.cs` lines 22–23, 27; `Modules/EzIPCManagers/IPC_PluginState.cs` line 15 (commit cc020df). It has no IPC to entrust or withdraw. Housing Studio's plugin asks "AutoRetainer.IsBusy", which isn't published. |
| Allagan Tools (`InventoryTools`) | `AllaganTools.IsInitialized` → bool, `.GetCharactersOwnedByActive` (bool) → HashSet&lt;ulong&gt;, `.GetCharacterItems` (ulong) → HashSet&lt;ulong[]&gt; (each CriticalCommonLib's `InventoryItem.ToNumeric()`: container, slot, item, quantity…) | `InventoryTools/IPC/IPCService.cs`, lines 571–593 (commit 70a9f41); `ToNumeric` in CriticalCommonLib `Models/InventoryItem.cs` line 832 |

## The site (`Core/SiteRpc.cs`, `Core/SiteLink.cs`, `Session/SiteSync.cs`)

Linked with a token from the site (Settings → Market Ledger → The plugin): "mlp_" and 43 letters,
kept in the plugin's settings, never logged, sent only in the body of a POST to
`https://upjcxjuuzxujqddtwehn.supabase.co/rest/v1/rpc/<function>` with the site's publishable key (as
Raid Planner's plugin does). Only `ml_plugin_[a-z_]+` functions are ever called. A refusal is shown in
the site's own words when its hint is one written for players (`token`, `off`, `rate`, `size`, `run`,
`data`); `token` turns the link to Refused until you link again, `off` too but it's tried again every
10 minutes (and on linking again, even with the same token); `rate` holds that kind of call for 2
minutes; `run` on the run in hand's update stops sending its progress and stops the run here (it was
stopped or deleted on the site); sales or listings refused as `data` or `size` aren't sent again. Calls run off the framework
thread, one of each kind at a time, waiting longer after each failure (5 s doubling to 5 minutes), and
paced under the site's hourly limits for a person (hello 240, holdings 240, inbox 1,800, run_update
3,600 and 100 more for finishes, sales 240, listings 480): at most 180, 30, 600, 1,800, 180 and 450 an
hour (and a few more run updates besides: taking a run, and the last word on unloading). The site's
newer optional arguments (`p_known`, `p_sent_at`, `p_key`) are sent; a site that answers 404 for them
(an older one) has that function called without them from then on (`Core/RunFile.cs`, `SiteArgs`).

| function | when | what |
| --- | --- | --- |
| `ml_plugin_hello` (p_character) | logging in, another character, every 10 minutes | `{ key, name, world, dc, jobs, gil, freeSlots }`; its `account` is shown as linked |
| `ml_plugin_holdings` (p_key, p_items, p_places) | when what you hold changes, at most every 2 minutes | totals, and each place `{ place, kind, items }`: Bags, Crystals, Currencies (kind `currency`: your currencies' balances, gil left out), Saddlebag (as last seen), each retainer (as last seen open, else Allagan Tools' count; 12 at most, the ones seen most lately). The totals (`p_items`) are the materials only: the currencies are never added to them. A currency changing (a scrip turn-in, say) is a change like any other (looked for every 10 s, sent at most every 2 minutes) |
| `ml_plugin_inbox` (p_known) | every 30 s; every 6 s while a run is in hand | `p_known`: the ids of the runs whose files the plugin has (in hand, and waiting), whose `run` and `progress` the site then leaves out (the files kept from before are used). The runs sent (read and checked: one that can't be read is logged and left out; one this plugin has had in hand isn't offered again; each one's `sent_at` kept as the site wrote it); a pending command it shows for the run in hand makes the next run update go at once; the first after loading, its `active` runs (`ActiveRun`: id, name, status, command, run, progress, `taken_key`), to pick up the one this install had in hand (carried on only by the character in `taken_key`) |
| `ml_plugin_run_update` (p_run, p_status, p_progress, p_sent_at, p_key) | taking a run (it sends "accepted", the claim, before it starts, with `p_sent_at`, the inbox's `sent_at` for the sending it has, so a run sent again since is refused); each change (at most every 2 s); every 15 s while running and at least every 45 s in every other status while a run is held (the site takes a plugin silent for 2 minutes to be gone: its Stop then finishes the run, and another plugin may take it over); each run's last word (done, stopped, failed; paused on unloading) | `p_key` on every call: the character carrying the run out (the site holds a run for one plugin and character; another's word on it is refused with `run`, and the run then stops here). `RunProgress`, the status never `sent` or `draft`; its answer's `command` (handed over once there) is obeyed. Last words are queued by run (`SiteSync.QueueFinal`) and sent before the run in hand's progress, again after a failure, until the site takes each (or refuses it as `run`, `data` or `size`): starting another run never loses the one put aside. On unloading what's queued goes at once, waited for 2 s at most |
| `ml_plugin_sales` (p_key, p_sales) | a retainer's sale history window read with new sales | `[{ retainer, item, hq, quantity, price, at }]`, at most 500 a call, each sale once (by its key) |
| `ml_plugin_listings` (p_key, p_retainer, p_items) | a retainer's sell list read and changed | `[{ item, hq, quantity, price }]` |

**The character's key** is the SHA-256 of "market-ledger:", the content id and this install's own
random salt (made once, kept in the settings), as 32 hex letters: the site never sees a content id.
**Jobs** are the eight crafters' and three gatherers' levels (`IPlayerState.GetClassJobLevel`), a job
not unlocked left out.

**What retainers hold** (`Game/HoldingsReader.cs`): whenever a retainer's inventory window is open its
seven pages and its crystals are counted (HQ and collectable copies as the item), and the saddlebags whenever the game
has them; kept per character in `holdings.json` in the plugin's config folder, so they're still known
after a restart. Allagan Tools' counts fill in retainers not seen (`Holdings.FromAllagan`: each item
is CriticalCommonLib's `InventoryItem.ToNumeric()`, [0] container, [1] slot, [2] item, [3] quantity;
only the bag pages and crystals count, not what's on the market, worn or gil), named from the game's
retainer list or AutoRetainer's (a count it can't name, a company chest say, is left out). AutoRetainer's
character record reaches the plugin as JSON (Dalamud converts a type the plugin can't name through
Newtonsoft), read by `Holdings.RetainerNamesFromJson`, asked at most every 5 minutes. The retainer's
windows (sell list, sale history) are read once a second; what's waiting to be sent for one character
is dropped when another logs in.

**Your currencies** (`Game/HoldingsReader.cs`, `ReadCurrencies`; the place is built by `Currencies.Place`
in `Core/Ledger.cs`): read with your bags (every 10 s), sent as the place `{ place: "Currencies", kind:
"currency", items: { "<currency item id>": amount } }`, never in the totals (`Holdings.Total` skips the
kind). Read from (FFXIVClientStructs, `FFXIVClientStructs/FFXIV/Client/Game/`):

- the **Currency container**, `InventoryManager.GetInventoryContainer(InventoryType.Currency)`
  (`InventoryType.cs:11`, 2000; `InventoryManager.cs:58`), every slot (`GetBaseItemId`, `GetQuantity`):
  gil, the Grand Company seals, Wolf Marks, Allied Seals, MGP and the Allagan tomestones are kept there
  (FFXIVClientStructs doesn't list its slots, so it's read whole). Until the game has sent it (just
  after logging in) no currency place is sent at all.
- `InventoryManager`'s own counters (`InventoryManager.cs`): `GetCompanySeals(1–3)` (:119; 1 the
  Maelstrom's Storm Seals, item 20; 2 the Twin Adder's Serpent Seals, 21; 3 the Immortal Flames' Flame
  Seals, 22: `Currencies.SealsOf`), `GetGoldSaucerCoin` (:110, MGP, 29), `GetWolfMarks` (:113, 25),
  `GetAlliedSeals` (:116, 27), and `GetTomestoneCount(item)` (:125) for every tomestone in the game's
  `TomestonesItem` sheet (Lumina, looked up once).
- **`CurrencyManager`** (`CurrencyManager.cs`, `Instance()` :8): every item in its `SpecialItemBucket`
  (:26: the White, Purple and Orange Crafters' and Gatherers' Scrips, Centurio Seals) and its
  `ItemBucket` (:72: Sacks of Nuts, Bicolor Gemstones, Skybuilders' Scrips, Trophy Crystals, the allied
  societies' currencies, ventures, MGF…), each by `GetItemCount(item)` (:205), so a currency the game
  adds later is sent without a change here. Its `ContentItemBucket` (:196: Island Sanctuary's
  materials, Bozjan Clusters), held only while you're there, isn't read.
- and `Currencies.Named` one by one (Centurio Seals 10307, Sacks of Nuts 26533, Bicolor Gemstones
  26807, Trophy Crystals 36656): `CurrencyManager.GetItemCount` when it `HasItem` (:220), else
  `InventoryManager.GetInventoryItemCount` (:64).

A currency read in two of these is counted once (the larger: it's the same balance); gil (item 1, said in
`ml_plugin_hello`), id 0 and nothing held are left out; each amount is at most 100,000,000, and 200
currencies at most (the lowest ids), as the site keeps them. Should reading them throw, only the
currencies are left out (said once in the log): the bags, saddlebags and retainers are still sent.
**Not checked in the game yet**: which
items the Currency container and the buckets hold is as FFXIVClientStructs' notes and other plugins
(GatherBuddy Reborn's vendor currencies) say.

**Listings** (`Game/RetainerMarket.cs`): while the retainer's sell list (`RetainerSellList`) is open,
the game's `RetainerMarket` container and each slot's price (`InventoryManager.GetRetainerMarketPrice`),
as Allagan Market reads them. **Sales**: while the retainer's sale history (`RetainerHistory`) is open.
Neither FFXIVClientStructs nor Dalamud describe that window, so its rows are found in its values by
their shape (`Core/Ledger.cs`, `SaleHistory`, tested on a made-up layout): an item's name (with the
game's HQ mark), then a price, a quantity and a time in seconds since 1970. **Not checked in the game
yet**: when the window's open and nothing reads, its values go to the log once (`/xllog`), to fix the
reader from.

## The windows (`UI/`)

Written by the UI side against `Session/LedgerSession.cs`: `Link` (Unlinked, Linking,
Linked(account), Refused(why)), `Inbox` (the runs sent, oldest first, "later" ones last), `Current`
(the run in hand as a `RunState`: file, status, step, each step's progress with its label, kind and
who does it, gil spent, the log, the message, what's being done now, about how long is left, whether
it's held for you, text to copy), `Helpers`, `Character`, `Notice`, and `Start`, `Pause`, `Resume`,
`Stop`, `SkipStep`, `Later`, `CheckHelpers`, `Refresh` (reads the token again). Both windows read it
each frame on the framework thread (where `Tick` runs too); what you press is queued and run after the
frame's drawing.

Both are drawn with the house kit of Koko's plugins (`UI/Kit`, from Raid Planner's plugin with Glamour
Studio's layout parts) in Market Ledger's gold. The kit stays in the namespace `MarketLedger.UI`: a
namespace `MarketLedger.UI.Kit` would make every `Kit.Button(...)` resolve to it and fail to compile.

**The main window** (`MainWindow`; `/ledger`, Dalamud's open button opens Home, its cog Settings):

- **Home**: the link (a gold box with Paste a token and Get a token while unlinked or refused), who
  you're playing (name, world, gil, free slots), the run in hand (step, name, what it's doing, a bar
  for the whole run, steps / elapsed / left / spent of the agreed gil, its message, and Continue when
  it's held for you, Pause or Resume, Skip step, Stop, Open on kokos-tools.com), why the last Start
  didn't (`Notice`), and the runs sent from the site, each with Start now and, while a run is in hand,
  After this run. A run that was stopped or failed (by you, by the site, or deleted there) shows its
  reason in a red box, here and in Run's Now.
- **Run**: the steps as a table (the one under way in gold, a bar with its count or its note such as
  "in 4 min"), the log (newest first, each helper's name in its tint; only the rows in view are
  drawn), and Now, the numbers and the switches that matter while it runs. Under 700px wide, the
  right column goes under the left.
- **Helpers**: each plugin, needed or optional, what it's asked (and its IPC calls), found with its
  version and whether its IPC answers, its note, Get it when missing; Check again.
- **Settings**: the account (the token typed in a password field, checked to be `mlp_` and 47
  letters before it's kept; once linked only `mlp_••••` and its last four are shown; Unlink asks
  first), while running (the steppers keep free slots at 0–60 and the market ceiling to its steps)
  and the HUD.

Skip, Stop, Unlink and Start now over a run in hand ask first (a card over the window that takes
every click). The answer acts on what it asked about: a Skip answered after the step moved on skips
nothing, a Stop answered after another run started stops nothing.

**The HUD** (`HudWindow`): a small box with no title bar, sized to what it holds, top right the
first time and then where you leave it (ImGui keeps the place). It shows while a run is in hand, a
new one waits, or for 30 seconds after a run ends (Done, or Stopped / Failed with why; × puts it
away), when Settings → Show the HUD allows it; Dalamud hides it, as every plugin window, in
cutscenes, in gpose and with the game's UI hidden. It holds the step, a strip with a segment per
step, the step's count and what it's doing, step n / of and the time, Pause or Resume, and the way
to the main window (Run). On the market it shows the gil spent of what was agreed. Continue when
the run is held for you, Copy the list when there's text to paste. When a run arrives from the site
it asks "A new run from kokos-tools.com" with Start now and After this run (or Not now when nothing
is in hand); Start now over a run in hand asks there first ("… is stopped where it is (the site is
told), and this one starts") with Keep going and Stop it and start.

Drawing is kept cheap: strings for numbers that stand still are kept (`Memo`), ImGui ids with
numbers in them are made once (`Ids`), wrapped lines and text cut with "…" are kept per text, width,
face and size (`Lines`, `Txt.Fit`) but only once the fonts are built, and thrown away when Dalamud
rebuilds them (`Fonts.Ready`, `Fonts.Generation`). The fonts are made by the first window and let go
with the last (`Ui.AcquireFonts` / `ReleaseFonts`). The icons' Lucide paths are in
`UI/Model/IconPaths.cs`, game-free, and the tests read every one (a path the reader can't take
would otherwise throw the first time its icon is drawn).

## Settings (`Configuration.cs`)

Token; Pause on a tell (on); Keep free bag slots (5); Market ceiling (0: only the run's gil); Trust
the new clicks (off); Hold AutoRetainer (on); Show the HUD (on); Let the site pause and stop (on);
Say pauses and stops in chat (on). The character key's salt. The site's id of the run in hand
(`RunInHand`, empty when none), to pick it up again after a reload. No migration: Version 1, fields
only added (a missing one is its default).

## Commands

`/ledger` (or `/marketledger`) opens the window; `/ledger stop`, `/ledger pause`, `/ledger resume`;
`/ledger settings`.

## Layout

- `Core/`: game-free, unit-tested (`MarketLedger.Tests` compiles these files directly, without
  Dalamud): the run file and progress, the counts, the market rule, pacing, the site's calls and
  token, holdings (currencies too) and sales, the IPC guard, Eorzea time, the step queue and window calls.
- `Game/`: reading and doing in the game (bags, retainers, vendors, the board's listings, the
  player, movement).
- `Automation/`: the windows and clicks, with where each came from (`ErrandTable`, `UiTable`,
  `GameUi`).
- `Ipc/`: one wrapper per helper plugin.
- `Session/`: the run (`RunEngine*`), the site (`SiteSync`), and the façade the windows read
  (`LedgerSession`, `LedgerTypes`).
- `UI/`: the windows (`MainWindow` with `Views/`, `HudWindow`) and the house UI kit (`Kit/`, Geist
  fonts embedded); `UI/Model/` is its game-free part (the icons' paths and their reader, dashed
  edges), compiled into the tests too.

## Developing

```sh
dotnet test MarketLedger.Tests/MarketLedger.Tests.csproj   # anywhere .NET 10 is
dotnet build MarketLedger.sln -c Release                   # needs Dalamud (DALAMUD_HOME, or XIVLauncher's dev folder)
# → MarketLedger/bin/x64/Release/MarketLedger/latest.zip
```

CI (`.github/workflows/build.yml`) downloads the current Dalamud, runs the tests and builds on every
push; `release.yml` releases a tag (see `docs/PUBLISHING.md`). `repo.json` is `[]` until the first
release, which makes its entry.

## Not yet

- Repricing your listings stays a suggestion on the site.
- A run picked up again after the plugin reloads needs the site's inbox to send its `run` and
  `progress` in `active` (the hardening migration does); a site that doesn't has such a run reported
  stopped.
- What a market line had spent before a reload isn't known line by line: the run's `maxGil` (with
  all it had spent) still holds, each line's own budget starts again.
- Words are matched in English only (menus, objects' names, the sale history's item names).
- The sale history reader and the entrust clicks are unconfirmed in the game.

## Release notes

### 0.1.2

Vendors behind a counter: vnavmesh walks up to them from the front (its path ends within reach), and one
it can't get right up to is talked to from within 4.5 yalms, after a walk to the nearest floor that
reaches them; the run used to walk at them twice and stop. A summoning bell or a market board behind a
rail likewise. A walk that hasn't moved for 12 s is stopped and asked again. The same walk as Housing
Studio's plugin 1.0.4.

### 0.1.0

The first version: runs from the site carried out step by step with Artisan, GatherBuddy Reborn,
Lifestream and vnavmesh; retainers, vendors and the market board by the plugin's own clicks; your
character, holdings (with your currencies as a place of their own), listings and sales sent to the site.
