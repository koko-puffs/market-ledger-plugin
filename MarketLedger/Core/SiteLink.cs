using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MarketLedger.Core;

/// <summary>
/// The link to Market Ledger on kokos-tools.com: a token made on the site (Settings → Market Ledger →
/// The plugin), "mlp_" and 43 letters, kept in the plugin's settings and never logged. The plugin calls
/// the site's database functions for it (<c>ml_plugin_…</c>, through <see cref="SiteRpc"/>) with the
/// site's publishable key, as the site itself would; the token says whose account it is.
/// </summary>
public static class SiteLink
{
    /// <summary>The site's database (Supabase): its address and its publishable key, public by design.</summary>
    public const string SupabaseUrl = "https://upjcxjuuzxujqddtwehn.supabase.co";

    public const string PublishableKey = "sb_publishable_A-5zpDIk5O6xyKTicipcuA_M3J0sGVP";

    /// <summary>Market Ledger on the site.</summary>
    public const string SiteUrl = "https://www.kokos-tools.com/market-ledger";

    /// <summary>Where a token is made: Settings → Market Ledger → The plugin.</summary>
    public const string TokensUrl = "https://www.kokos-tools.com/market-ledger?settings=market-ledger";

    /// <summary>A run's own page on the site.</summary>
    public static string RunUrl(string id) => $"{SiteUrl}/runs/{Uri.EscapeDataString(id)}";

    public const string Prefix = "mlp_";

    private static readonly Regex Shape = new(@"^mlp_[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant);

    private static readonly Regex Uuid = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant);

    /// <summary>A token as pasted, without the spaces, quotes or line breaks that come with a copy.</summary>
    public static string Clean(string? pasted) => (pasted ?? string.Empty).Trim().Trim('"', '\'', '`').Trim();

    /// <summary>Does it look like a token the site makes ("mlp_" and 43 letters)?</summary>
    public static bool LooksRight(string? token) => token != null && Shape.IsMatch(token);

    /// <summary>A token shown without giving it away: "mlp_••••••••3xQz".</summary>
    public static string Masked(string? token) =>
        string.IsNullOrEmpty(token) ? string.Empty
        : token.Length <= 8 ? new string('•', 8)
        : (token.StartsWith(Prefix, StringComparison.Ordinal) ? Prefix : string.Empty) + new string('•', 8) + token[^4..];

    public static bool IsUuid(string? id) => id != null && Uuid.IsMatch(id);

    /// <summary>
    /// A character's key for the site: the SHA-256 of its content id and this install's own salt, as 32
    /// hex letters. The site never sees the content id, and another install (with another salt) gives
    /// the same character another key.
    /// </summary>
    public static string CharacterKey(ulong contentId, string salt)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"market-ledger:{contentId}:{salt}"));
        return Convert.ToHexString(bytes, 0, 16).ToLowerInvariant();
    }

    /// <summary>A new install's salt: 32 random hex letters.</summary>
    public static string NewSalt() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>An item → count map as the site takes it (<c>Record&lt;number, number&gt;</c>: keys are the ids as text), zeros left out.</summary>
    public static JsonObject Counts(IEnumerable<KeyValuePair<uint, int>> counts)
    {
        var o = new JsonObject();
        foreach (var (id, n) in counts.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key))
            o[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = n;
        return o;
    }
}
