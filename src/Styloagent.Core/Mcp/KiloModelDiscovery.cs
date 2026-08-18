using System.Diagnostics;
using System.Text.RegularExpressions;
using Styloagent.Core.Sessions;

namespace Styloagent.Core.Mcp;

/// <summary>
/// Dynamically discovers the model catalog from the installed Kilo CLI (<c>kilo models</c>), so the
/// cockpit's <c>agent_capabilities</c> reflects the actual models available on this machine rather than a
/// hard-coded list. The plain (non-verbose) output is one <c>provider/model</c> per line, which is exactly
/// what <c>kilo run --model</c> accepts. Results are cached for <see cref="CacheTtl"/>; a stale cache is
/// refreshed in the background by whoever calls <see cref="RefreshAsync"/>.
/// </summary>
public static class KiloModelDiscovery
{
    // Compatibility-only while callers in the cockpit are removed with the retired runtime.
    private static readonly string[] RetiringRuntimeEfforts = { "default", "low", "medium", "high", "max" };
    /// <summary>How long a discovered catalog stays fresh before the next call re-queries the CLI.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private static readonly Regex ModelLine = new(
        @"^([A-Za-z0-9._~-]+/)+[A-Za-z0-9._~:+-]+$", RegexOptions.Compiled);

    private static readonly object Gate = new();
    private static IReadOnlyList<AgentCapability> _cache = Array.Empty<AgentCapability>();
    private static DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    /// <summary>True when a successful discovery populated the cache.</summary>
    public static bool HasDiscovered { get; private set; }

    /// <summary>
    /// The last discovered catalog (empty when discovery has never succeeded). Safe to call on the UI
    /// thread — it never touches the filesystem/process.
    /// </summary>
    public static IReadOnlyList<AgentCapability> CachedModels()
    {
        lock (Gate) return _cache;
    }

    /// <summary>
    /// Re-runs <c>kilo models</c> now and caches the result. Returns the (possibly empty) catalog.
    /// Best-effort: any failure yields an empty list so callers fall back to static defaults.
    /// </summary>
    public static async Task<IReadOnlyList<AgentCapability>> RefreshAsync(CancellationToken ct = default)
    {
        var models = await DiscoverAsync(ct).ConfigureAwait(false);
        lock (Gate)
        {
            _cache = models;
            _cachedAt = DateTimeOffset.UtcNow;
            HasDiscovered = models.Count > 0;
        }
        return models;
    }

    /// <summary>
    /// Returns the cached catalog, triggering a background refresh when it is stale or never populated.
    /// Callers on a UI thread get the stale/empty list instantly — the process spawn never blocks the UI.
    /// </summary>
    public static IReadOnlyList<AgentCapability> GetOrStartRefresh()
    {
        lock (Gate)
        {
            if (HasDiscovered && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
                return _cache;
        }
        _ = Task.Run(() => RefreshAsync());
        return CachedModels();
    }

    private static async Task<IReadOnlyList<AgentCapability>> DiscoverAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "kilo",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("models");
            psi.Environment["NO_COLOR"] = "1";

            using var proc = Process.Start(psi);
            if (proc is null) return Array.Empty<AgentCapability>();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var stdout = await proc.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (proc.ExitCode != 0) return Array.Empty<AgentCapability>();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var models = new List<AgentCapability>();
            foreach (var raw in stdout.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || !ModelLine.IsMatch(line)) continue;
                if (!seen.Add(line)) continue;

                // "default" is the CLI-default sentinel every runtime advertises; don't let a real model
                // named "default" collide with it (kilo never emits one, but guard anyway).
                if (line.Equals("default", StringComparison.OrdinalIgnoreCase)) continue;

                models.Add(new AgentCapability(line, Humanize(line), AgentRuntimeProfileEfforts()));
            }
            return models;
        }
        catch (OperationCanceledException) { return Array.Empty<AgentCapability>(); }
        catch (InvalidOperationException) { return Array.Empty<AgentCapability>(); }
        catch (System.ComponentModel.Win32Exception) { return Array.Empty<AgentCapability>(); } // kilo not installed
    }

    private static IReadOnlyList<string> AgentRuntimeProfileEfforts()
    {
        // Kept behind a property so this file never hard-codes the effort list twice.
        return RetiringRuntimeEfforts;
    }

    /// <summary>
    /// Turns <c>deepseek/deepseek-v4-pro</c> into a human label: <c>Deepseek V4 Pro</c>. The full id
    /// is kept as the selectable value; only the display label is derived from the last path segment.
    /// </summary>
    private static readonly char[] Separators = { '-', '_', ' ' };

    private static string Humanize(string modelId)
    {
        var slug = modelId;
        int slash = slug.LastIndexOf('/');
        if (slash >= 0 && slash < slug.Length - 1) slug = slug[(slash + 1)..];
        slug = slug.Replace('~', ' ');
        var words = slug.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            words[i] = w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w;
        }
        return string.Join(' ', words);
    }
}
