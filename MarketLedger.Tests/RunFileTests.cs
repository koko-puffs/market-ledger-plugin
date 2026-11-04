using System.Text.Json.Nodes;
using MarketLedger.Core;
using Xunit;

namespace MarketLedger.Tests;

public class RunFileTests
{
    /// <summary>A run as the site writes it (kokos-tools, model/run.ts's buildRun): every kind of step.</summary>
    public const string Sample = """
    {
      "format": "market-ledger-run",
      "version": 1,
      "id": "6f1c2a5e-3b4d-4e8f-9a1b-2c3d4e5f6a7b",
      "name": "Gemdraughts for the weekend",
      "createdAt": "2026-11-04T07:51:00.000Z",
      "character": "0123456789abcdef0123456789abcdef",
      "targets": [{ "item": 44162, "amount": 60, "hq": true }],
      "maxGil": 52000,
      "steps": [
        { "id": "s1", "kind": "withdraw", "lines": [
          { "item": 5, "amount": 240, "need": 300, "retainer": "Mimi" },
          { "item": 44030, "amount": 12, "need": 12, "retainer": "Saddlebag" } ] },
        { "id": "s2", "kind": "vendor", "lines": [
          { "item": 5509, "amount": 20, "need": 20, "price": 120, "npc": "Material Supplier", "zone": "Mist" } ] },
        { "id": "s3", "kind": "market", "lines": [
          { "item": 44051, "amount": 30, "need": 30, "maxPrice": 1150, "world": "Odin" },
          { "item": 44052, "amount": 6, "need": 6, "maxPrice": 900, "world": null, "hq": true } ] },
        { "id": "s4", "kind": "gather", "lines": [
          { "item": 43976, "amount": 280, "need": 280, "job": "MIN", "zone": "Yak T'el" } ] },
        { "id": "s5", "kind": "gather", "lines": [
          { "item": 43977, "amount": 10, "need": 10, "job": "BTN", "zone": "Kozama'uka", "timed": { "spawns": [4, 16], "duration": 120 } } ] },
        { "id": "s6", "kind": "craft", "recipe": 35500, "job": "BSM", "crafts": 14, "line": { "item": 43900, "amount": 42, "need": 42 } },
        { "id": "s7", "kind": "craft", "recipe": 35580, "job": "ALC", "crafts": 20, "line": { "item": 44162, "amount": 60, "need": 60, "hq": true } },
        { "id": "s8", "kind": "entrust", "lines": [ { "item": 44162, "amount": 60, "need": 0, "hq": true } ] }
      ],
      "estimate": { "sellFor": 420000, "spend": 41200, "tax": 21000, "gathered": 0, "profit": 357800, "minutes": 95.5 }
    }
    """;

    [Fact]
    public void ReadsEveryKindOfStep()
    {
        var run = RunFile.Parse(Sample);
        Assert.Equal("6f1c2a5e-3b4d-4e8f-9a1b-2c3d4e5f6a7b", run.Id);
        Assert.Equal(new Guid("6f1c2a5e-3b4d-4e8f-9a1b-2c3d4e5f6a7b"), run.Guid);
        Assert.Equal("Gemdraughts for the weekend", run.Name);
        Assert.Equal(52000, run.MaxGil);
        Assert.Equal(8, run.Steps.Count);
        Assert.Equal(new[] { RunStepKind.Withdraw, RunStepKind.Vendor, RunStepKind.Market, RunStepKind.Gather, RunStepKind.Gather, RunStepKind.Craft, RunStepKind.Craft, RunStepKind.Entrust },
            run.Steps.Select(s => s.Kind));
        Assert.Equal("Mimi", run.Steps[0].Lines[0].Retainer);
        Assert.Equal("Saddlebag", run.Steps[0].Lines[1].Retainer);
        Assert.Equal(120, run.Steps[1].Lines[0].Price);
        Assert.Equal("Material Supplier", run.Steps[1].Lines[0].Npc);
        Assert.Equal(1150, run.Steps[2].Lines[0].MaxPrice);
        Assert.Equal("Odin", run.Steps[2].Lines[0].World);
        Assert.Null(run.Steps[2].Lines[1].World);
        Assert.True(run.Steps[2].Lines[1].Hq);
        Assert.False(run.Steps[2].Lines[0].Hq);
        Assert.Null(run.Steps[3].Lines[0].Timed);
        Assert.Equal(new[] { 4, 16 }, run.Steps[4].Lines[0].Timed!.Spawns);
        Assert.Equal(120, run.Steps[4].Lines[0].Timed!.Duration);
        Assert.Equal(35500u, run.Steps[5].Recipe);
        Assert.Equal("BSM", run.Steps[5].Job);
        Assert.Equal(14, run.Steps[5].Crafts);
        Assert.Equal(42, run.Steps[5].Line.Amount);
        Assert.Equal(95.5, run.Estimate.Minutes);
        Assert.Equal(357800, run.Estimate.Profit);
        Assert.Single(run.Targets);
        Assert.True(run.Targets[0].Hq);
    }

