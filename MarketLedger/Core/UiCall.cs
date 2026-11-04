using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MarketLedger.Core;

public enum ArgKind
{
    Int,
    UInt,
    Bool,

    /// <summary>An empty value (the game's "undefined"), as some windows send after their numbers.</summary>
    Empty,
}

/// <summary>One value of a window callback: a number, or a named blank filled in when it's sent ("{index}").</summary>
public readonly record struct UiArg(ArgKind Kind, long Value, string? Blank)
{
    public override string ToString() => Kind == ArgKind.Empty ? "empty" : (Kind switch { ArgKind.UInt => "u", ArgKind.Bool => "b", _ => "i" }) + ":" + (Blank != null ? $"{{{Blank}}}" : Value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// A callback a game window fires when you click something in it (what <c>AtkUnitBase.FireCallback</c>
/// sends to the window's agent), written as data: the window, its values, and whether the window
/// closes. Automation plugins click this way (ECommons' <c>Callback.Fire</c>); the values for each
/// window are in <c>Automation/UiTable.cs</c>, with where each one came from, so they're quick to fix
/// from a recorded log.
/// </summary>
/// <param name="Spec">Values as text, e.g. <c>"i:1 i:{index}"</c> (i int, u uint, b bool; <c>empty</c> for an empty value).</param>
/// <param name="Verified">Seen working in another plugin (true), or a best guess to be confirmed by a recording (false).</param>
public sealed record UiCall(string Addon, string Spec, bool Close, string Source, bool Verified = true)
{
    public IReadOnlyList<UiArg> Args { get; } = Parse(Spec);

    public static IReadOnlyList<UiArg> Parse(string spec)
    {
        var list = new List<UiArg>();
        foreach (var token in spec.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("empty", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new UiArg(ArgKind.Empty, 0, null));
                continue;
            }
            var colon = token.IndexOf(':');
            if (colon <= 0) throw new FormatException($"\"{token}\" isn't a value like i:1 or i:{{index}}.");
            var kind = token[..colon].ToLowerInvariant() switch
            {
                "i" => ArgKind.Int,
                "u" => ArgKind.UInt,
                "b" => ArgKind.Bool,
                _ => throw new FormatException($"\"{token}\": the type must be i, u or b."),
            };
            var raw = token[(colon + 1)..];
            if (raw.StartsWith('{') && raw.EndsWith('}') && raw.Length > 2)
                list.Add(new UiArg(kind, 0, raw[1..^1]));
            else if (kind == ArgKind.Bool && bool.TryParse(raw, out var b))
                list.Add(new UiArg(kind, b ? 1 : 0, null));
            else if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                list.Add(new UiArg(kind, n, null));
            else
                throw new FormatException($"\"{token}\": \"{raw}\" isn't a number.");
        }
        return list;
    }

    /// <summary>The values to send, blanks filled in.</summary>
    public IReadOnlyList<(ArgKind Kind, long Value)> Resolve(IReadOnlyDictionary<string, long>? blanks = null) =>
        Args.Select(a =>
        {
            if (a.Blank == null) return (a.Kind, a.Value);
            if (blanks == null || !blanks.TryGetValue(a.Blank, out var v)) throw new InvalidOperationException($"No value for {{{a.Blank}}} in {Addon}'s callback.");
            return (a.Kind, v);
        }).ToList();

    /// <summary>"Shop ← [i:0, i:14, i:1, empty]": what's being sent, for the log and step-by-step mode.</summary>
    public string Describe(IReadOnlyDictionary<string, long>? blanks = null)
    {
        string Value((ArgKind Kind, long Value) v) => v.Kind == ArgKind.Empty ? "empty" : (v.Kind switch { ArgKind.UInt => "u", ArgKind.Bool => "b", _ => "i" }) + ":" + v.Value.ToString(CultureInfo.InvariantCulture);
        string body;
        try
        {
            body = string.Join(", ", Resolve(blanks).Select(Value));
        }
        catch (InvalidOperationException)
        {
            body = string.Join(", ", Args);
        }
        return $"{Addon} ← [{body}]{(Close ? " (closes)" : string.Empty)}{(Verified ? string.Empty : " (unconfirmed)")}";
    }
}

public static class UiCallValues
{
    public static Dictionary<string, long> Of(params (string Name, long Value)[] values) => values.ToDictionary(v => v.Name, v => v.Value);
}
