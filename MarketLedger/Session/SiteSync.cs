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
/// <item><c>ml_plugin_inbox</c>: the runs sent from the site (every 30 s; every 6 s while a run is in hand),
/// a pending command for the run in hand, and (once, after loading) the run this plugin had in hand.</item>
/// <item><c>ml_plugin_run_update</c>: taking a run before it starts (<see cref="Claim"/>: "accepted"), then its
/// status and progress on each change (at most every 2 s) and every 15 s while it runs; its answer may carry a
/// command from the site (pause, resume, skip, stop). Each run's last word (done, stopped, failed, or paused
/// as the plugin unloads) is queued until the site takes it, so starting another never loses it.</item>
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
    // inbox 720, run_update 3,600, sales 240, listings 480): at most 180, 30, 600, 1,800, 180 and 450 (claims
    // and the last word on unloading, a few at most, go besides).
    private readonly Pace hello = new(TimeSpan.FromSeconds(20), HelloEvery);
    private readonly Pace holdingsPace = new(TimeSpan.FromMinutes(2));
    private readonly Pace inbox = new(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(30));
    private readonly Pace update = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15));
    private readonly Pace salesPace = new(TimeSpan.FromSeconds(20));
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

    /// <summary>Runs' last words (done, stopped, failed; paused on unloading), oldest first, until the site takes each.</summary>
    private readonly List<Final> finals = new();

    private readonly record struct Final(Guid Id, string RunId, RunStatus Status, JsonObject? Progress, string? Key);

    /// <summary>The site's newer optional arguments, left out of a function's calls once an older site says it doesn't know them.</summary>
    private readonly SiteArgs siteArgs = new();

    /// <summary>Each waiting run's <c>sent_at</c> as the site wrote it (said back, to the microsecond, when it's taken).</summary>
    private readonly Dictionary<Guid, string> sentAtWords = new();

    /// <summary>Waiting runs sent again since their file was kept: asked for whole next time.</summary>
    private readonly HashSet<Guid> resend = new();

    /// <summary>
    /// While a run is held, the site hears of it at least this often whatever its status (paused and waiting
    /// too): after 2 minutes of silence the site takes the plugin to be gone (its Stop finishes the run, and
    /// another plugin may take it over).
    /// </summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(45);

    /// <summary>Refused as switched off: the link is tried again then.</summary>
    private DateTime? offRetryAt;

    private static readonly TimeSpan OffRetry = TimeSpan.FromMinutes(10);

    /// <summary>A run being taken from the site (<see cref="Claim"/>), one at a time.</summary>
    private bool claiming;

    /// <summary>The inbox has answered once since the plugin loaded (the run in hand before is looked for then).</summary>
    private bool activeSeen;

    private DateTime windowsReadAt;

    /// <summary>
    /// The site's runs that are taken and not finished, as the first inbox after loading says them: the
    /// session picks up the one this plugin had in hand.
    /// </summary>
    public event Action<IReadOnlyList<ActiveRun>>? ActiveSeen;

    /// <summary>A run's last word was taken by the site (its id and the status said).</summary>
    public event Action<Guid, RunStatus>? FinalTaken;
    public SiteSync(Configuration config, RunEngine engine, HoldingsReader holdings, AllaganTools allagan)
    {
        this.config = config;
        this.engine = engine;
        this.holdings = holdings;
        this.allagan = allagan;
        site = new SiteSession(() => config.Token);
        engine.Changed += () => update.Changed();
        engine.Ended += (file, progress) => QueueFinal(file, progress, engine.RunKey);
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
        offRetryAt = null;
        updatesRefused = null;
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
        if (!site.Linked) return;
        if (Link is LinkState.Refused)
        {
            // Switched off on the site "for now": asked again every 10 minutes.
            if (offRetryAt is not { } at || now < at) return;
            offRetryAt = null;
            Link = new LinkState.Linking();
            hello.Reset();
            hello.Changed();
            inbox.Changed();
            Version++;
        }

        // A run in hand: the inbox is asked more often (the site's buttons answer sooner).
        inbox.Every = engine.Active ? TimeSpan.FromSeconds(6) : TimeSpan.FromSeconds(30);
        update.Every = !engine.Active ? null : engine.Status == RunStatus.Running ? TimeSpan.FromSeconds(15) : Heartbeat;

        if (Character is { } c && hello.Due(now)) SendHello(c);
        if (Link is not LinkState.Linked) return;
        if (Character != null && holdingsPace.Due(now)) SendHoldings();
        if (inbox.Due(now)) AskInbox();
        // Runs' last words first (one replaced by another must still be heard of), then the run in hand's
        // progress ("sent" and "draft" are the site's own words, never said).
        // (The run in hand's heartbeat goes even while a last word waits, so a site slow to take that one never
        // has the run in hand taken for gone.)
        var held = engine.File is { } inHand && engine.Active && updatesRefused != inHand.Guid ? inHand : null;
        var heartbeatDue = held != null && now - runUpdateAt > Heartbeat;
        if (heartbeatDue) update.Changed();
        if (finals.Count > 0 && !heartbeatDue)
        {
            if (update.Due(now)) SendFinal(finals[0]);
        }
        else if (held != null && update.Due(now)) SendUpdate(held);
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
            // What another character's retainers listed and sold isn't this one's.
            salesWaiting.Clear();
            listingsWaiting = null;
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
        // Once a second: the sale history's values are read whole each time.
        if (now - windowsReadAt < TimeSpan.FromSeconds(1)) return;
        windowsReadAt = now;
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
        {
            var waitingKeys = salesWaiting.Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var row in s.Rows)
                if (!salesSent.Contains(row.Key) && waitingKeys.Add(row.Key))
                {
                    salesWaiting.Add(row);
                    salesPace.Changed();
                }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Calls.

    /// <summary>Calls a function on a worker; <paramref name="then"/> runs on the framework thread with the answer (unless the link changed meanwhile).</summary>
    private void Call(string function, JsonObject args, Pace pace, Action<RpcResult> then)
    {
        var gen = generation;
        var sent = siteArgs.For(function, args);
        site.Call(function, sent).ContinueWith(t =>
        {
            var r = t.Status == TaskStatus.RanToCompletion ? t.Result : new RpcResult(false, null, "The call failed.", Unreachable: true);
            answers.Enqueue(() =>
            {
                if (gen != generation) return;
                // An older site, without the newer arguments: they're left out from now on (the call goes again after the backoff).
                if (siteArgs.Learn(function, sent, r)) Services.Log.Information($"Market Ledger: the site's {function} is an older one; calling it without the newer arguments.");
                if (r.Hint == "rate") pace.RateLimited(DateTime.UtcNow, RateWait);
                else pace.Done(r.Ok, DateTime.UtcNow);
                if (!r.Ok)
                {
                    Services.Log.Debug($"Market Ledger: {function} didn't go: {r.Why}");
                    if (r.TokenRefused || (r.Hint == "off"))
                    {
                        Link = new LinkState.Refused(r.Why ?? "The site turned the link down.");
                        offRetryAt = r.Hint == "off" ? DateTime.UtcNow + OffRetry : null;
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
        // The runs whose files the plugin has (in hand, and waiting): the site leaves their files out.
        var known = new JsonArray();
        var have = new Dictionary<Guid, RunFile>();
        if (engine.File is { } inHand) have[inHand.Guid] = inHand;
        foreach (var w in Waiting) have[w.Id] = w.File;
        // A run whose sending changed since its file was kept (sent again, maybe with a new gil limit)
        // is asked for whole, so a stale file is never offered or started.
        foreach (var id in have.Keys)
            if (!resend.Contains(id)) known.Add(id.ToString());
        Call("ml_plugin_inbox", new JsonObject { ["p_known"] = known }, inbox, r =>
        {
            if (Body(r) is not JsonObject o) return;
            var waiting = new List<(Guid, string, DateTime, RunFile)>();
            if (o["runs"] is JsonArray runs)
                foreach (var n in runs)
                {
                    if (n is not JsonObject ro || !Guid.TryParse(JsonRead.Str(ro, "id"), out var id)) continue;
                    try
                    {
                        // Left out as known: the file kept from before, but only if it's still the same
                        // sending; sent again since, it's asked for whole on the next call.
                        var sentWords = JsonRead.Str(ro, "sent_at");
                        RunFile? file;
                        if (ro["run"] is JsonObject)
                        {
                            file = RunFile.From(ro["run"]);
                            resend.Remove(id);
                        }
                        else if (have.TryGetValue(id, out var kept)
                            && (sentWords == null || !sentAtWords.TryGetValue(id, out var keptWords) || keptWords == sentWords))
                            file = kept;
                        else
                        {
                            resend.Add(id);
                            continue;
                        }
                        if (sentWords != null) sentAtWords[id] = sentWords;
                        waiting.Add((id, JsonRead.Str(ro, "name") ?? file.Name, JsonRead.Time(ro, "sent_at") ?? DateTime.UtcNow, file));
                    }
                    catch (FormatException ex)
                    {
                        Services.Log.Warning($"Market Ledger: the run \"{JsonRead.Str(ro, "name")}\" from the site can't be read: {ex.Message}");
                    }
                }
            // A run this plugin has had in hand isn't offered again (the inbox may be a moment behind).
            Waiting = waiting.Where(w => !engine.Taken.Contains(w.Item1)).ToList();
            Version++;
            if (!activeSeen)
            {
                activeSeen = true;
                var list = new List<ActiveRun>();
                if (o["active"] is JsonArray all)
                    foreach (var n in all)
                        if (n is JsonObject a && ActiveRun.From(a) is { } taken) list.Add(taken);
                try
                {
                    ActiveSeen?.Invoke(list);
                }
                catch (Exception ex)
                {
                    Services.Log.Warning(ex, "Market Ledger couldn't look for the run it had in hand");
                }
            }
            // A command waiting for the run in hand: the inbox only shows it; the next run update fetches
            // it (it's handed over once, there), so that goes now.
            if (o["active"] is JsonArray active && engine.File is { } run)
                foreach (var n in active)
                    if (n is JsonObject a && Guid.TryParse(JsonRead.Str(a, "id"), out var id) && id == run.Guid
                        && RunWords.Command(JsonRead.Str(a, "command")) != null)
                        update.Changed();
        });
    }

    /// <summary>When the run in hand's progress last went (its heartbeat).</summary>
    private DateTime runUpdateAt;

    private void SendUpdate(RunFile run)
    {
        runUpdateAt = DateTime.UtcNow;
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
        if (engine.RunKey is { } key) args["p_key"] = key;
        Call("ml_plugin_run_update", args, update, r =>
        {
            if (!r.Ok)
            {
                // The run isn't the site's any more (deleted there, or finished there): not sent again.
                if (r.Hint == "run")
                {
                    updatesRefused = runId;
                    engine.Say(HelperNames.Ledger, $"The site won't take this run's progress any more: {r.Why}");
                    // Stopped or deleted there, sent again, or held by another of your plugins (another PC or game
                    // client): it isn't carried on here (nor any more gil spent on it).
                    if (engine.Active && engine.File?.Guid == runId) engine.Stop($"The site won't let this plugin carry it on: {r.Why ?? "it was stopped or deleted there."}");
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

    /// <summary>A run's last word, kept until the site takes it (one said again for the same run replaces it).</summary>
    public void QueueFinal(RunFile file, RunProgress progress, string? key) => QueueFinal(file.Guid, file.Id, progress.Status, progress.ToJson(), key);

    /// <summary>A run's last word: its status, its progress (null keeps what the site has), and the character carrying it out.</summary>
    public void QueueFinal(Guid id, string runId, RunStatus status, JsonObject? progress, string? key)
    {
        finals.RemoveAll(f => f.Id == id);
        finals.Add(new Final(id, runId, status, progress, key));
        update.Changed();
    }

    private void SendFinal(Final f)
    {
        Call("ml_plugin_run_update", FinalArgs(f), update, r =>
        {
            // Taken; or refused as not the site's any more (or unreadable): either way, not said again.
            if (!r.Ok && r.Hint is not ("run" or "data" or "size")) return;
            finals.RemoveAll(x => x.Id == f.Id && x.Status == f.Status);
            if (updatingRun == f.Id) lastSentStatus = f.Status;
            if (r.Ok) FinalTaken?.Invoke(f.Id, f.Status);
        });
    }

    private static JsonObject FinalArgs(Final f)
    {
        var args = new JsonObject
        {
            ["p_run"] = f.RunId,
            ["p_status"] = RunWords.Of(f.Status),
            ["p_progress"] = f.Progress?.DeepClone(),
        };
        if (f.Key != null) args["p_key"] = f.Key;
        return args;
    }

    /// <summary>
    /// The plugin is unloading: the runs' last words still waiting go now, waiting at most
    /// <paramref name="wait"/> for the site (the game waits meanwhile, so it's short).
    /// </summary>
    public void FlushOnUnload(TimeSpan wait)
    {
        if (!site.Linked || finals.Count == 0) return;
        var deadline = DateTime.UtcNow + wait;
        foreach (var f in finals.ToList())
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.FromMilliseconds(100)) break;
            try
            {
                var task = site.Call("ml_plugin_run_update", siteArgs.For("ml_plugin_run_update", FinalArgs(f)), left);
                if (!task.Wait(left)) break;
                if (task.Result.Ok) finals.Remove(f);
            }
            catch (Exception ex)
            {
                Services.Log.Debug(ex, "Market Ledger couldn't tell the site about the run while unloading");
                break;
            }
        }
    }

    /// <summary>
    /// Takes a run from the site before it starts: says it's "accepted" (the site lets one plugin take a
    /// run sent, once; a run stopped there meanwhile is refused). <paramref name="then"/> runs on the framework
    /// thread with the answer, whatever happens.
    /// </summary>
    public void Claim(RunFile file, Action<RpcResult> then)
    {
        if (claiming)
        {
            then(new RpcResult(false, null, "Another run is being taken from the site: a moment."));
            return;
        }
        if (!site.Linked || Link is not LinkState.Linked)
        {
            then(new RpcResult(false, null, "The plugin isn't linked to the site yet (Settings), so it can't take the run."));
            return;
        }
        claiming = true;
        var args = new JsonObject
        {
            ["p_run"] = file.Id,
            ["p_status"] = RunWords.Of(RunStatus.Accepted),
            ["p_progress"] = Accepted(file).ToJson(),
        };
        // The sending this is (a run sent again since is refused), and who carries it out.
        if (sentAtWords.TryGetValue(file.Guid, out var sentAt)) args["p_sent_at"] = sentAt;
        if (Character?.Key is { Length: > 0 } key) args["p_key"] = key;
        var sent = siteArgs.For("ml_plugin_run_update", args);
        var gen = generation;
        site.Call("ml_plugin_run_update", sent).ContinueWith(t =>
        {
            var r = t.Status == TaskStatus.RanToCompletion ? t.Result : new RpcResult(false, null, "The call failed.", Unreachable: true);
            answers.Enqueue(() =>
            {
                claiming = false;
                if (siteArgs.Learn("ml_plugin_run_update", sent, r))
                    r = new RpcResult(false, null, "The site is an older one: press Start again.");
                if (gen != generation) r = new RpcResult(false, null, "The link changed meanwhile: try again.");
                else if (r.TokenRefused || r.Hint == "off")
                {
                    Link = new LinkState.Refused(r.Why ?? "The site turned the link down.");
                    offRetryAt = r.Hint == "off" ? DateTime.UtcNow + OffRetry : null;
                    Version++;
                }
                if (r.Ok) updatingRun = file.Guid;
                then(r);
            });
        }, TaskScheduler.Default);
    }

    /// <summary>A run's progress as it's taken: every step still to do.</summary>
    public static RunProgress Accepted(RunFile file)
    {
        var now = DateTime.UtcNow;
        var steps = file.Steps.Select(s => new ProgressStep(s.Id, StepState.Waiting, 0, s.Lines.Sum(l => Math.Max(0, l.Amount)))).ToList();
        return new RunProgress(RunStatus.Accepted, 0, steps, 0, null, now, new[] { new ProgressLog(now, HelperNames.Ledger, "Taken by the plugin.") });
    }

    /// <summary>A run was started or ended here: the site hears at once.</summary>
    public void RunChanged()
    {
        update.Changed();
        inbox.Changed();
    }

    public void Dispose() => site.Dispose();
}