    [Fact]
    public void WritesItBackAsTheSiteDoes()
    {
        var run = RunFile.Parse(Sample);
        var json = run.ToJson();
        Assert.Equal("market-ledger-run", json["format"]!.GetValue<string>());
        var craft = json["steps"]![5]!.AsObject();
        Assert.Equal("craft", craft["kind"]!.GetValue<string>());
        Assert.NotNull(craft["line"]);
        Assert.Null(craft["lines"]);
        Assert.Equal(1150, json["steps"]![2]!["lines"]![0]!["maxPrice"]!.GetValue<long>());
        // And reads back the same.
        var again = RunFile.Parse(json.ToJsonString());
        Assert.Equal(run.Steps.Count, again.Steps.Count);
        for (var i = 0; i < run.Steps.Count; i++)
        {
            Assert.Equal(run.Steps[i].Id, again.Steps[i].Id);
            Assert.Equal(run.Steps[i].Kind, again.Steps[i].Kind);
            Assert.Equal(run.Steps[i].Lines.Select(l => (l.Item, l.Amount, l.Need, l.Hq, l.MaxPrice, l.Price)), again.Steps[i].Lines.Select(l => (l.Item, l.Amount, l.Need, l.Hq, l.MaxPrice, l.Price)));
        }
    }

    [Theory]
    [InlineData("\"format\": \"market-ledger-run\"", "\"format\": \"something-else\"", "Not a Market Ledger run")]
    [InlineData("\"version\": 1", "\"version\": 2", "version 2")]
    [InlineData("\"kind\": \"vendor\"", "\"kind\": \"teleport\"", "doesn't know")]
    [InlineData("\"recipe\": 35500", "\"recipe\": 70000", "recipe")]
    [InlineData("\"id\": \"s2\"", "\"id\": \"s1\"", "Two steps")]
    [InlineData("\"maxPrice\": 1150", "\"maxPrice\": 0", "maxPrice")]
    [InlineData("\"retainer\": \"Mimi\"", "\"retainer\": \"\"", "retainer")]
    public void RefusesWhatIsntARun(string from, string to, string says)
    {
        var bad = Sample.Replace(from, to);
        Assert.NotEqual(Sample, bad);
        var ex = Assert.Throws<FormatException>(() => RunFile.Parse(bad));
        Assert.Contains(says, ex.Message);
    }

    [Fact]
    public void RefusesNonsense()
    {
        Assert.Throws<FormatException>(() => RunFile.Parse("not json"));
        Assert.Throws<FormatException>(() => RunFile.Parse("[]"));
        Assert.Throws<FormatException>(() => RunFile.From(null));
    }

    [Fact]
    public void ProgressIsWrittenInTheSitesWords()
    {
        var at = new DateTime(2026, 11, 4, 8, 55, 12, DateTimeKind.Utc);
        var log = Enumerable.Range(0, 50).Select(i => new ProgressLog(at.AddSeconds(-i), "Market Ledger", $"line {i}")).ToList();
        var p = new RunProgress(RunStatus.Waiting, 3,
            new[] { new ProgressStep("s1", StepState.Done, 288, 288), new ProgressStep("s4", StepState.Running, 112, 280, "Gathering") },
            13410, at.AddHours(-1), at, log, "Waiting for a timed node");
        var json = p.ToJson();
        Assert.Equal("waiting", json["status"]!.GetValue<string>());
        Assert.Equal(3, json["step"]!.GetValue<int>());
        Assert.Equal("done", json["steps"]![0]!["state"]!.GetValue<string>());
        Assert.Null(json["steps"]![0]!["note"]);
        Assert.Equal("Gathering", json["steps"]![1]!["note"]!.GetValue<string>());
        Assert.Equal(13410, json["spent"]!.GetValue<long>());
        Assert.Equal("2026-11-04T08:55:12.000Z", json["updatedAt"]!.GetValue<string>());
        Assert.Equal("2026-11-04T07:55:12.000Z", json["startedAt"]!.GetValue<string>());
        Assert.Equal(RunProgress.LogLines, json["log"]!.AsArray().Count);
        Assert.Equal("line 0", json["log"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("Waiting for a timed node", json["message"]!.GetValue<string>());

        var back = RunProgress.From(JsonNode.Parse(json.ToJsonString()));
        Assert.NotNull(back);
        Assert.Equal(RunStatus.Waiting, back!.Status);
        Assert.Equal(2, back.Steps.Count);
        Assert.Equal(at, back.UpdatedAt);
    }

    [Fact]
    public void WordsAreTheSitesExactly()
    {
        Assert.Equal("running", RunWords.Of(RunStatus.Running));
        Assert.Equal(RunCommand.Skip, RunWords.Command("skip"));
        Assert.Null(RunWords.Command("Skip"));
        Assert.Null(RunWords.Status(null));
        Assert.True(RunWords.Finished(RunStatus.Stopped));
        Assert.False(RunWords.Finished(RunStatus.Paused));
    }
}
