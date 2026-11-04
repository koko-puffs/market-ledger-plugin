using System.Text.Json.Nodes;
using MarketLedger.Core;
using Xunit;

namespace MarketLedger.Tests;

/// <summary>What the helpers hand over, read the way their sources write it.</summary>
public class HelperDataTests
{
    [Fact]
    public void AllaganToolsSlotsAreContainerSlotItemQuantity()
    {
        // CriticalCommonLib's InventoryItem.ToNumeric(): [0] container, [1] slot, [2] item, [3] quantity, then more.
        var slots = new[]
        {
            new ulong[] { 10000, 0, 5057, 12, 0, 0, 0 }, // bag page 1
            new ulong[] { 10006, 3, 5057, 3, 0 }, // bag page 7: the same item, added
            new ulong[] { 12001, 0, 5, 900 }, // crystals
            new ulong[] { 12002, 0, 44162, 20 }, // on the market: not withdrawable
            new ulong[] { 11000, 0, 3000, 1 }, // worn
            new ulong[] { 12000, 0, 1, 50000 }, // gil
            new ulong[] { 10001, 2, 0, 0 }, // an empty slot
            new ulong[] { 10002, 1 }, // too short to read
        };
        var counts = Holdings.FromAllagan(slots);
        Assert.Equal(2, counts.Count);
        Assert.Equal(15, counts[5057]);
        Assert.Equal(900, counts[5]);
    }

    [Fact]
    public void AutoRetainersCharacterIsReadFromItsJson()
    {
        // As Dalamud hands an OfflineCharacterData to an object subscriber: Newtonsoft's JSON of its fields.
        const string json = """
        { "CID": 18014398509481984, "Name": "Koko Puffs", "RetainerData": [
          { "Name": "Mimi", "RetainerID": 18446744073709551000, "Level": 100 },
          { "Name": "Momo", "RetainerID": 33776997205278720 },
          { "Name": "", "RetainerID": 5 },
          { "Name": "Nobody", "RetainerID": 0 } ] }
        """;
        var names = Holdings.RetainerNamesFromJson(json);
        Assert.Equal(2, names.Count);
        Assert.Equal("Mimi", names[18446744073709551000UL]);
        Assert.Equal("Momo", names[33776997205278720UL]);
        Assert.Empty(Holdings.RetainerNamesFromJson("not json"));
        Assert.Empty(Holdings.RetainerNamesFromJson("{\"RetainerData\": 3}"));
        Assert.Empty(Holdings.RetainerNamesFromJson(null));
    }
}

public class ProgressWordsTests
{
    [Fact]
    public void DoingAndHeldForYouAreSentOnlyWhenThereIsSomething()
    {
        var at = new DateTime(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc);
        var quiet = new RunProgress(RunStatus.Running, 0, Array.Empty<ProgressStep>(), 0, at, at, Array.Empty<ProgressLog>()).ToJson();
        Assert.Null(quiet["doing"]);
        Assert.Null(quiet["heldForYou"]);

        var p = new RunProgress(RunStatus.Waiting, 1, Array.Empty<ProgressStep>(), 10, at, at, Array.Empty<ProgressLog>(), "Watch it")
        {
            Doing = "Gathering Ra'Kaznar Ore · GatherBuddy Reborn",
            HeldForYou = true,
        };
        var o = p.ToJson();
        Assert.Equal("Gathering Ra'Kaznar Ore · GatherBuddy Reborn", (string?)o["doing"]);
        Assert.True((bool)o["heldForYou"]!);
        var back = RunProgress.From(JsonNode.Parse(o.ToJsonString()))!;
        Assert.Equal(p.Doing, back.Doing);
        Assert.True(back.HeldForYou);
        Assert.Equal("waiting", (string?)o["status"]);
    }
}

public class PickUpTests
{
    private static RunFile Run() => RunFile.Parse(RunFileTests.Sample);

