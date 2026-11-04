using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MarketLedger.Core;
using MarketLedger.Game;
using MarketLedger.Ipc;

namespace MarketLedger.Session;

/// <summary>
/// The plugin and the site, kept in step in the background, through the site's functions for the
/// plugin (<c>ml_plugin_…</c>, <see cref="SiteRpc"/>):
/// <list type="bullet">
/// <item><c>ml_plugin_hello</c>: who you're playing (its key, name, world, data centre, the crafting and
/// gathering jobs' levels, gil, free bag slots), when you log in and every 10 minutes; its answer says
/// whose account the token is.</item>
/// <item><c>ml_plugin_holdings</c>: what you hold, place by place, when it changes, at most every 2 minutes.</item>
/// <item><c>ml_plugin_inbox</c>: the runs sent from the site (every 30 s; every 5 s while a run is in hand),
/// and a pending command for the run in hand.</item>
/// <item><c>ml_plugin_run_update</c>: the run's status and progress on each change (at most every 2 s) and
/// every 15 s while it runs; its answer may carry a command from the site (pause, resume, skip, stop).</item>
/// <item><c>ml_plugin_sales</c> and <c>ml_plugin_listings</c>: a retainer's sales and listings, read while
/// their windows are open, each sent once.</item>
/// </list>
/// Calls run off the framework thread; their answers come back to it (<see cref="Tick"/>). Nothing is
/// sent before the plugin's linked. The token is never logged.
/// </summary>
public sealed class SiteSync : IDisposable
{
    private static readonly TimeSpan HelloEvery = TimeSpan.FromMinutes(10);

    private readonly Configuration config;
    private readonly RunEngine engine;
    private readonly HoldingsReader holdings;
    private readonly AllaganTools allagan;
    private readonly SiteSession site;
    private readonly ConcurrentQueue<Action> answers = new();

