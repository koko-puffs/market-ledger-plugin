using System;
using System.Collections.Generic;

namespace MarketLedger.Core;

/// <summary>What a step says after it's run once.</summary>
public enum StepStatus
{
    /// <summary>Finished: go to the next step.</summary>
    Done,

    /// <summary>Not yet (waiting for a window, the game…): run me again next frame, until my timeout.</summary>
    Waiting,

    /// <summary>This part can't be done: run the clean-up and go on to the next part (the run step decides what that means).</summary>
    SkipJob,

    /// <summary>Something's wrong: run the clean-up and stop the whole run.</summary>
    Stop,
}

public readonly record struct StepOutcome(StepStatus Status, string? Message = null, ProblemKind Problem = ProblemKind.Other)
{
    public static StepOutcome Done => new(StepStatus.Done);
    public static StepOutcome Waiting => new(StepStatus.Waiting);
    public static StepOutcome Skip(string why, ProblemKind problem = ProblemKind.Other) => new(StepStatus.SkipJob, why, problem);
    public static StepOutcome Halt(string why, ProblemKind problem = ProblemKind.Other) => new(StepStatus.Stop, why, problem);
}

/// <summary>
/// One thing to do or wait for. <see cref="IsAction"/> steps change something in the game (a click,
/// a callback, buying); in step-by-step mode the queue stops before each of them until
/// you press Continue. Waits time out after <see cref="Timeout"/> with <see cref="OnTimeout"/>.
/// </summary>
public sealed class Step
{
    public required string Label { get; init; }
    public required Func<StepOutcome> Run { get; init; }
    public bool IsAction { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// What a timeout means: by default the whole run stops ("an expected window didn't appear");
    /// SkipJob skips the part; Done just goes on to the next step (a wait that may run out).
    /// </summary>
    public StepStatus OnTimeout { get; init; } = StepStatus.Stop;

    /// <summary>What to say when it times out (default: "Timed out: label").</summary>
    public string? TimeoutMessage { get; init; }

    /// <summary>What kind of problem a timeout is, for the problems list.</summary>
    public ProblemKind TimeoutProblem { get; init; } = ProblemKind.Other;

    /// <summary>What kind of step this is ("guess": a click not yet seen working, held for Continue until trusted).</summary>
    public string? Key { get; init; }

    /// <summary>
    /// The least time since the last action before this one (an action only), whatever the pace: the
    /// queue's <see cref="StepQueue.ActionGap"/> when that's longer.
    /// </summary>
    public TimeSpan? MinGap { get; init; }

    /// <summary>A short wait before the step first runs (the game needs a moment after a click).</summary>
    public TimeSpan Delay { get; init; }

    public static Step Wait(string label, Func<bool> until, TimeSpan timeout, StepStatus onTimeout = StepStatus.Stop) =>
        new() { Label = label, Run = () => until() ? StepOutcome.Done : StepOutcome.Waiting, Timeout = timeout, OnTimeout = onTimeout };

    public static Step Pause(string label, TimeSpan delay) =>
        new() { Label = label, Run = () => StepOutcome.Done, Delay = delay, Timeout = delay + TimeSpan.FromSeconds(1) };

    public static Step Act(string label, Func<StepOutcome> run, TimeSpan? delay = null) =>
        new() { Label = label, Run = run, IsAction = true, Delay = delay ?? TimeSpan.Zero };
}

public enum QueueState
{
    Idle,
    Running,

    /// <summary>Step-by-step mode: holding before an action until <see cref="StepQueue.Continue"/>.</summary>
    AwaitingContinue,
    Finished,
    Skipped,
    Stopped,
}

/// <summary>
/// Runs one part of a run step (getting to a board, buying one line…), one per frame at most, the way automation plugins drive game windows
/// (ECommons' TaskManager works the same way): each step either finishes, waits (until its
/// timeout), skips the part or stops everything. When a part ends badly its clean-up steps run
/// (close the menus it opened). Pure: the clock is passed in, so it's tested without the game.
/// </summary>
public sealed class StepQueue
{
    private readonly Func<DateTime> now;
    private readonly List<Step> steps = new();
    private readonly List<Step> cleanup = new();
    private int index;
    private DateTime stepStarted;
    private bool started;
    private bool approved;
    private bool finishEarly;
    private DateTime lastAction = DateTime.MinValue;

    public StepQueue(Func<DateTime> now) => this.now = now;

    /// <summary>When the last action (click) went, on this queue's clock (MinValue: none yet).</summary>
    public DateTime LastActionAt => lastAction;

    public QueueState State { get; private set; } = QueueState.Idle;

    /// <summary>The least time between two actions (clicks), however quickly the waits between them finish.</summary>
    public TimeSpan ActionGap { get; set; }

    /// <summary>Pause before each action until <see cref="Continue"/> is pressed.</summary>
    public bool StepByStep { get; set; }

    /// <summary>Why the part was skipped or the run stopped.</summary>
    public string? Message { get; private set; }

    /// <summary>What kind of problem ended the part, when one did.</summary>
    public ProblemKind Problem { get; private set; }

    public Step? Current => index < steps.Count ? steps[index] : null;

    /// <summary>How long the current step has run, past its delay (zero before it starts).</summary>
    public TimeSpan Elapsed
    {
        get
        {
            if (!started || Current is not { } step) return TimeSpan.Zero;
            var t = now() - stepStarted - step.Delay;
            return t < TimeSpan.Zero ? TimeSpan.Zero : t;
        }
    }

