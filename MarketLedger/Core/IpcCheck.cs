using System;
using System.Collections.Generic;
using System.Globalization;

namespace MarketLedger.Session
{
    /// <summary>How well a helper's IPC answered when last checked.</summary>
    public enum HelperIpc
    {
        /// <summary>Not at all (not installed, not loaded, or every call failed).</summary>
        No,

        /// <summary>Some calls answered and some didn't (another version, or a part it doesn't have).</summary>
        Partly,

        Ok,
    }
}

namespace MarketLedger.Core
{
    using MarketLedger.Session;

    /// <summary>
    /// Calling another plugin, wrapped: a call that isn't there (not installed, another version's
    /// signature) or throws means "can't", and each call's first failure is logged once, not every
    /// frame. Pure (the logging is handed in), so it's tested.
    /// </summary>
    public sealed class IpcGuard
    {
        private readonly string plugin;
        private readonly Action<Exception, string> log;
        private readonly HashSet<string> failed = new(StringComparer.Ordinal);

        public IpcGuard(string plugin, Action<Exception, string> log)
        {
            this.plugin = plugin;
            this.log = log;
        }

        /// <summary>The calls that have failed since the last <see cref="Reset"/>.</summary>
        public IReadOnlyCollection<string> Failed => failed;

        /// <summary>Calls it; on any throw, <paramref name="fallback"/> (and the first throw of each call is logged).</summary>
        public T Call<T>(string what, Func<T> call, T fallback)
        {
            try
            {
                var v = call();
                return v;
            }
            catch (Exception ex)
            {
                if (failed.Add(what)) log(ex, $"{plugin} didn't answer {what}");
                return fallback;
            }
        }

        /// <summary>An action: true when it went, false when it couldn't be called.</summary>
        public bool Do(string what, Action call) => Call(what, () =>
        {
            call();
            return true;
        }, false);

        /// <summary>Forget the failures (a "Check again", or the plugin was installed meanwhile): logged again next time.</summary>
        public void Reset() => failed.Clear();
    }

    /// <summary>Judging a helper from its checks. Pure, so it's tested.</summary>
    public static class HelperCheck
    {
        /// <summary>How its IPC did: none of <paramref name="asked"/> answered, some, or all.</summary>
        public static HelperIpc Judge(bool found, int answered, int asked) =>
            !found || asked <= 0 || answered <= 0 ? HelperIpc.No : answered < asked ? HelperIpc.Partly : HelperIpc.Ok;

        /// <summary>A version as Dalamud says it ("1.2.3.4"), and whether it's at least <paramref name="least"/>; an unreadable one isn't.</summary>
        public static bool AtLeast(string? version, string least) =>
            Version.TryParse(version, out var v) && Version.TryParse(least, out var l) && v >= l;

        /// <summary>"4.0.5.21 · IPC ok", "found · partly", "not installed": the window's Found column.</summary>
        public static string Words(bool found, string? version, HelperIpc ipc)
        {
            if (!found) return "not installed";
            var ipcWords = ipc switch
            {
                HelperIpc.Ok => "IPC ok",
                HelperIpc.Partly => "partly",
                _ => "not answering",
            };
            return $"{(string.IsNullOrEmpty(version) ? "found" : version)} · {ipcWords}";
        }

        /// <summary>Reads a version number from what a plugin's IPC returned (an int, a string): null when it isn't one.</summary>
        public static int? IpcVersion(object? answer) => answer switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }
}
