using System.Net;
using System.Text.Json.Nodes;
using MarketLedger.Core;
using MarketLedger.Session;
using Xunit;

namespace MarketLedger.Tests;

public class StepMathTests
{
    private static readonly RunLine Gather = new(43976, 280, 300);

    [Fact]
    public void CountsUpToTheNeed()
    {
        // The step started with 20 in the bags (need 300 less amount 280).
        Assert.Equal(0, StepMath.Done(RunStepKind.Gather, Gather, 20));
        Assert.Equal(112, StepMath.Done(RunStepKind.Gather, Gather, 132));
        Assert.Equal(280, StepMath.Done(RunStepKind.Gather, Gather, 400));
        Assert.Equal(0, StepMath.Done(RunStepKind.Gather, Gather, 0));
        Assert.Equal(168, StepMath.Missing(RunStepKind.Gather, Gather, 132));
        Assert.False(StepMath.LineDone(RunStepKind.Gather, Gather, 299));
        Assert.True(StepMath.LineDone(RunStepKind.Gather, Gather, 300));
    }

    [Fact]
    public void EntrustCountsDownToWhatsLeft()
    {
        // 60 to entrust, none left in the bags after.
        var line = new RunLine(44162, 60, 0, true);
        Assert.Equal(0, StepMath.Done(RunStepKind.Entrust, line, 60));
        Assert.Equal(25, StepMath.Done(RunStepKind.Entrust, line, 35));
        Assert.Equal(35, StepMath.Missing(RunStepKind.Entrust, line, 35));
        Assert.True(StepMath.LineDone(RunStepKind.Entrust, line, 0));
        // 5 kept back: done at 5.
        var keep = new RunLine(1, 10, 5);
        Assert.True(StepMath.LineDone(RunStepKind.Entrust, keep, 5));
        Assert.Equal(10, StepMath.Done(RunStepKind.Entrust, keep, 5));
    }

    [Fact]
    public void ProgressAddsTheLines()
    {
        var step = new RunStep("s1", RunStepKind.Market, new[] { new RunLine(1, 10, 10), new RunLine(2, 5, 8) });
        var have = new Dictionary<uint, int> { [1] = 4, [2] = 8 };
        Assert.Equal((9, 15), StepMath.Progress(step, l => have[l.Item]));
        Assert.False(StepMath.StepDone(step, l => have[l.Item]));
        have[1] = 10;
        Assert.True(StepMath.StepDone(step, l => have[l.Item]));
    }

    [Fact]
    public void CraftsLeftFollowTheYield()
    {
        // 14 crafts making 42: 3 each.
        var craft = new RunStep("s6", RunStepKind.Craft, new[] { new RunLine(43900, 42, 50) }) { Recipe = 35500, Crafts = 14 };
        Assert.Equal(3, StepMath.Yield(craft));
        Assert.Equal(14, StepMath.CraftsLeft(craft, 8));
        Assert.Equal(4, StepMath.CraftsLeft(craft, 40));
        Assert.Equal(0, StepMath.CraftsLeft(craft, 50));
        // Never more than planned, even when something went missing.
        Assert.Equal(14, StepMath.CraftsLeft(craft, 0));
        var none = new RunStep("x", RunStepKind.Craft, new[] { new RunLine(1, 0, 0) }) { Crafts = 0 };
        Assert.Equal(1, StepMath.Yield(none));
    }

    [Fact]
    public void NextStepPassesWhatsThrough()
    {
        var states = new[] { StepState.Done, StepState.Skipped, StepState.Failed, StepState.Waiting };
        Assert.Equal(2, StepMath.NextStep(states));
        Assert.Equal(3, StepMath.NextStep(states, 3));
        Assert.Equal(-1, StepMath.NextStep(states, 4));
        Assert.Equal(-1, StepMath.NextStep(new[] { StepState.Done }));
    }

    [Fact]
    public void LabelsAndWho()
    {
        var step = new RunStep("s4", RunStepKind.Gather, new[] { new RunLine(1, 1, 1), new RunLine(2, 1, 1) });
        Assert.Equal("Gather Ore and 1 more", StepMath.Label(step, id => id == 1 ? "Ore" : "Log"));
        Assert.Equal(HelperNames.GatherBuddy, StepMath.By(RunStepKind.Gather));
        Assert.Equal(HelperNames.Artisan, StepMath.By(RunStepKind.Craft));
        Assert.Equal(HelperNames.Ledger, StepMath.By(RunStepKind.Withdraw));
    }

