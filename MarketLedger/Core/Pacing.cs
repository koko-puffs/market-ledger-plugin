using System;

namespace MarketLedger.Core;

/// <summary>
/// Waiting longer after each failure: first <c>first</c>, doubling up to <c>most</c>, back to none
/// after a success. So a site that's down (or a connection that's gone) isn't asked again every frame.
/// </summary>
public sealed class Backoff
{
    private readonly TimeSpan first;
    private readonly TimeSpan most;
    private TimeSpan wait;
    private DateTime until = DateTime.MinValue;

    public Backoff(TimeSpan first, TimeSpan most)
    {
        this.first = first;
        this.most = most;
    }

    public bool Ready(DateTime now) => now >= until;

    /// <summary>When it's ready again.</summary>
    public DateTime Until => until;

    public void Failed(DateTime now)
    {
        wait = wait == TimeSpan.Zero ? first : TimeSpan.FromTicks(Math.Min(most.Ticks, wait.Ticks * 2));
        until = now + wait;
    }

    public void Succeeded()
    {
        wait = TimeSpan.Zero;
        until = DateTime.MinValue;
    }

    /// <summary>Not before <paramref name="when"/> (the site said "too often"), however short the wait was.</summary>
    public void HoldUntil(DateTime when)
    {
        if (when > until) until = when;
    }
}

/// <summary>
/// One kind of call to the site, paced: at most once every <see cref="Gap"/> (a change waits for the gap
/// to pass, and the last change always goes), again every <see cref="Every"/> anyway when that's set,
/// one call at a time, and less often after failures. Pure: the clock is passed in, so it's tested.
/// </summary>
/// <example>
/// The run's progress: a change sent at most every 2 s, and every 15 s while it runs whether it changed
/// or not (<c>new Pace(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15))</c>).
/// </example>
public sealed class Pace
{
    private readonly Backoff backoff;
    private DateTime lastSent = DateTime.MinValue;
    private bool changed;
    private bool inFlight;

    public Pace(TimeSpan gap, TimeSpan? every = null, TimeSpan? backoffFirst = null, TimeSpan? backoffMost = null)
    {
        Gap = gap;
        Every = every;
        backoff = new Backoff(backoffFirst ?? TimeSpan.FromSeconds(5), backoffMost ?? TimeSpan.FromMinutes(5));
    }

    /// <summary>The least time between two calls.</summary>
    public TimeSpan Gap { get; set; }

    /// <summary>A call this often even with nothing changed (null: only on a change).</summary>
    public TimeSpan? Every { get; set; }

    /// <summary>A call is on its way.</summary>
    public bool Busy => inFlight;

    /// <summary>When the last call went (MinValue: none yet).</summary>
    public DateTime LastSent => lastSent;

    /// <summary>Something changed that the site should hear about.</summary>
    public void Changed() => changed = true;

    /// <summary>Whether to call now; when it says yes, the call counts as on its way until <see cref="Done"/>.</summary>
    public bool Due(DateTime now)
    {
        if (inFlight || !backoff.Ready(now) || now - lastSent < Gap) return false;
        var periodic = Every is { } e && now - lastSent >= e;
        if (!changed && !periodic) return false;
        inFlight = true;
        changed = false;
        lastSent = now;
        return true;
    }

    /// <summary>How the call went. A failure keeps what it carried as changed, so it goes again (after the backoff).</summary>
    public void Done(bool ok, DateTime now)
    {
        inFlight = false;
        if (ok)
        {
            backoff.Succeeded();
            return;
        }
        changed = true;
        backoff.Failed(now);
    }

    /// <summary>The site said "too often" (its <c>rate</c> hint): nothing goes for <paramref name="wait"/>, and what was carried goes after.</summary>
    public void RateLimited(DateTime now, TimeSpan wait)
    {
        inFlight = false;
        changed = true;
        backoff.HoldUntil(now + wait);
    }

    /// <summary>Forget everything (unlinked, or linked again): the next change goes at once.</summary>
    public void Reset()
    {
        inFlight = false;
        changed = false;
        lastSent = DateTime.MinValue;
        backoff.Succeeded();
    }
}