    [Fact]
    public void ActiveRunsAreReadWithTheirRunAndProgressWhenSent()
    {
        var run = Run();
        var progress = new RunProgress(RunStatus.Paused, 2, new[] { new ProgressStep("s1", StepState.Done, 252, 252) }, 4200, DateTime.UtcNow, DateTime.UtcNow, Array.Empty<ProgressLog>(), "The plugin was unloaded.");
        var a = new JsonObject
        {
            ["id"] = run.Id,
            ["status"] = "paused",
            ["command"] = null,
            ["run"] = run.ToJson(),
            ["progress"] = progress.ToJson(),
        };
        var read = ActiveRun.From(a)!;
        Assert.Equal(run.Guid, read.Id);
        Assert.Equal(RunStatus.Paused, read.Status);
        Assert.Equal(run.Steps.Count, read.File!.Steps.Count);
        Assert.Equal(4200L, read.Progress!.Spent);

        // Today's inbox: no run nor progress in it.
        var bare = ActiveRun.From(new JsonObject { ["id"] = run.Id, ["status"] = "running", ["command"] = "pause", ["name"] = "Gems", ["taken_key"] = "0123456789abcdef0123456789abcdef" })!;
        Assert.Equal("Gems", bare.Name);
        Assert.Equal("0123456789abcdef0123456789abcdef", bare.TakenKey);
        Assert.Null(bare.File);
        Assert.Null(bare.Progress);
        Assert.Equal("pause", bare.Command);

        // A run that can't be read is no run; an id that isn't one is nothing.
        var bad = (JsonObject)a.DeepClone();
        bad["run"]!["version"] = 99;
        Assert.Null(ActiveRun.From(bad)!.File);
        Assert.Null(ActiveRun.From(new JsonObject { ["id"] = "nope" }));
    }

    [Fact]
    public void StepsPickUpWhereTheProgressLeftThem()
    {
        var run = Run();
        var progress = new RunProgress(RunStatus.Paused, 3, new[]
        {
            new ProgressStep("s1", StepState.Done, 252, 252),
            new ProgressStep("s2", StepState.Skipped, 0, 20),
            new ProgressStep("s3", StepState.Failed, 30, 36, "Not bought"),
            new ProgressStep("s4", StepState.Running, 100, 280),
            new ProgressStep("nope", StepState.Done, 1, 1),
        }, 0, null, DateTime.UtcNow, Array.Empty<ProgressLog>());
        var (states, notes, step) = RunPickup.Restore(run, progress);
        Assert.Equal(StepState.Done, states[0]);
        Assert.Equal(StepState.Skipped, states[1]);
        Assert.Equal(StepState.Failed, states[2]);
        Assert.Equal("Not bought", notes[2]);
        Assert.Equal(StepState.Waiting, states[3]);
        Assert.Equal(StepState.Waiting, states[7]);
        Assert.Equal(3, step);
        Assert.Equal(3, StepMath.NextStep(states, step));

        var (fresh, _, first) = RunPickup.Restore(run, null);
        Assert.True(fresh.All(s => s == StepState.Waiting));
        Assert.Equal(0, first);
        Assert.Equal(run.Steps.Count - 1, RunPickup.Restore(run, progress with { Step = 99 }).Step);
    }

    [Fact]
    public void TheNextRunIsTheFirstSaidLaterStillWaiting()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        Assert.Equal(b, RunPickup.NextLater(new[] { a, b, c }, new[] { b, c }));
        Assert.Equal(c, RunPickup.NextLater(new[] { a, c }, new[] { b, c }));
        Assert.Null(RunPickup.NextLater(new[] { a }, new[] { b, c }));
        Assert.Null(RunPickup.NextLater(new[] { a, b }, Array.Empty<Guid>()));
    }
}

public class SiteArgsTests
{
    [Fact]
    public void AnOlderSiteGetsItsCallsWithoutTheNewerArguments()
    {
        var args = new SiteArgs();
        var call = new JsonObject { ["p_run"] = "x", ["p_status"] = "accepted", ["p_key"] = "k", ["p_sent_at"] = "t" };
        Assert.True(ReferenceEquals(call, args.For("ml_plugin_run_update", call)));

        // A 404 with the newer arguments: an older site.
        var notFound = SiteRpc.Read(System.Net.HttpStatusCode.NotFound, "{\"code\":\"PGRST202\"}");
        Assert.True(notFound.NotFound);
        Assert.True(args.Learn("ml_plugin_run_update", call, notFound));
        Assert.False(args.Learn("ml_plugin_run_update", call, notFound));
        var plain = args.For("ml_plugin_run_update", call);
        Assert.Equal(2, plain.Count);
        Assert.Null(plain["p_key"]);
        Assert.Equal("accepted", (string?)plain["p_status"]);

        // Another function isn't touched; nor is one whose 404 came without the newer arguments, or another failure.
        Assert.True(ReferenceEquals(call, args.For("ml_plugin_inbox", call)));
        Assert.False(args.Learn("ml_plugin_hello", new JsonObject { ["p_character"] = 1 }, notFound));
        Assert.False(args.Learn("ml_plugin_inbox", new JsonObject { ["p_known"] = new JsonArray() }, SiteRpc.Read(System.Net.HttpStatusCode.InternalServerError, null)));
        Assert.False(args.Older("ml_plugin_inbox"));
        Assert.False(SiteRpc.Read(System.Net.HttpStatusCode.OK, "{}").NotFound);
    }
}