    [Fact]
    public void InboxPutsLaterLast()
    {
        var a = (Id: Guid.NewGuid(), At: new DateTime(2026, 11, 1));
        var b = (Id: Guid.NewGuid(), At: new DateTime(2026, 11, 2));
        var c = (Id: Guid.NewGuid(), At: new DateTime(2026, 11, 3));
        var order = RunOrder.Inbox(new[] { c, a, b }, r => r.Id, r => r.At, new[] { a.Id });
        Assert.Equal(new[] { b.Id, c.Id, a.Id }, order.Select(r => r.Id));
        var twice = RunOrder.Inbox(new[] { c, a, b }, r => r.Id, r => r.At, new[] { c.Id, a.Id });
        Assert.Equal(new[] { b.Id, c.Id, a.Id }, twice.Select(r => r.Id));
    }

    [Fact]
    public void TheLogKeepsTheNewestFirst()
    {
        var log = new RunLog();
        var t = new DateTime(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc);
        log.Add(t, "A", "one");
        log.Add(t.AddSeconds(1), "A", "two");
        log.Add(t.AddSeconds(2), "A", "two");
        Assert.Equal(new[] { "two", "one" }, log.Lines.Select(l => l.Text));
        for (var i = 0; i < RunLog.Keep + 10; i++) log.Add(t.AddMinutes(i + 1), "B", $"n{i}");
        Assert.Equal(RunLog.Keep, log.Lines.Count);
        Assert.Equal($"n{RunLog.Keep + 9}", log.Lines[0].Text);
    }
}

public class MarketRuleTests
{
    [Fact]
    public void CostAddsTheTax()
    {
        Assert.Equal(1050, MarketRule.Cost(100, 10, 0));
        Assert.Equal(1020, MarketRule.Cost(100, 10, 20));
        Assert.Equal(12075, MarketRule.LineBudget(10, 1150));
        Assert.Equal(0, MarketRule.LineBudget(0, 1150));
    }

    [Fact]
    public void BuysOnlyAtOrUnderTheLinesPrice()
    {
        Assert.Null(MarketRule.WhyNot(1150, 5, 0, 1150, 10, 100_000, 100_000, 100_000));
        Assert.Contains("the run allows", MarketRule.WhyNot(1151, 5, 0, 1150, 10, 100_000, 100_000, 100_000));
        Assert.Contains("left for this line", MarketRule.WhyNot(1000, 99, 0, 1150, 10, 20_000, 1_000_000, 1_000_000));
        Assert.Contains("gil agreed", MarketRule.WhyNot(1000, 5, 0, 1150, 10, 100_000, 5000, 100_000));
        Assert.Contains("more than you have", MarketRule.WhyNot(1000, 5, 0, 1150, 10, 100_000, 100_000, 4000));
        Assert.Equal("enough already", MarketRule.WhyNot(1000, 5, 0, 1150, 0, 100_000, 100_000, 100_000));
        // HQ only when the line asks for it.
        Assert.NotNull(MarketRule.WhyNot(1000, 5, 0, 1150, 10, 100_000, 100_000, 100_000, hqWanted: true, hq: false));
        Assert.Null(MarketRule.WhyNot(1000, 5, 0, 1150, 10, 100_000, 100_000, 100_000, hqWanted: false, hq: true));
    }

    [Fact]
    public void PicksTheCheapestThatFit()
    {
        var listings = new List<(uint, int, uint, bool)>
        {
            (205, 99, 0, false),
            (188, 24, 0, false),
            (260, 12, 0, false),
            (199, 30, 0, false),
        };
        // 60 wanted at up to 210 each, a line budget of 60 × 210 with tax.
        var picked = MarketRule.Pick(listings, 210, 60, MarketRule.LineBudget(60, 210), 1_000_000, 1_000_000);
        Assert.Equal(new[] { 1, 3 }, picked.Take(2));
        Assert.DoesNotContain(2, picked);
        // The 99 at 205 is more than the line's gil left once the first two are bought.
        Assert.DoesNotContain(0, picked);
        // Within the run's gil left only the cheapest fits.
        Assert.Equal(new[] { 1 }, MarketRule.Pick(listings, 210, 60, 1_000_000, 5000, 1_000_000));
    }

