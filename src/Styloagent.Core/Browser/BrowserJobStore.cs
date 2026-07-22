using System.Text.Json;

namespace Styloagent.Core.Browser;

/// <summary>Durable one-JSON-file-per-job store. Replacements are atomic within one filesystem.</summary>
public sealed class BrowserJobStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _root;

    public BrowserJobStore(string root) => _root = root;
    public string ArtifactsRoot => Path.Combine(_root, "artifacts");
    private string JobsRoot => Path.Combine(_root, "jobs");

    public BrowserJob Create(string requester, string environment, BrowserRunMode mode, string purpose,
        string relativePath, string? selector, bool fullPage, string? credentialRef, DateTimeOffset now)
    {
        var id = $"{now.UtcDateTime:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}";
        var job = new BrowserJob(id, requester, environment, mode, purpose, relativePath, selector, fullPage,
            credentialRef, BrowserJobStatus.Pending, null, null, null, now.ToUniversalTime(), now.ToUniversalTime());
        Write(job);
        return job;
    }

    public BrowserJob? Read(string id)
    {
        if (!SafeId(id)) return null;
        try
        {
            var path = Path.Combine(JobsRoot, id + ".json");
            return File.Exists(path) ? JsonSerializer.Deserialize<BrowserJob>(File.ReadAllText(path), Json) : null;
        }
        catch { return null; }
    }

    public IReadOnlyList<BrowserJob> ReadAll()
    {
        if (!Directory.Exists(JobsRoot)) return Array.Empty<BrowserJob>();
        var jobs = new List<BrowserJob>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(JobsRoot, "*.json"))
            {
                try
                {
                    var job = JsonSerializer.Deserialize<BrowserJob>(File.ReadAllText(path), Json);
                    if (job is not null) jobs.Add(job);
                }
                catch { }
            }
        }
        catch { }
        return jobs.OrderByDescending(j => j.RequestedAt).ThenBy(j => j.Id, StringComparer.Ordinal).ToList();
    }

    public void Write(BrowserJob job)
    {
        if (!SafeId(job.Id)) throw new ArgumentException("Invalid browser job id.", nameof(job));
        Directory.CreateDirectory(JobsRoot);
        var path = Path.Combine(JobsRoot, job.Id + ".json");
        var temp = Path.Combine(JobsRoot, "." + job.Id + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(job, Json));
        File.Move(temp, path, true);
    }

    private static bool SafeId(string? id) => !string.IsNullOrWhiteSpace(id) &&
        id.Length <= 128 && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
}