    // Each kind of call paced well under the site's hourly limits for a person (hello 240, holdings 240,
    // inbox 720, run_update 3,600, sales 240, listings 480): at most 180, 30, 600, 1,800, 240 and 450.
    private readonly Pace hello = new(TimeSpan.FromSeconds(20), HelloEvery);
    private readonly Pace holdingsPace = new(TimeSpan.FromMinutes(2));
    private readonly Pace inbox = new(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(30));
    private readonly Pace update = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15));
    private readonly Pace salesPace = new(TimeSpan.FromSeconds(15));
    private readonly Pace listingsPace = new(TimeSpan.FromSeconds(8));

    /// <summary>How long a kind of call waits after the site says "too often".</summary>
    private static readonly TimeSpan RateWait = TimeSpan.FromMinutes(2);

    /// <summary>Goes up with each link: an answer for an earlier one is let go.</summary>
    private int generation;

    private string? loggedInAs;
    private string holdingsSent = string.Empty;
    private int holdingsVersion = -1;
    private DateTime holdingsLookedAt;
    private readonly HashSet<string> salesSent = new(StringComparer.Ordinal);
    private readonly List<SaleRow> salesWaiting = new();
    private readonly Dictionary<string, string> listingsSent = new(StringComparer.OrdinalIgnoreCase);
    private (string Retainer, List<ListingRow> Rows, string Print)? listingsWaiting;
    private RunStatus? lastSentStatus;
    private Guid? updatingRun;

    /// <summary>A run the site refused progress for (its <c>run</c> hint): not sent again.</summary>
    private Guid? updatesRefused;

    public SiteSync(Configuration config, RunEngine engine, HoldingsReader holdings, AllaganTools allagan)
    {
        this.config = config;
        this.engine = engine;
        this.holdings = holdings;
        this.allagan = allagan;
        site = new SiteSession(() => config.Token);
        engine.Changed += () => update.Changed();
        Relinked();
    }

    public LinkState Link { get; private set; } = new LinkState.Unlinked();

    /// <summary>The runs sent from the site and not started, as the inbox last said (oldest first).</summary>
    public IReadOnlyList<(Guid Id, string Name, DateTime SentAt, RunFile File)> Waiting { get; private set; } = Array.Empty<(Guid, string, DateTime, RunFile)>();

    /// <summary>Goes up whenever <see cref="Link"/> or <see cref="Waiting"/> changes.</summary>
    public int Version { get; private set; }

    /// <summary>The character you're playing, as last read (and said to the site).</summary>
    public CharacterSnapshot? Character { get; private set; }

    /// <summary>The token was changed (linked, unlinked): start again.</summary>
    public void Relinked()
    {
        generation++;
        hello.Reset();
        holdingsPace.Reset();
        inbox.Reset();
        update.Reset();
        salesPace.Reset();
        listingsPace.Reset();
        holdingsSent = string.Empty;
        lastSentStatus = null;
        Waiting = Array.Empty<(Guid, string, DateTime, RunFile)>();
        Link = site.Linked ? new LinkState.Linking() : (LinkState)new LinkState.Unlinked();
        if (site.Linked)
        {
            hello.Changed();
            inbox.Changed();
        }
        Version++;
    }

    public void Tick()
    {
        while (answers.TryDequeue(out var answer))
        {
            try
            {
                answer();
            }
            catch (Exception ex)
            {
                Services.Log.Warning(ex, "Market Ledger couldn't read the site's answer");
            }
        }
        var now = DateTime.UtcNow;
        ReadCharacter(now);
        holdings.Tick();
        ReadRetainerWindows(now);
        if (!site.Linked || Link is LinkState.Refused) return;

        // A run in hand: the inbox is asked more often (the site's buttons answer sooner).
        inbox.Every = engine.Active ? TimeSpan.FromSeconds(6) : TimeSpan.FromSeconds(30);
        update.Every = engine.Active && engine.Status == RunStatus.Running ? TimeSpan.FromSeconds(15) : null;

        if (Character is { } c && hello.Due(now)) SendHello(c);
        if (Link is not LinkState.Linked) return;
        if (Character != null && holdingsPace.Due(now)) SendHoldings();
        if (inbox.Due(now)) AskInbox();
        // The run's status as the site knows it ("sent" and "draft" are the site's own, never said).
        if (engine.File is { } run && engine.Status is not (RunStatus.Draft or RunStatus.Sent) && updatesRefused != run.Guid
            && (engine.Active || lastSentStatus != engine.Status) && update.Due(now)) SendUpdate(run);
        if (Character is { } who && salesWaiting.Count > 0 && salesPace.Due(now)) SendSales(who.Key);
        if (Character is { } me && listingsWaiting != null && listingsPace.Due(now)) SendListings(me.Key);
    }

    // ---------------------------------------------------------------------------------------------
    // What's read in the game.

    private DateTime characterReadAt;

    private void ReadCharacter(DateTime now)
    {
        if (now - characterReadAt < TimeSpan.FromSeconds(2)) return;
        characterReadAt = now;
        if (config.Salt.Length == 0)
        {
            config.Salt = SiteLink.NewSalt();
            config.Save();
        }
        var c = PlayerReader.Read(config.Salt);
        var before = Character;
        Character = c;
        if (c == null)
        {
            loggedInAs = null;
            return;
        }
        // Logged in (or another character): the site hears at once.
        if (loggedInAs != c.Key)
        {
            loggedInAs = c.Key;
            hello.Reset();
            hello.Changed();
            holdingsPace.Reset();
            holdingsSent = string.Empty;
        }
        else if (before != null && (before.Gil != c.Gil || before.FreeSlots != c.FreeSlots)) Version++;
        // What's held: looked at every 10 s, sent when it's changed (at most every 2 min).
        if (holdings.Version != holdingsVersion || now - holdingsLookedAt > TimeSpan.FromSeconds(10))
        {
            holdingsVersion = holdings.Version;
            holdingsLookedAt = now;
            pendingPlaces = holdings.Places(allagan, engine.AutoRetainer);
            if (Holdings.Fingerprint(pendingPlaces) != holdingsSent) holdingsPace.Changed();
        }
    }

    private List<Place> pendingPlaces = new();

    private void ReadRetainerWindows(DateTime now)
    {
        if (RetainerMarket.Listings() is { } l)
        {
            var print = ListingRow.Fingerprint(l.Retainer, l.Rows);
            if (!listingsSent.TryGetValue(l.Retainer, out var sent) || sent != print)
            {
                if (listingsWaiting?.Print != print)
                {
                    listingsWaiting = (l.Retainer, l.Rows, print);
                    listingsPace.Changed();
                }
            }
        }
        if (RetainerMarket.Sales() is { } s)
            foreach (var row in s.Rows)
                if (!salesSent.Contains(row.Key) && salesWaiting.All(w => w.Key != row.Key))
                {
                    salesWaiting.Add(row);
                    salesPace.Changed();
                }
    }

    // ---------------------------------------------------------------------------------------------
    // Calls.

    /// <summary>Calls a function on a worker; <paramref name="then"/> runs on the framework thread with the answer (unless the link changed meanwhile).</summary>
    private void Call(string function, JsonObject args, Pace pace, Action<RpcResult> then)
    {
        var gen = generation;
        site.Call(function, args).ContinueWith(t =>
        {
            var r = t.Status == TaskStatus.RanToCompletion ? t.Result : new RpcResult(false, null, "The call failed.", Unreachable: true);
            answers.Enqueue(() =>
            {
                if (gen != generation) return;
                if (r.Hint == "rate") pace.RateLimited(DateTime.UtcNow, RateWait);
                else pace.Done(r.Ok, DateTime.UtcNow);
                if (!r.Ok)
                {
                    Services.Log.Debug($"Market Ledger: {function} didn't go: {r.Why}");
                    if (r.TokenRefused || (r.Hint == "off"))
                    {
                        Link = new LinkState.Refused(r.Why ?? "The site turned the link down.");
                        Version++;
                    }
                }
                then(r);
            });
        }, TaskScheduler.Default);
    }

    private static JsonNode? Body(RpcResult r)
    {
        if (!r.Ok || string.IsNullOrWhiteSpace(r.Body)) return null;
        try
        {
            return JsonNode.Parse(r.Body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void SendHello(CharacterSnapshot c)
    {
        var character = new JsonObject
        {
            ["key"] = c.Key,
            ["name"] = c.Name,
            ["world"] = c.World,
            ["dc"] = c.DataCentre,
            ["jobs"] = Jobs.ToJson(c.Jobs),
            ["gil"] = c.Gil,
            ["freeSlots"] = c.FreeSlots,
        };
        Call("ml_plugin_hello", new JsonObject { ["p_character"] = character }, hello, r =>
        {
            if (!r.Ok) return;
            var account = Body(r) is JsonObject o ? JsonRead.Str(o, "account") : null;
            Link = new LinkState.Linked(string.IsNullOrWhiteSpace(account) ? "your account" : account);
            Version++;
            inbox.Changed();
            holdingsPace.Changed();
        });
    }

    private void SendHoldings()
    {
        if (Character is not { } c || pendingPlaces.Count == 0)
        {
            holdingsPace.Done(true, DateTime.UtcNow);
            return;
        }
        var places = pendingPlaces;
        var print = Holdings.Fingerprint(places);
        if (print == holdingsSent)
        {
            holdingsPace.Done(true, DateTime.UtcNow);
            return;
        }
        var args = new JsonObject
        {
            ["p_key"] = c.Key,
            ["p_items"] = SiteLink.Counts(Holdings.Total(places)),
            ["p_places"] = Holdings.PlacesJson(places),
        };
        Call("ml_plugin_holdings", args, holdingsPace, r =>
        {
            if (r.Ok) holdingsSent = print;
        });
    }

    private void AskInbox()
    {
        Call("ml_plugin_inbox", new JsonObject(), inbox, r =>
        {
            if (Body(r) is not JsonObject o) return;
            var waiting = new List<(Guid, string, DateTime, RunFile)>();
            if (o["runs"] is JsonArray runs)
                foreach (var n in runs)
                {
                    if (n is not JsonObject ro || !Guid.TryParse(JsonRead.Str(ro, "id"), out var id)) continue;
                    try
                    {
                        var file = RunFile.From(ro["run"]);
                        waiting.Add((id, JsonRead.Str(ro, "name") ?? file.Name, JsonRead.Time(ro, "sent_at") ?? DateTime.UtcNow, file));
                    }
                    catch (FormatException ex)
                    {
                        Services.Log.Warning($"Market Ledger: the run \"{JsonRead.Str(ro, "name")}\" from the site can't be read: {ex.Message}");
                    }
                }
            Waiting = waiting.Where(w => engine.File?.Guid != w.Item1 || !engine.Active).ToList();
            Version++;
            // A command waiting for the run in hand: the inbox only shows it; the next run update fetches
            // it (it's handed over once, there), so that goes now.
            if (o["active"] is JsonArray active && engine.File is { } run)
                foreach (var n in active)
                    if (n is JsonObject a && Guid.TryParse(JsonRead.Str(a, "id"), out var id) && id == run.Guid
                        && RunWords.Command(JsonRead.Str(a, "command")) != null)
                        update.Changed();
        });
    }

    private void SendUpdate(RunFile run)
    {
        var progress = engine.Progress();
        var status = engine.Status;
        var runId = run.Guid;
        if (updatingRun != runId)
        {
            updatingRun = runId;
            lastSentStatus = null;
        }
        var args = new JsonObject
        {
            ["p_run"] = run.Id,
            ["p_status"] = RunWords.Of(status),
            ["p_progress"] = progress.ToJson(),
        };
        Call("ml_plugin_run_update", args, update, r =>
        {
            if (!r.Ok)
            {
                // The run isn't the site's any more (deleted there, or finished there): not sent again.
                if (r.Hint == "run")
                {
                    updatesRefused = runId;
                    engine.Say(HelperNames.Ledger, $"The site won't take this run's progress any more: {r.Why}");
                }
                return;
            }
            if (updatingRun == runId) lastSentStatus = status;
            if (Body(r) is JsonObject o && RunWords.Command(JsonRead.Str(o, "command")) is { } command && engine.File?.Guid == runId)
                engine.Command(command);
        });
    }

    private void SendSales(string key)
    {
        var batch = salesWaiting.Take(500).ToList();
        var args = new JsonObject
        {
            ["p_key"] = key,
            ["p_sales"] = new JsonArray(batch.Select(s => (JsonNode)s.ToJson()).ToArray()),
        };
        Call("ml_plugin_sales", args, salesPace, r =>
        {
            // Refused as not readable or too big: not sent again (it would be refused again).
            if (!r.Ok && r.Hint is not ("data" or "size")) return;
            foreach (var s in batch)
            {
                salesSent.Add(s.Key);
                salesWaiting.RemoveAll(w => w.Key == s.Key);
            }
            if (salesWaiting.Count > 0) salesPace.Changed();
        });
    }

    private void SendListings(string key)
    {
        if (listingsWaiting is not { } l) return;
        var args = new JsonObject
        {
            ["p_key"] = key,
            ["p_retainer"] = l.Retainer,
            ["p_items"] = new JsonArray(l.Rows.Select(x => (JsonNode)x.ToJson()).ToArray()),
        };
        Call("ml_plugin_listings", args, listingsPace, r =>
        {
            if (!r.Ok && r.Hint is not ("data" or "size")) return;
            listingsSent[l.Retainer] = l.Print;
            if (listingsWaiting?.Print == l.Print) listingsWaiting = null;
        });
    }

    /// <summary>A run was started or ended here: the site hears at once.</summary>
    public void RunChanged()
    {
        update.Changed();
        inbox.Changed();
    }

    public void Dispose() => site.Dispose();
}