    [Fact]
    public void WhatsLeftToSpend()
    {
        Assert.Equal(30_000, MarketRule.MarketLeft(52_000, 22_000, 0, 10_000));
        Assert.Equal(5_000, MarketRule.MarketLeft(52_000, 22_000, 15_000, 10_000));
        Assert.Equal(0, MarketRule.MarketLeft(10_000, 22_000, 0, 0));
        Assert.Null(MarketRule.VendorWhyNot(120, 20, 52_000, 0, 10_000));
        Assert.NotNull(MarketRule.VendorWhyNot(120, 20, 2_000, 0, 10_000));
        Assert.NotNull(MarketRule.VendorWhyNot(120, 20, 52_000, 0, 1_000));
    }

    [Fact]
    public void TheBoardsQuestionMustNameTheListing()
    {
        Assert.True(MarketRule.BoardQuestionMatches("Purchase 1 Cieldalaes planter for 7,229 gil (344 fee included)?", 1, 7229, 6885));
        Assert.True(MarketRule.BoardQuestionMatches("Purchase 20 ore for 4,200 gil (200 fee included)?", 20, 4200, 4000));
        Assert.False(MarketRule.BoardQuestionMatches("Purchase 20 ore for 10,500 gil (500 fee included)?", 1, 525, 500));
        Assert.False(MarketRule.BoardQuestionMatches("Purchase 3 ore for 4,200 gil?", 20, 4200, 4000));
        Assert.False(MarketRule.BoardQuestionMatches(null, 1, 100, 95));
    }

    [Fact]
    public void TheShopsQuestionMustNameTheItemAndPrice()
    {
        Assert.True(MarketRule.QuestionMatches("Purchase 3 lumps of beeswax for 360 gil?", 120, 3, 360));
        Assert.False(MarketRule.QuestionMatches("Purchase 3 lumps of beeswax for 999 gil?", 120, 3, 360));
        Assert.True(MarketRule.MentionsItem("Purchase 3 lumps of beeswax for 360 gil?", "Beeswax"));
        Assert.False(MarketRule.MentionsItem("Purchase 3 maple logs for 360 gil?", "Beeswax"));
        Assert.Equal(new long[] { 3, 1234567, 12 }, MarketRule.Numbers("3 for 1,234,567 gil (12)"));
        Assert.Equal(new long[] { 1234 }, MarketRule.Numbers("1.234"));
    }

    [Fact]
    public void NewSlotsCountStacks()
    {
        Assert.Equal(1, MarketRule.NewSlots(0, 99, 999));
        Assert.Equal(0, MarketRule.NewSlots(10, 99, 999));
        Assert.Equal(1, MarketRule.NewSlots(990, 20, 999));
        Assert.Equal(2, MarketRule.NewSlots(0, 2, 1));
        Assert.Equal(0, MarketRule.NewSlots(5, 0, 99));
    }
}

public class PacingTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AChangeGoesAtMostEveryGap()
    {
        var p = new Pace(TimeSpan.FromSeconds(2));
        Assert.False(p.Due(T0));
        p.Changed();
        Assert.True(p.Due(T0));
        // One call at a time.
        p.Changed();
        Assert.False(p.Due(T0.AddSeconds(5)));
        p.Done(true, T0.AddSeconds(1));
        Assert.False(p.Due(T0.AddSeconds(1.5)));
        // The change made meanwhile still goes, once the gap's past.
        Assert.True(p.Due(T0.AddSeconds(2)));
    }

    [Fact]
    public void ItGoesOnABeatTooWhenAsked()
    {
        var p = new Pace(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15));
        Assert.True(p.Due(T0));
        p.Done(true, T0);
        Assert.False(p.Due(T0.AddSeconds(10)));
        Assert.True(p.Due(T0.AddSeconds(15)));
    }

    [Fact]
    public void FailuresWaitLongerEachTime()
    {
        var p = new Pace(TimeSpan.FromSeconds(1), backoffFirst: TimeSpan.FromSeconds(5), backoffMost: TimeSpan.FromSeconds(20));
        p.Changed();
        Assert.True(p.Due(T0));
        p.Done(false, T0);
        Assert.False(p.Due(T0.AddSeconds(4)));
        Assert.True(p.Due(T0.AddSeconds(5)));
        p.Done(false, T0.AddSeconds(5));
        Assert.False(p.Due(T0.AddSeconds(14)));
        Assert.True(p.Due(T0.AddSeconds(15)));
        p.Done(true, T0.AddSeconds(15));
        p.Changed();
        Assert.True(p.Due(T0.AddSeconds(16)));
    }

    [Fact]
    public void BackoffDoublesUpToItsMost()
    {
        var b = new Backoff(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(12));
        b.Failed(T0);
        Assert.Equal(T0.AddSeconds(5), b.Until);
        b.Failed(T0);
        Assert.Equal(T0.AddSeconds(10), b.Until);
        b.Failed(T0);
        Assert.Equal(T0.AddSeconds(12), b.Until);
        b.Succeeded();
        Assert.True(b.Ready(T0));
    }

    [Fact]
    public void TooOftenWaitsAndThenGoes()
    {
        var p = new Pace(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(30));
        Assert.True(p.Due(T0));
        p.RateLimited(T0.AddSeconds(1), TimeSpan.FromMinutes(2));
        Assert.False(p.Busy);
        Assert.False(p.Due(T0.AddSeconds(100)));
        Assert.True(p.Due(T0.AddSeconds(121)));
    }

    [Fact]
    public void ResetForgetsEverything()
    {
        var p = new Pace(TimeSpan.FromMinutes(2));
        p.Changed();
        Assert.True(p.Due(T0));
        p.Reset();
        p.Changed();
        Assert.True(p.Due(T0.AddSeconds(1)));
    }
}

