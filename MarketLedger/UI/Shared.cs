using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using MarketLedger.Core;
using MarketLedger.Session;

namespace MarketLedger.UI;

/// <summary>
/// What both windows share: the fonts (made by the first window, let go with the last), the site's
/// addresses, the words for statuses and numbers, and each helper's tint in the log.
/// </summary>
internal static class Ui
{
    public const string SiteUrl = "https://www.kokos-tools.com/market-ledger";
    public const string TokensUrl = "https://www.kokos-tools.com/market-ledger?settings=market-ledger";

    public static string RunUrl(string id) => $"https://www.kokos-tools.com/market-ledger/runs/{Uri.EscapeDataString(id)}";

    // ---------------------------------------------------------------------------------------------
    // Fonts: both windows draw with the same handles, so they're made once.

    private static Fonts? fonts;
    private static int users;

    public static Fonts AcquireFonts()
    {
        fonts ??= new Fonts();
        Txt.Fonts = fonts;
        users++;
        return fonts;
    }

    public static void ReleaseFonts()
    {
        if (users == 0) return;
        if (--users > 0) return;
        fonts?.Dispose();
        fonts = null;
    }

    // ---------------------------------------------------------------------------------------------
    // Numbers, always in the invariant culture ("2,481,930"), as the site writes them.

    public static string Gil(long gil) => gil.ToString("N0", CultureInfo.InvariantCulture);