    /// <summary>
    /// A pause of <paramref name="paused"/> is over: the current step's clock (its delay and timeout)
    /// and the gap since the last action carry on from where they were, not counting the pause.
    /// </summary>
    public void Hold(TimeSpan paused)
    {
        if (paused <= TimeSpan.Zero) return;
        if (started) stepStarted += paused;
        if (lastAction != DateTime.MinValue) lastAction += paused;
    }
    public int Index => index;
    public int Count => steps.Count;
    public IReadOnlyList<Step> Steps => steps;

    /// <summary>Called for every step as it starts and as it ends: (step, "start" | "done" | outcome message).</summary>
    public event Action<Step, string>? Trace;

    /// <summary>Called when a step ends, with how long it took from its start (its delay included).</summary>
    public event Action<Step, TimeSpan>? Timed;

    /// <summary>How many steps that finish at once may run in one frame (waits and checks; clicks keep their gap).</summary>
    public int StepsPerTick { get; set; } = 1;

    /// <summary>Starts a new part's steps; <paramref name="cleanupSteps"/> run (best effort, no waiting) if it ends badly.</summary>
    public void Begin(IEnumerable<Step> work, IEnumerable<Step>? cleanupSteps = null)
    {
        steps.Clear();
        steps.AddRange(work);
        cleanup.Clear();
        if (cleanupSteps != null) cleanup.AddRange(cleanupSteps);
        index = 0;
        started = false;
        approved = false;
        finishEarly = false;
        Message = null;
        Problem = ProblemKind.None;
        State = steps.Count == 0 ? QueueState.Finished : QueueState.Running;
    }

    /// <summary>Adds steps right after the current one (a step that finds out more to do).</summary>
    public void InsertNext(IEnumerable<Step> more) => steps.InsertRange(Math.Min(index + 1, steps.Count), more);

    /// <summary>Ends the part successfully once the current step is done (it found there's nothing left to do).</summary>
    public void FinishAfterThis() => finishEarly = true;

    public void Continue()
    {
        if (State != QueueState.AwaitingContinue) return;
        approved = true;
        State = QueueState.Running;
    }

    /// <summary>Ends the part now (Stop pressed), running the clean-up.</summary>
    public void Abort(string why) => End(QueueState.Stopped, why, ProblemKind.Skipped);

    /// <summary>Run once per frame.</summary>
    public void Tick()
    {
        for (var n = 0; n < Math.Max(1, StepsPerTick); n++)
        {
            var before = index;
            TickOnce();
            // Go on in the same frame only after a step finished, and never into a click.
            if (State != QueueState.Running || index == before || index >= steps.Count || steps[index].IsAction) break;
        }
    }

    private void TickOnce()
    {
        if (State != QueueState.Running) return;
        if (index >= steps.Count)
        {
            State = QueueState.Finished;
            return;
        }
        var step = steps[index];
        var t = now();
        if (!started)
        {
            if (step.IsAction && t - lastAction < (step.MinGap is { } gap && gap > ActionGap && ActionGap > TimeSpan.Zero ? gap : ActionGap)) return;
            if (step.IsAction && StepByStep && !approved)
            {
                State = QueueState.AwaitingContinue;
                return;
            }
            started = true;
            stepStarted = t;
            Trace?.Invoke(step, "start");
        }
        if (t - stepStarted < step.Delay) return;

        StepOutcome outcome;
        try
        {
            outcome = step.Run();
        }
        catch (Exception ex)
        {
            outcome = StepOutcome.Halt($"{step.Label}: {ex.Message}");
        }

        switch (outcome.Status)
        {
            case StepStatus.Done:
                if (step.IsAction) lastAction = t;
                Trace?.Invoke(step, "done");
                Timed?.Invoke(step, t - stepStarted);
                index = finishEarly ? steps.Count : index + 1;
                started = false;
                approved = false;
                if (index >= steps.Count) State = QueueState.Finished;
                break;
            case StepStatus.Waiting:
                if (t - stepStarted - step.Delay >= step.Timeout && step.OnTimeout == StepStatus.Done)
                {
                    // A wait that's allowed to run out: go on to the next step.
                    Trace?.Invoke(step, "done");
                    Timed?.Invoke(step, t - stepStarted);
                    index++;
                    started = false;
                    approved = false;
                    if (index >= steps.Count) State = QueueState.Finished;
                }
                else if (t - stepStarted - step.Delay >= step.Timeout)
                {
                    var why = step.TimeoutMessage ?? $"Timed out: {step.Label} (waited {step.Timeout.TotalSeconds:0.#} s).";
                    End(step.OnTimeout == StepStatus.SkipJob ? QueueState.Skipped : QueueState.Stopped, why, step.TimeoutProblem);
                }
                break;
            case StepStatus.SkipJob:
                End(QueueState.Skipped, outcome.Message ?? step.Label, outcome.Problem);
                break;
            default:
                End(QueueState.Stopped, outcome.Message ?? step.Label, outcome.Problem);
                break;
        }
    }

    private void End(QueueState state, string why, ProblemKind problem = ProblemKind.Other)
    {
        if (State is QueueState.Stopped or QueueState.Skipped) return;
        Message = why;
        Problem = problem;
        if (started && index < steps.Count) Timed?.Invoke(steps[index], now() - stepStarted);
        Trace?.Invoke(steps.Count > 0 && index < steps.Count ? steps[index] : new Step { Label = "end", Run = () => StepOutcome.Done }, why);
        foreach (var c in cleanup)
        {
            try
            {
                c.Run();
            }
            catch
            {
                // Clean-up is best effort.
            }
        }
        State = state;
    }
}

/// <summary>What kind of problem ended a part, when one did.</summary>
public enum ProblemKind
{
    None,
    Other,

    /// <summary>Stopped or skipped by you.</summary>
    Skipped,
}
