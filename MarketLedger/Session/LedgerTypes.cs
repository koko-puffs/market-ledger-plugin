using System;
using System.Collections.Generic;
using MarketLedger.Core;

namespace MarketLedger.Session;

/// <summary>The link to the site: not linked, linking (the first hello on its way), linked to an account, or refused.</summary>
public abstract record LinkState
{
    private LinkState()
    {
    }

    public sealed record Unlinked : LinkState;

    public sealed record Linking : LinkState;

    public sealed record Linked(string Account) : LinkState;

    /// <summary>The site said no, in words for players ("Link again in Settings with a new one.").</summary>
    public sealed record Refused(string Why) : LinkState;

    public bool IsLinked => this is Linked;
}

/// <summary>A run sent from the site and not started.</summary>
/// <param name="Later">You said "later" to it: it waits until after the others.</param>
public sealed record WaitingRun(Guid Id, string Name, DateTime SentAt, RunFile File, bool Later);

/// <summary>One step of the run in hand, for the window: its progress, and who does it.</summary>
/// <param name="Done">Of the step's own amount, as your bags say it.</param>
/// <param name="Label">"Gather Ra'Kaznar Ore and 1 more".</param>
/// <param name="Kind">The step's kind as the run file writes it: "withdraw", "vendor", "market", "gather", "craft", "entrust".</param>
/// <param name="By">The helper doing it: "Artisan", "GatherBuddy Reborn", "Market Ledger"…</param>
public sealed record StepProgress(string Id, StepState State, int Done, int Of, string? Note, string Label, string Kind, string By);

/// <summary>One line of the run's log (<see cref="At"/> in UTC).</summary>
public sealed record LogLine(DateTime At, string Who, string Text);

/// <summary>
/// The run in hand, as the window shows it: made afresh by <see cref="LedgerSession"/> when something
/// changes (read it each frame; don't keep it).
/// </summary>
public sealed class RunState
{
    public required RunFile File { get; init; }
    public required RunStatus Status { get; init; }

    /// <summary>The step it's on (an index into the file's steps and <see cref="Steps"/>).</summary>
    public required int StepIndex { get; init; }

    public required IReadOnlyList<StepProgress> Steps { get; init; }

    /// <summary>Gil spent so far (vendors and the market).</summary>
    public required long Spent { get; init; }

    /// <summary>When it started (UTC), or null before it has.</summary>
    public DateTime? StartedAt { get; init; }

    /// <summary>The newest lines first.</summary>
    public required IReadOnlyList<LogLine> Log { get; init; }

    /// <summary>Why it stopped or paused, or what it's waiting for, in words for players.</summary>
    public string? Message { get; init; }

    /// <summary>What's happening now: "Gathering Ra'Kaznar Ore · GatherBuddy Reborn".</summary>
    public string? Doing { get; init; }

    /// <summary>About how long is left, from the site's estimate and how far it's got (null when not known).</summary>
    public TimeSpan? Left { get; init; }

    /// <summary>It's holding for you: a click not yet seen working, or something to do by hand. Resume carries on.</summary>
    public bool HeldForYou { get; init; }

    /// <summary>
    /// Text for you to copy, when the step needs it (GatherBuddy Reborn's list, when its IPC can't take
    /// one: paste it with its Auto-Gather tab's "Import from TeamCraft"), or null.
    /// </summary>
    public string? CopyText { get; init; }

    /// <summary>When the site sent it (null when not known: a run picked up again after loading).</summary>
    public DateTime? SentAt { get; init; }
}

/// <summary>
/// One of the plugins Market Ledger asks to do the work, as last checked.
/// </summary>
/// <param name="Needed">A run can't go without it (Artisan, GatherBuddy Reborn, vnavmesh, Lifestream); false: optional.</param>
/// <param name="What">What Market Ledger asks of it, in a few words.</param>
/// <param name="Note">Anything worth knowing ("Lists over IPC aren't upstream yet: …"), or null.</param>
public sealed record HelperStatus(string Name, bool Needed, bool Found, string? Version, HelperIpc Ipc, string What, string? Note)
{
    /// <summary>Its internal name in Dalamud's list.</summary>
    public string InternalName { get; init; } = string.Empty;

    /// <summary>Where to get it.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>The IPC names it was asked about.</summary>
    public string Calls { get; init; } = string.Empty;
}

/// <summary>The character you're playing, as the plugin last read it.</summary>
public sealed record CharacterSnapshot(string Name, string World, long? Gil, int? FreeSlots)
{
    /// <summary>The site's key for it (see <see cref="SiteLink.CharacterKey"/>).</summary>
    public string Key { get; init; } = string.Empty;

    public string DataCentre { get; init; } = string.Empty;

    /// <summary>The crafting and gathering jobs' levels, by abbreviation ("CRP" → 100).</summary>
    public IReadOnlyDictionary<string, int> Jobs { get; init; } = new Dictionary<string, int>();
}