    public static string Count(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"1:04:12", or "4:05" under an hour.</summary>
    public static string Clock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var h = (int)t.TotalHours;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:00}");
    }

    /// <summary>"about 40 min", "about 1 h 20 min", for estimates.</summary>
    public static string About(double minutes)
    {
        if (double.IsNaN(minutes) || minutes <= 0) return "a moment";
        var m = (int)Math.Round(minutes);
        if (m < 1) return "under a minute";
        if (m < 60) return $"about {m} min";
        var h = m / 60;
        var rest = m % 60;
        return rest == 0 ? $"about {h} h" : $"about {h} h {rest} min";
    }

    /// <summary>"07:51" today, "Mon 07:51" this week, else "3 Nov".</summary>
    public static string When(DateTime utc)
    {
        var local = utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        var now = DateTime.Now;
        if (local.Date == now.Date) return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (now - local < TimeSpan.FromDays(6)) return local.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
        return local.ToString("d MMM", CultureInfo.InvariantCulture);
    }

    /// <summary>"40 min", "1:20" (hours and minutes), for what's left.</summary>
    public static string Minutes(long m) => m < 1 ? "<1 min" : m < 60 ? $"{m} min" : string.Create(CultureInfo.InvariantCulture, $"{m / 60}:{m % 60:00}");

    public static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{Count(n)} {many}";

    // ---------------------------------------------------------------------------------------------
    // Statuses.

    public static string StatusWord(RunStatus s) => s switch
    {
        RunStatus.Draft => "Draft",
        RunStatus.Sent => "Sent",
        RunStatus.Accepted => "Ready",
        RunStatus.Running => "Running",
        RunStatus.Paused => "Paused",
        RunStatus.Waiting => "Waiting",
        RunStatus.Done => "Done",
        RunStatus.Stopped => "Stopped",
        RunStatus.Failed => "Failed",
        _ => "—",
    };

    public static Tone StatusTone(RunStatus s) => s switch
    {
        RunStatus.Running => Tone.Live,
        RunStatus.Paused or RunStatus.Waiting or RunStatus.Accepted => Tone.Waiting,
        RunStatus.Done => Tone.Success,
        RunStatus.Failed => Tone.Danger,
        _ => Tone.Neutral,
    };

    /// <summary>A run that's still going (not finished one way or another).</summary>
    public static bool Going(RunStatus s) => s is RunStatus.Accepted or RunStatus.Running or RunStatus.Paused or RunStatus.Waiting;

    public static string StateWord(StepState s) => s switch
    {
        StepState.Running => "Running",
        StepState.Done => "Done",
        StepState.Skipped => "Skipped",
        StepState.Failed => "Failed",
        _ => "Waiting",
    };

    public static Vector4 StateColor(StepState s) => s switch
    {
        StepState.Running => Palette.GoldSoft,
        StepState.Done => Palette.Gain,
        StepState.Failed => Palette.Loss,
        _ => Palette.Muted,
    };

    public static Icon KindIcon(string? kind) => kind switch
    {
        "withdraw" => Icon.ArrowDownToLine,
        "vendor" => Icon.Store,
        "market" => Icon.ShoppingCart,
        "gather" => Icon.Pickaxe,
        "craft" => Icon.Hammer,
        "entrust" => Icon.ArrowUpFromLine,
        _ => Icon.Coins,
    };

    // ---------------------------------------------------------------------------------------------
    // Each helper's tint in the log (the mockup's), Market Ledger's own in gold.

    private static readonly Vector4 TintGbr = Palette.Hex(0x7fc8ff);
    private static readonly Vector4 TintLifestream = Palette.Hex(0xc9a6ff);
    private static readonly Vector4 TintAutoRetainer = Palette.Hex(0x9be7c4);
    private static readonly Vector4 TintArtisan = Palette.Hex(0xffb38a);
    private static readonly Vector4 TintNav = Palette.Hex(0xa6d8ff);
    private static readonly Vector4 TintAllagan = Palette.Hex(0xe6a6ff);

    public static Vector4 Tint(string? who)
    {
        if (string.IsNullOrEmpty(who)) return Palette.Muted;
        if (who.StartsWith("GatherBuddy", StringComparison.OrdinalIgnoreCase) || who.Equals("GBR", StringComparison.OrdinalIgnoreCase)) return TintGbr;
        if (who.StartsWith("Lifestream", StringComparison.OrdinalIgnoreCase)) return TintLifestream;
        if (who.StartsWith("AutoRetainer", StringComparison.OrdinalIgnoreCase)) return TintAutoRetainer;
        if (who.StartsWith("Artisan", StringComparison.OrdinalIgnoreCase)) return TintArtisan;
        if (who.StartsWith("vnav", StringComparison.OrdinalIgnoreCase)) return TintNav;
        if (who.StartsWith("Allagan", StringComparison.OrdinalIgnoreCase)) return TintAllagan;
        return Palette.GoldSoft;
    }

    // ---------------------------------------------------------------------------------------------
    // The run's numbers, worked out the same way in both windows.

    /// <summary>How far the whole run is: each finished step counts whole, the one under way by its count.</summary>
    public static float Fraction(RunState run)
    {
        var steps = run.Steps;
        if (steps.Count == 0) return 0;
        var sum = 0f;
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            if (s.State is StepState.Done or StepState.Skipped) sum += 1;
            else if (s.Of > 0) sum += Math.Clamp((float)s.Done / s.Of, 0, 1);
        }
        return sum / steps.Count;
    }

    /// <summary>How many steps are behind it (done or skipped).</summary>
    public static int StepsDone(RunState run)
    {
        var n = 0;
        var steps = run.Steps;
        for (var i = 0; i < steps.Count; i++)
            if (steps[i].State is StepState.Done or StepState.Skipped) n++;
        return n;
    }

    public static TimeSpan Elapsed(RunState run) =>
        run.StartedAt is { } at ? DateTime.UtcNow - (at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : at) : TimeSpan.Zero;

    public static StepProgress? StepNow(RunState run) =>
        run.StepIndex >= 0 && run.StepIndex < run.Steps.Count ? run.Steps[run.StepIndex] : null;
}

/// <summary>
/// A string worked out again only when what it shows changes (a clock each second, a count when it
/// moves), so a frame doesn't make new strings for numbers that stand still.
/// </summary>
internal sealed class Memo<T> where T : IEquatable<T>
{
    private T? key;
    private string? text;

    public string Get(T value, Func<T, string> make)
    {
        if (text != null && Same(value)) return text;
        key = value;
        return text = make(value);
    }

    /// <summary>As <see cref="Get(T, Func{T, string})"/>, with what the words need passed in, so the lambda can be static (no closure each frame).</summary>
    public string Get<TState>(T value, TState state, Func<T, TState, string> make)
    {
        if (text != null && Same(value)) return text;
        key = value;
        return text = make(value, state);
    }

    private bool Same(T value) => key is { } k && k.Equals(value);
}

/// <summary>ImGui ids with a number in them ("##inbox-start3"), made once and kept, not every frame.</summary>
internal static class Ids
{
    private static readonly Dictionary<(string Prefix, int N), string> Made = new();

    public static string Of(string prefix, int n)
    {
        if (Made.TryGetValue((prefix, n), out var id)) return id;
        if (Made.Count > 4096) Made.Clear();
        return Made[(prefix, n)] = prefix + n.ToString(CultureInfo.InvariantCulture);
    }
}