public class SiteTests
{
    private const string Token = "mlp_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ";

    [Fact]
    public void TokensHaveTheirShape()
    {
        Assert.Equal(47, Token.Length);
        Assert.True(SiteLink.LooksRight(Token));
        Assert.False(SiteLink.LooksRight("rpp_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ"));
        Assert.False(SiteLink.LooksRight(Token[..^1]));
        Assert.False(SiteLink.LooksRight(null));
        Assert.Equal(Token, SiteLink.Clean($"  \"{Token}\"\n"));
        Assert.Equal("mlp_••••••••NOPQ", SiteLink.Masked(Token));
    }

    [Fact]
    public void CharacterKeysAreSaltedAndShort()
    {
        var a = SiteLink.CharacterKey(18014398509481984, "salt-one");
        Assert.Equal(32, a.Length);
        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.Equal(a, SiteLink.CharacterKey(18014398509481984, "salt-one"));
        Assert.NotEqual(a, SiteLink.CharacterKey(18014398509481984, "salt-two"));
        Assert.NotEqual(a, SiteLink.CharacterKey(18014398509481985, "salt-one"));
        Assert.Matches("^[0-9a-f]{32}$", SiteLink.NewSalt());
    }

    [Fact]
    public void CountsAreWrittenAsTheSiteTakesThem()
    {
        var o = SiteLink.Counts(new Dictionary<uint, int> { [5] = 240, [44030] = 0, [2] = 3 });
        Assert.Equal("{\"2\":3,\"5\":240}", o.ToJsonString());
    }

    [Theory]
    [InlineData("token", true, false)]
    [InlineData("off", false, false)]
    [InlineData("rate", false, false)]
    [InlineData("run", false, false)]
    [InlineData("size", false, false)]
    [InlineData("data", false, false)]
    public void RefusalsAreSaidInTheSitesWords(string hint, bool tokenRefused, bool unreachable)
    {
        var r = SiteRpc.Read(HttpStatusCode.BadRequest, $"{{\"message\":\"Nope, for a reason.\",\"hint\":\"{hint}\"}}");
        Assert.False(r.Ok);
        Assert.Equal(hint, r.Hint);
        Assert.Equal(tokenRefused, r.TokenRefused);
        Assert.Equal(unreachable, r.Unreachable);
        Assert.StartsWith("Nope, for a reason.", r.Why);
    }

    [Fact]
    public void OtherAnswersAreWordedHere()
    {
        Assert.True(SiteRpc.Read(HttpStatusCode.OK, "{\"ok\":true}").Ok);
        var db = SiteRpc.Read(HttpStatusCode.BadRequest, "{\"message\":\"relation ml_runs does not exist\",\"hint\":null}");
        Assert.Equal("The site said no (400).", db.Why);
        Assert.True(SiteRpc.Read(HttpStatusCode.ServiceUnavailable, "<html>").Unreachable);
        Assert.Contains("updating", SiteRpc.Read(HttpStatusCode.NotFound, string.Empty).Why);
    }

