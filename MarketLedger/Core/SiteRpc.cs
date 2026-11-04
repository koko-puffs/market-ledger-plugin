using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MarketLedger.Core;

/// <summary>
/// What a call to one of the site's database functions came back with: its answer (the JSON the
/// function returned) or why it didn't go, in words for players.
/// </summary>
/// <param name="Hint">The site's own hint on a refusal (<c>rate</c>, <c>run</c>…), or null.</param>
/// <param name="TokenRefused">The site doesn't know the token: link again.</param>
/// <param name="Unreachable">It never got an answer (no connection, the wait ran out): worth trying again later.</param>
/// <param name="NotFound">The site has no such function, or none taking those arguments (an older site: a newer
/// argument can be left out and the call tried again).</param>
public sealed record RpcResult(bool Ok, string? Body, string? Why, string? Hint = null, bool TokenRefused = false, bool Unreachable = false, bool NotFound = false)
{
    public static RpcResult Stopped { get; } = new(false, null, "Stopped.");
}

/// <summary>
/// The one way the plugin calls the site: a POST to a database function (<c>ml_plugin_…</c>) with
/// the site's publishable key, the token in the body (never in the address or a header), a wait
/// that runs out, and a stop when the plugin unloads. A refusal is shown in the site's own words
/// only when it hints that they're written for players; anything else (the database's own words)
/// becomes a short line on what happened. Never throws. The token is never logged.
/// </summary>
public static class SiteRpc
{
    /// <summary>The refusals the site words for players (its hints), besides <c>token</c>.</summary>
    private static readonly string[] Hints = { "off", "rate", "size", "run", "data" };

    /// <summary>The site's functions for the plugin, and only those, are ever called.</summary>
    private static readonly Regex Function = new(@"^ml_plugin_[a-z_]{1,40}$", RegexOptions.CultureInvariant);

    /// <summary>How long a small call waits for the site.</summary>
    public static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(15);

    /// <summary>Does the site word refusals with this hint for players?</summary>
    public static bool Worded(string? hint) => hint != null && Array.IndexOf(Hints, hint) >= 0;

    /// <summary>A call's arguments, the token first (<c>p_token</c>).</summary>
    public static JsonObject Args(string token) => new() { ["p_token"] = token };

    /// <summary>Reads the site's answer: the function's JSON on success, else its refusal in words (see <see cref="SiteRpc"/>).</summary>
    public static RpcResult Read(HttpStatusCode status, string? body)
    {
        var code = (int)status;
        if (code is >= 200 and < 300) return new RpcResult(true, body ?? string.Empty, null);
        JsonNode? node = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(body)) node = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            // Not JSON (a proxy's page): said by the status below.
        }
        string? message = null, hint = null;
        if (node is JsonObject o)
        {
            message = Text(o["message"]);
            hint = Text(o["hint"]);
        }
        if (hint == "token")
            return new RpcResult(false, null, (message is { Length: <= 300 } ? message : "The site doesn't know this token.") + " Link again in Settings with a new one.", hint, true);
        if (Worded(hint) && message is { Length: <= 300 })
            return new RpcResult(false, null, message, hint);
        var why = code switch
        {
            401 or 403 => "The site turned the plugin away. It may need updating.",
            404 => "The site can't do that for the plugin yet. The plugin may need updating.",
            413 => "That's too big for the site.",
            429 => "The site is busy. Try again in a minute.",
            >= 500 => $"The site had a problem ({code}). Try again in a minute.",
            _ => $"The site said no ({code}).",
        };
        return new RpcResult(false, null, why, Worded(hint) ? hint : null, Unreachable: code is 429 or >= 500, NotFound: code == 404);
    }

    /// <summary>
    /// Posts a body (JSON text, the token inside it) to a function. Throws what HttpClient throws; for
    /// callers that word it themselves.
    /// </summary>
    internal static async Task<(HttpStatusCode Status, string Text)> PostAsync(HttpClient http, string function, string body, CancellationToken cancel)
    {
        if (!Function.IsMatch(function)) throw new ArgumentException("Not one of the site's functions for the plugin.", nameof(function));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{SiteLink.SupabaseUrl}/rest/v1/rpc/{function}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("apikey", SiteLink.PublishableKey);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        return (response.StatusCode, text);
    }

    /// <summary>
    /// Calls a function with its arguments (which hold the token as <c>p_token</c>) and says how it
    /// went. Never throws; a token that isn't one never leaves.
    /// </summary>
    public static async Task<RpcResult> CallAsync(HttpClient http, string function, JsonObject args, TimeSpan timeout, CancellationToken cancel)
    {
        var token = args["p_token"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : null;
        if (!SiteLink.LooksRight(token)) return new RpcResult(false, null, "Link your kokos-tools.com account in Settings first.", "token", true);
        if (cancel.IsCancellationRequested) return RpcResult.Stopped;
        string body;
        try
        {
            body = args.ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return new RpcResult(false, null, "That couldn't be written down to send.");
        }
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            wait.CancelAfter(timeout);
            var (status, text) = await PostAsync(http, function, body, wait.Token).ConfigureAwait(false);
            return Read(status, text);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return new RpcResult(false, null, "The site didn't answer in time.", Unreachable: true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return RpcResult.Stopped;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new RpcResult(false, null, "Couldn't reach the site. Check your connection.", Unreachable: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new RpcResult(false, null, "That couldn't be sent.");
        }
    }

    private static string? Text(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}

/// <summary>
/// The plugin's connection to the site, made once and shared: one HttpClient, the account's token,
/// and a stop for everything under way when the plugin unloads. Every call runs on a worker, so the
/// game never waits for the site.
/// </summary>
public sealed class SiteSession : IDisposable
{
    /// <summary>The most an answer may be: an inbox of runs is well under this, and nothing bigger is read into memory.</summary>
    public const long MaxAnswer = 8 * 1024 * 1024;

    private readonly HttpClient http = new() { MaxResponseContentBufferSize = MaxAnswer };
    private readonly CancellationTokenSource closing = new();

    /// <summary>Taken once, so it's still read after the source is disposed (asking the source for it then throws).</summary>
    private readonly CancellationToken closingToken;
    private readonly Func<string> token;

    public SiteSession(Func<string> token)
    {
        this.token = token;
        closingToken = closing.Token;
    }

    public HttpClient Http => http;

    /// <summary>Cancelled when the plugin unloads: what's under way stops, and nothing after it runs.</summary>
    public CancellationToken Closing => closingToken;

    public bool Closed => closingToken.IsCancellationRequested;

    public bool Linked => SiteLink.LooksRight(token());

    /// <summary>The current token (read on the caller's thread, so a worker never reads the settings).</summary>
    public string Token => token();

    /// <summary>
    /// Calls one of the site's functions on a worker, with the token added. <paramref name="args"/>
    /// holds the rest (<c>p_…</c>); it's not touched after this returns.
    /// </summary>
    public Task<RpcResult> Call(string function, JsonObject? args = null, TimeSpan? timeout = null)
    {
        if (Closed) return Task.FromResult(RpcResult.Stopped);
        var all = SiteRpc.Args(Token);
        if (args != null)
            foreach (var (key, value) in args)
                if (key != "p_token") all[key] = value?.DeepClone();
        var cancel = closingToken;
        var wait = timeout ?? SiteRpc.ShortWait;
        return Task.Run(() => SiteRpc.CallAsync(http, function, all, wait, cancel));
    }

    public void Dispose()
    {
        try
        {
            closing.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Unloaded twice.
        }
        http.Dispose();
        closing.Dispose();
    }
}