    [Fact]
    public async Task NothingLeavesWithoutAToken()
    {
        using var http = new HttpClient();
        var r = await SiteRpc.CallAsync(http, "ml_plugin_hello", SiteRpc.Args("not a token"), TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True(r.TokenRefused);
    }

    [Fact]
    public async Task OnlyThePluginsFunctionsAreCalled()
    {
        // Another tool's function is refused before anything is sent.
        using var http = new HttpClient();
        var r = await SiteRpc.CallAsync(http, "rp_plugin_send", SiteRpc.Args(Token), TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal("That couldn't be sent.", r.Why);
    }
}

public class HelperTests
{
    [Fact]
    public void AFailingCallIsLoggedOnce()
    {
        var logged = new List<string>();
        var guard = new IpcGuard("Artisan", (_, what) => logged.Add(what));
        Assert.Equal(-1, guard.Call<int>("IsBusy", () => throw new InvalidOperationException("not ready"), -1));
        Assert.Equal(-1, guard.Call<int>("IsBusy", () => throw new InvalidOperationException("not ready"), -1));
        Assert.Single(logged);
        Assert.Equal("Artisan didn't answer IsBusy", logged[0]);
        Assert.False(guard.Do("CraftItem", () => throw new InvalidOperationException()));
        Assert.True(guard.Do("SetEnduranceStatus", () => { }));
        Assert.Equal(7, guard.Call("Version", () => 7, 0));
        Assert.Equal(2, logged.Count);
        guard.Reset();
        guard.Call<int>("IsBusy", () => throw new InvalidOperationException(), 0);
        Assert.Equal(3, logged.Count);
    }

    [Fact]
    public void HelpersAreJudgedByWhatAnswered()
    {
        Assert.Equal(HelperIpc.No, HelperCheck.Judge(false, 3, 3));
        Assert.Equal(HelperIpc.No, HelperCheck.Judge(true, 0, 3));
        Assert.Equal(HelperIpc.Partly, HelperCheck.Judge(true, 2, 3));
        Assert.Equal(HelperIpc.Ok, HelperCheck.Judge(true, 3, 3));
        Assert.Equal("not installed", HelperCheck.Words(false, null, HelperIpc.No));
        Assert.Equal("4.0.5.21 · IPC ok", HelperCheck.Words(true, "4.0.5.21", HelperIpc.Ok));
        Assert.Equal("found · partly", HelperCheck.Words(true, null, HelperIpc.Partly));
        Assert.True(HelperCheck.AtLeast("7.5.6.0", "7.5"));
        Assert.False(HelperCheck.AtLeast("7.4.0.0", "7.5"));
        Assert.False(HelperCheck.AtLeast(null, "1.0"));
        Assert.Equal(5, HelperCheck.IpcVersion(5));
        Assert.Equal(2, HelperCheck.IpcVersion("2"));
        Assert.Null(HelperCheck.IpcVersion(new object()));
    }
}

public class LedgerTests
{
    [Fact]
    public void HoldingsAddUpPlaceByPlace()
    {
        var places = new[]
        {
            new Place("Bags", "bags", new Dictionary<uint, int> { [5] = 60, [44051] = 3 }),
            new Place("Mimi", "retainer", new Dictionary<uint, int> { [5] = 240 }),
        };
        var total = Holdings.Total(places);
        Assert.Equal(300, total[5]);
        Assert.Equal(3, total[44051]);
        var json = Holdings.PlacesJson(places);
        Assert.Equal("retainer", json[1]!["kind"]!.GetValue<string>());
        Assert.Equal(240, json[1]!["items"]!["5"]!.GetValue<int>());
        Assert.Equal(Holdings.Fingerprint(places), Holdings.Fingerprint(Enumerable.Reverse(places)));
        Assert.NotEqual(Holdings.Fingerprint(places), Holdings.Fingerprint(places.Take(1)));
    }

    [Fact]
    public void HqAndCollectableIdsCountAsTheItem()
    {
        var c = Holdings.Count(new (uint, long)[] { (5, 10), (1_000_005, 2), (500_005, 1), (0, 9), (7, 0) });
        Assert.Equal(13, c[5]);
        Assert.Single(c);
    }

    [Fact]
    public void SalesAreFoundByTheirShape()
    {
        uint ByName(string n) => n switch { "Gemdraught of Strength" => 44162, "Ra'Kaznar Ingot" => 43900, _ => 0 };
        var values = new List<UiValue>
        {
            UiValue.Of(2),
            UiValue.Of("Gemdraught of Strength"), UiValue.Of(2400), UiValue.Of(3), UiValue.Of(1793700000), UiValue.Of("Some Buyer"),
            UiValue.Of("Ra'Kaznar Ingot"), UiValue.Of(950), UiValue.Of(10), UiValue.Of(1793690000), UiValue.Of("Another"),
            UiValue.Of("Nothing we know"), UiValue.Of(1), UiValue.Of(1), UiValue.Of(1793690000),
        };
        var rows = SaleHistory.Parse("Mimi", values, ByName, new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(2, rows.Count);
        Assert.Equal(44162u, rows[0].Item);
        Assert.True(rows[0].Hq);
        Assert.Equal(2400, rows[0].Price);
        Assert.Equal(3, rows[0].Quantity);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793700000).UtcDateTime, rows[0].At);
        Assert.False(rows[1].Hq);
        Assert.NotEqual(rows[0].Key, rows[1].Key);
        Assert.Equal(rows[0].Key, (rows[0] with { }).Key);
        Assert.Equal("Mimi", rows[0].ToJson()["retainer"]!.GetValue<string>());
        // A row without its time isn't kept.
        Assert.Empty(SaleHistory.Parse("Mimi", new[] { UiValue.Of("Ra'Kaznar Ingot"), UiValue.Of(950), UiValue.Of(10) }, ByName, DateTime.UtcNow));
    }

    [Fact]
    public void JobsAreTheMakersAndGatherers()
    {
        var o = Jobs.ToJson(new Dictionary<string, int> { ["CRP"] = 100, ["WHM"] = 90, ["MIN"] = 0, ["FSH"] = 52 });
        Assert.Equal("{\"CRP\":100,\"FSH\":52}", o.ToJsonString());
    }

    [Fact]
    public void GatherListIsInTeamcraftsWords()
    {
        Assert.Equal("280x Ra'Kaznar Ore\n10x Claro Walnut Log", GatherList.Teamcraft(new[] { ("Ra'Kaznar Ore", 280), ("Claro Walnut Log", 10), ("Nothing", 0) }));
        Assert.StartsWith("Market Ledger · ", GatherList.Name("Gemdraughts"));
    }

    [Fact]
    public void ListingsFingerprintIgnoresOrder()
    {
        var a = new[] { new ListingRow(1, false, 5, 100), new ListingRow(2, true, 1, 900) };
        Assert.Equal(ListingRow.Fingerprint("Mimi", a), ListingRow.Fingerprint("Mimi", Enumerable.Reverse(a)));
        Assert.NotEqual(ListingRow.Fingerprint("Mimi", a), ListingRow.Fingerprint("Pom", a));
    }
}

public class EorzeaTests
{
    [Fact]
    public void ItRunsTwentyTimesAsFast()
    {
        // 175 real seconds is one Eorzea hour.
        var t0 = DateTimeOffset.FromUnixTimeSeconds(0).UtcDateTime;
        Assert.Equal((0, 0), Eorzea.Now(t0));
        Assert.Equal((1, 0), Eorzea.Now(t0.AddSeconds(175)));
        Assert.Equal("16:00 ET", Eorzea.Format(16, 0));
    }

    [Fact]
    public void WindowsOpenAtTheirHours()
    {
        var t0 = DateTimeOffset.FromUnixTimeSeconds(0).UtcDateTime;
        var w = new TimedWindow(new[] { 4, 16 }, 120);
        var (opens, closes, open) = Eorzea.NextWindow(w, t0.AddSeconds(175));
        Assert.False(open);
        Assert.Equal(t0.AddSeconds(4 * 175), opens);
        Assert.Equal(t0.AddSeconds(6 * 175), closes);
        // Inside the window: open, from its start.
        var inside = Eorzea.NextWindow(w, t0.AddSeconds(5 * 175));
        Assert.True(inside.Open);
        Assert.Equal(t0.AddSeconds(4 * 175), inside.OpensAt);
        // After the day's last: the next day's first.
        var late = Eorzea.NextWindow(w, t0.AddSeconds(20 * 175));
        Assert.Equal(t0.AddSeconds(28 * 175), late.OpensAt);
    }
}
