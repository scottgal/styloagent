using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Styloagent.Core.Channel;
using Styloagent.Core.Hooks;
using Styloagent.Core.Transcripts;

namespace Styloagent.Core.Sessions;

/// <summary>
/// Owns one authenticated OpenCode v2 backend and an independently attached TUI. All automation
/// uses HTTP; WriteAsync is reserved for the operator's keyboard. Native history survives disposal.
/// </summary>
public sealed class OpenCodeSession : IPtySession, INativeAgentSession
{
    private readonly OpenCodeOptions _options;
    private readonly PtySpawnOptions _spawn;
    private readonly IPtyLauncher _launcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _reconcile = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pendingInputs = new(StringComparer.Ordinal);
    private readonly string _directory;
    private readonly string _executable;
    private Process? _server;
    private FileStream? _lease;
    private IPtySession? _terminal;
    private Task? _events;
    private Task? _poll;
    private Task? _stderr;
    private Task? _stdout;
    private int _disposed;
    private int _cols;
    private int _rows;
    private string _initialId = "msg_" + Guid.NewGuid().ToString("N");
    private string? _lastState;
    private bool _connectionInterrupted;
    private string? _lastToolId;
    private DateTimeOffset _catalogAt;
    private IReadOnlyList<OpenCodeModel> _models = [];
    private Dictionary<string, string> _environment = new(StringComparer.Ordinal);

    public string SessionId { get; private set; } = "";
    public string? Model { get; private set; }
    public string? Effort { get; private set; }
    public TranscriptUsage? Usage { get; private set; }
    public string? PendingPermissionId { get; private set; }
    public bool IsIdle { get; private set; } = true;
    public int ProcessId
    {
        get { try { return _server is { HasExited: false } process ? process.Id : 0; } catch (InvalidOperationException) { return 0; } }
    }
    public Uri? ServerUrl => _http.BaseAddress;
    public IPtySession TerminalSession => _terminal?.TerminalSession ?? this;
    private readonly object _outputGate = new();
    private readonly StringBuilder _outputBacklog = new();
    private Action<string>? _output;
    public event Action<string>? Output
    {
        add
        {
            lock (_outputGate)
            {
                _output += value;
                if (_outputBacklog.Length > 0) value?.Invoke(_outputBacklog.ToString());
            }
        }
        remove { lock (_outputGate) _output -= value; }
    }
    public event Action? Exited;
    public event Action<HookEvent>? RuntimeEvent;

    internal OpenCodeSession(OpenCodeOptions options, PtySpawnOptions spawn, IPtyLauncher launcher, HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _options = options;
        _spawn = spawn;
        _launcher = launcher;
        _cols = spawn.Cols;
        _rows = spawn.Rows;
        var identity = Path.GetFullPath(options.RepoRoot) + "\n" + options.Prefix + "\n" + Path.GetFullPath(spawn.WorkingDirectory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        _directory = options.StateDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Styloagent", "opencode", key);
        _executable = ResolveExecutable();
    }

    public static async Task<OpenCodeSession> StartAsync(OpenCodeOptions options, PtySpawnOptions spawn,
        IPtyLauncher launcher, string prompt, string? model, string? effort, CancellationToken ct = default)
    {
        var session = new OpenCodeSession(options, spawn, launcher);
        try { await session.StartCoreAsync(prompt, model, effort, ct).ConfigureAwait(false); return session; }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task StartCoreAsync(string prompt, string? model, string? effort, CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        try { _lease = new FileStream(Path.Combine(_directory, "backend.lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("This OpenCode agent is already running in another cockpit. Close that agent before starting another copy.", ex); }
        LoadState();
        _environment = BuildEnvironment();
        var start = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = _spawn.WorkingDirectory, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var arg in new[] { "serve", "--hostname", "127.0.0.1", "--port", "0", "--stdio" }) start.ArgumentList.Add(arg);
        foreach (var pair in _environment) start.Environment[pair.Key] = pair.Value;
        _server = await Task.Run(() => Process.Start(start) ?? throw new IOException("Could not start OpenCode."), ct).ConfigureAwait(false);
        _stderr = DrainStderrAsync(_server.StandardError);
        using var ready = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ready.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            while (true)
            {
                var line = await _server.StandardOutput.ReadLineAsync(ready.Token).ConfigureAwait(false);
                if (line is null) throw new IOException("OpenCode exited before reporting readiness. Install OpenCode v2 and check its provider configuration.");
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (!doc.RootElement.TryGetProperty("url", out var url)) continue;
                    if (!Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1")
                        throw new IOException("OpenCode did not report a loopback server URL.");
                    _http.BaseAddress = uri;
                    break;
                }
                catch (JsonException) { /* startup diagnostics are not a readiness signal */ }
            }
            _stdout = DrainStderrAsync(_server.StandardOutput);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes("opencode:" + _environment["OPENCODE_SERVER_PASSWORD"])));
            var info = await GetAsync("/api/info", ready.Token).ConfigureAwait(false);
            if (Str(info, "version") is not { } version || !version.StartsWith("2.", StringComparison.Ordinal))
                throw new NotSupportedException("Styloagent requires OpenCode v2. Update OpenCode before launching this agent.");
            do
            {
                await RefreshModelsAsync(ready.Token).ConfigureAwait(false);
                if (_models.Count == 0) await Task.Delay(150, ready.Token).ConfigureAwait(false);
            } while (_models.Count == 0);
            if (_options.McpUrl is not null)
            {
                while (true)
                {
                    var servers = await GetAsync("/api/mcp", ready.Token).ConfigureAwait(false);
                    var fleet = servers.GetProperty("data").EnumerateArray().FirstOrDefault(s => Str(s, "name") == "styloagent");
                    if (fleet.ValueKind != JsonValueKind.Undefined && fleet.TryGetProperty("status", out var status))
                    {
                        var connection = Str(status, "status");
                        if (connection == "connected") break;
                        if (connection is "failed" or "needs-auth" or "disabled")
                            throw new IOException("OpenCode could not connect to the Styloagent MCP server.");
                    }
                    await Task.Delay(150, ready.Token).ConfigureAwait(false);
                }
            }
            if (_options.GateCommand is { Count: > 0 })
            {
                var plugins = await GetAsync("/api/plugin", ready.Token).ConfigureAwait(false);
                var gate = plugins.GetProperty("data").EnumerateArray().FirstOrDefault(p => Str(p, "id") == "styloagent.ownership");
                if (gate.ValueKind == JsonValueKind.Undefined || !gate.TryGetProperty("state", out var state) || Str(state, "status") != "active")
                    throw new IOException("OpenCode did not activate the Styloagent ownership plugin. Check that the installed OpenCode v2 supports local TypeScript plugins.");
            }
            if (SessionId.Length > 0)
            {
                try { await GetAsync($"/api/session/{SessionId}", ready.Token).ConfigureAwait(false); }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                { SessionId = ""; _initialId = "msg_" + Guid.NewGuid().ToString("N"); }
            }
            var resumed = SessionId.Length > 0;
            if (SessionId.Length == 0)
            {
                var selected = SelectModel(model, effort);
                var created = await PostAsync("/api/session", new { title = "Styloagent " + _options.Prefix,
                    model = selected }, ready.Token).ConfigureAwait(false);
                SessionId = created.GetProperty("data").GetProperty("id").GetString()!;
                SaveState(); // identity is durable BEFORE admitting the first prompt
            }
            else
            {
                // Apply the cockpit's default on restore too: older sessions may have inherited an
                // unrelated global OpenCode default. Explicit model selections still take precedence.
                var selected = SelectModel(model, effort);
                await PostAsync($"/api/session/{SessionId}/model", new { model = selected }, ready.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(prompt))
                {
                    _initialId = "msg_" + Guid.NewGuid().ToString("N");
                    SaveState();
                }
            }
            if (!string.IsNullOrWhiteSpace(_options.Instructions))
            {
                using var response = await _http.PutAsJsonAsync($"/api/experimental/session/{SessionId}/instructions/entries/styloagent",
                    new { value = _options.Instructions }, ready.Token).ConfigureAwait(false);
                await ReadAsync(response, ready.Token).ConfigureAwait(false);
            }
            // Snapshot reconciliation also covers events missed while creating or resuming the session.
            await ReconcileAsync(ready.Token).ConfigureAwait(false);
            await AttachTerminalAsync(ready.Token).ConfigureAwait(false);
            _server.EnableRaisingEvents = true;
            _server.Exited += ServerExited;
            _events = EventLoopAsync(_lifetime.Token);
            _poll = PollAsync(_lifetime.Token);
            if (!string.IsNullOrWhiteSpace(prompt))
                await SendAsync(resumed ? "Resume this session using its existing history.\n\n" + prompt : prompt, _initialId, ready.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("OpenCode did not finish startup, model discovery and MCP connection within 45 seconds."); }
    }

    private object SelectModel(string? requested, string? effort)
    {
        OpenCodeModel? selected;
        if (!string.IsNullOrWhiteSpace(requested) && requested != "default")
            selected = _models.FirstOrDefault(m => m.QualifiedId == requested)
                ?? _models.SingleOrDefault(m => m.Id == requested);
        else
        {
            var routing = DeepSeekEnv.Load(_options.RepoRoot);
            routing.TryGetValue("ANTHROPIC_BASE_URL", out var baseUrl);
            selected = OpenCodeModelCatalog.DefaultFlash(_models, DeepSeekModels.IsOpenRouter(baseUrl));
        }
        if (selected is null) throw new ArgumentException($"OpenCode does not advertise model '{requested}'. Select a model from agent_capabilities.");
        var variant = string.IsNullOrWhiteSpace(effort) || effort == "default" ? null : effort;
        if (variant is not null && !selected.Variants.Contains(variant, StringComparer.Ordinal))
            throw new ArgumentException($"OpenCode model {selected.QualifiedId} does not advertise variant '{variant}'.");
        return variant is null ? new { providerID = selected.ProviderId, id = selected.Id } as object
            : new { providerID = selected.ProviderId, id = selected.Id, variant };
    }

    private Dictionary<string, string> BuildEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_spawn.Env is not null) foreach (var pair in _spawn.Env) env[pair.Key] = pair.Value;
        foreach (var (key, folder) in new[] { ("XDG_DATA_HOME", "data"), ("XDG_STATE_HOME", "state"), ("XDG_CACHE_HOME", "cache") })
            env[key] = Path.Combine(_directory, folder);
        // Keep native global config, but isolate the database. Seed provider login credentials only;
        // never copy another agent's history or expose a key in command-line arguments.
        var sharedData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        sharedData = string.IsNullOrWhiteSpace(sharedData) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share") : sharedData;
        var authSource = Path.Combine(sharedData, "opencode", "auth.json");
        var authTarget = Path.Combine(env["XDG_DATA_HOME"], "opencode", "auth.json");
        if (File.Exists(authSource) && (!File.Exists(authTarget) || File.GetLastWriteTimeUtc(authSource) > File.GetLastWriteTimeUtc(authTarget)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(authTarget)!);
            File.Copy(authSource, authTarget, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(authTarget, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        env["PWD"] = Path.GetFullPath(_spawn.WorkingDirectory);
        env["PATH"] = string.Join(Path.PathSeparator, new[] { Path.GetDirectoryName(_executable), "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", Environment.GetEnvironmentVariable("PATH") }.Where(p => !string.IsNullOrEmpty(p)));
        env["OPENCODE_SERVER_PASSWORD"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        env["OPENCODE_SERVER_USERNAME"] = "opencode";
        var routing = DeepSeekEnv.Load(_options.RepoRoot);
        routing.TryGetValue("ANTHROPIC_BASE_URL", out var url);
        if (Uri.TryCreate(url, UriKind.Absolute, out var endpoint) && routing.TryGetValue("ANTHROPIC_AUTH_TOKEN", out var token))
        {
            if (endpoint.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase) && !env.ContainsKey("DEEPSEEK_API_KEY") && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")))
                env["DEEPSEEK_API_KEY"] = token;
            else if (DeepSeekModels.IsOpenRouter(url) && !env.ContainsKey("OPENROUTER_API_KEY") && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
                env["OPENROUTER_API_KEY"] = token;
        }
        var config = new Dictionary<string, object?>();
        if (_options.McpUrl is { } mcp)
            config["mcp"] = new { servers = new Dictionary<string, object> { ["styloagent"] = new
            {
                type = "remote", url = mcp.ToString(), oauth = false, codemode = true,
                headers = new Dictionary<string, string> { ["Authorization"] = "Bearer {env:STYLOAGENT_MCP_TOKEN}",
                    ["X-Styloagent-Agent"] = _options.Prefix, ["X-Styloagent-Repo"] = Path.GetFileName(Path.TrimEndingDirectorySeparator(_options.RepoRoot)) }
            } } };
        var rules = new List<object>();
        if (_options.PermissionMode == FleetPermissionMode.Bypass) rules.Add(new { action = "*", resource = "*", effect = "allow" });
        else
        {
            rules.Add(new { action = "*", resource = "*", effect = "ask" });
            foreach (var action in new[] { "read", "glob", "grep", "skill" }) rules.Add(new { action, resource = "*", effect = "allow" });
            if (_options.PermissionMode == FleetPermissionMode.Scoped)
            {
                rules.Add(new { action = "edit", resource = "*", effect = "allow" });
                foreach (var directory in new[] { _options.RepoRoot, _spawn.WorkingDirectory }.Distinct(StringComparer.Ordinal))
                    rules.Add(new { action = "external_directory", resource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).Replace('\\', '/') + "/*", effect = "allow" });
                rules.Add(new { action = "styloagent_*", resource = "*", effect = "allow" });
                // Code Mode enforces the nested tool's own permissions before executing it.
                rules.Add(new { action = "execute", resource = "*", effect = "allow" });
                rules.Add(new { action = "opencode_list_mcp_resources", resource = "*", effect = "allow" });
                rules.Add(new { action = "opencode_read_mcp_resource", resource = "styloagent:*", effect = "allow" });
            }
        }
        config["permissions"] = rules;
        if (_options.GateCommand is { Count: > 0 } command)
        {
            var plugin = Path.Combine(_directory, "ownership-plugin");
            Directory.CreateDirectory(plugin);
            File.WriteAllText(Path.Combine(plugin, "package.json"), """{"name":"styloagent-ownership","type":"module","exports":"./index.ts"}""");
            File.WriteAllText(Path.Combine(plugin, "index.ts"), OpenCodeOwnershipPlugin.Source(_options.Prefix, _options.RepoRoot, command));
            config["plugins"] = new[] { plugin };
        }
        env["OPENCODE_CONFIG_CONTENT"] = JsonSerializer.Serialize(config);
        return env;
    }

    public async Task SendAsync(string text, string messageId, CancellationToken ct = default)
    {
        await PostAsync($"/api/session/{SessionId}/prompt", new { id = messageId, text, delivery = "steer", resume = true }, ct).ConfigureAwait(false);
    }

    public async Task DeliverAsync(BusMessage message, DeliveryMode mode, CancellationToken ct = default)
    {
        var id = "msg_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_options.RepoRoot + "\n" + _options.Prefix + "\n" + message.FilePath))).ToLowerInvariant();
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                _pending[id] = message.FilePath;
                _pendingInputs[id] = FormatNudge(message);
            }
            SaveState();
            var interrupt = mode == DeliveryMode.Interrupt && !IsIdle;
            await PostAsync($"/api/session/{SessionId}/prompt", new { id, text = FormatNudge(message), delivery = "steer", resume = !interrupt }, ct).ConfigureAwait(false);
            if (interrupt)
            {
                var outcome = await PostAsync($"/api/session/{SessionId}/interrupt?resume=true", null, ct).ConfigureAwait(false);
                // If it became idle during admission, explicitly resume the same idempotent input.
                if (!outcome.GetProperty("interrupted").GetBoolean())
                    await SendAsync(FormatNudge(message), id, ct).ConfigureAwait(false);
            }
        }
        finally { _sendGate.Release(); }
    }

    private string FormatNudge(BusMessage message)
    {
        // Prefer checkout-relative paths: native canonical paths may differ from logical macOS aliases.
        var relative = Path.GetRelativePath(_spawn.WorkingDirectory, message.FilePath);
        var local = relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
        return MessageDelivery.FormatNudge(local ? message with { FilePath = relative } : message);
    }

    public async Task ReplyToPermissionAsync(string decision, CancellationToken ct = default)
    {
        var request = PendingPermissionId ?? throw new InvalidOperationException("This agent has no pending permission request.");
        await PostAsync($"/api/session/{SessionId}/permission/{request}/reply", new { decision }, ct).ConfigureAwait(false);
        await ReconcileAsync(ct).ConfigureAwait(false);
    }

    public async Task InterruptAsync(bool resume, CancellationToken ct = default)
        => await PostAsync($"/api/session/{SessionId}/interrupt?resume={resume.ToString().ToLowerInvariant()}", null, ct).ConfigureAwait(false);

    private async Task AttachTerminalAsync(CancellationToken ct)
    {
        _terminal = await _launcher.SpawnAsync(_spawn with { Command = _executable,
            Args = new[] { "--auto", "--server", _http.BaseAddress!.ToString().TrimEnd('/'), "--session", SessionId, _spawn.WorkingDirectory },
            Env = _environment, Cols = _cols, Rows = _rows }, ct).ConfigureAwait(false);
        _terminal.Output += ForwardOutput;
        _terminal.Exited += TerminalExited;
    }

    private void ForwardOutput(string chunk)
    {
        Action<string>? output;
        lock (_outputGate)
        {
            _outputBacklog.Append(chunk);
            if (_outputBacklog.Length > 256 * 1024) _outputBacklog.Remove(0, _outputBacklog.Length - 256 * 1024);
            output = _output;
        }
        output?.Invoke(chunk);
    }
    private void TerminalExited() => Emit("Notification", "agent_needs_input", "OpenCode terminal detached. The backend is still running; park and resume this agent to reattach.");
    private void ServerExited(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _lifetime.Cancel();
        Emit("SessionEnd", message: "OpenCode backend exited.");
        Exited?.Invoke();
    }

    internal async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await ReconcileAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or TaskCanceledException)
            {
                _lastState = null;
                if (!_connectionInterrupted)
                {
                    _connectionInterrupted = true;
                    Emit("Notification", "connection_interrupted", "OpenCode connection interrupted; recovering session state.");
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task EventLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/event");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
                var data = new StringBuilder();
                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    if (line.StartsWith("data:", StringComparison.Ordinal)) data.Append(line.AsSpan(5).TrimStart());
                    else if (line.Length == 0 && data.Length > 0)
                    {
                        using var document = JsonDocument.Parse(data.ToString());
                        using var decoded = document.RootElement.ValueKind == JsonValueKind.String ? JsonDocument.Parse(document.RootElement.GetString()!) : null;
                        var root = decoded?.RootElement ?? document.RootElement;
                        var type = Str(root, "type") ?? "";
                        if (type is "model.updated" or "provider.updated") _catalogAt = DateTimeOffset.MinValue;
                        // A snapshot covers missed events on every reconnect; deltas only wake reconciliation.
                        if (type == "server.connected" || type.StartsWith("session.execution.", StringComparison.Ordinal) ||
                            type.StartsWith("permission.", StringComparison.Ordinal) || type.StartsWith("form.", StringComparison.Ordinal) || type is "session.inbox.delivered" or "session.step.ended" or "session.tool.called")
                            await ReconcileAsync(ct).ConfigureAwait(false);
                        data.Clear();
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException) { }
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task ReconcileAsync(CancellationToken ct = default, bool force = false)
    {
        await _reconcile.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (force) _lastState = null;
            if (DateTimeOffset.UtcNow - _catalogAt > TimeSpan.FromMinutes(5)) await RefreshModelsAsync(ct).ConfigureAwait(false);
            var session = (await GetAsync($"/api/session/{SessionId}", ct).ConfigureAwait(false)).GetProperty("data");
            if (session.TryGetProperty("model", out var model))
            {
                Model = Str(model, "providerID") + "/" + Str(model, "id");
                Effort = Str(model, "variant") ?? "default";
            }
            var active = (await GetAsync("/api/session/active", ct).ConfigureAwait(false)).GetProperty("data");
            IsIdle = !active.TryGetProperty(SessionId, out _);
            var requests = (await GetAsync($"/api/session/{SessionId}/permission", ct).ConfigureAwait(false)).GetProperty("data");
            var first = requests.EnumerateArray().FirstOrDefault();
            PendingPermissionId = first.ValueKind == JsonValueKind.Undefined ? null : Str(first, "id");
            var forms = (await GetAsync($"/api/session/{SessionId}/form", ct).ConfigureAwait(false)).GetProperty("data");
            var form = forms.EnumerateArray().FirstOrDefault();
            var formId = Str(form, "id");
            var messages = (await GetAsync($"/api/session/{SessionId}/message?order=desc&limit=100", ct).ConfigureAwait(false)).GetProperty("data");
            // Only the latest completed turn can require action for a model error. A null error
            // or an older failed turn must not override a running/new prompt's native status.
            var latest = messages.EnumerateArray().FirstOrDefault(m => Str(m, "type") is "assistant" or "user");
            var modelError = IsIdle && Str(latest, "type") == "assistant"
                && latest.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null
                ? error.GetRawText() : null;
            var state = PendingPermissionId is not null ? "permission:" + PendingPermissionId
                : formId is not null ? "form:" + formId
                : modelError is not null ? "error:" + Str(latest, "id") + ":" + modelError
                : IsIdle ? "idle" : "working";
            if (_lastState != state)
            {
                _lastState = state;
                if (PendingPermissionId is not null) Emit("PermissionRequest", message: "Approve " + Str(first, "action") + ": " + first.GetProperty("resources").GetRawText());
                else if (formId is not null) Emit("Notification", "agent_needs_input", "Answer in the OpenCode terminal: " + Str(form, "title"));
                else if (modelError is not null) Emit("Notification", "agent_needs_input", "OpenCode model error: " + modelError);
                else if (IsIdle) Emit("Notification", "idle_prompt");
                else Emit("UserPromptSubmit");
            }
            _connectionInterrupted = false;
            KeyValuePair<string, string>[] pending;
            lock (_stateGate) pending = _pending.ToArray();
            foreach (var (id, path) in pending)
            {
                JsonElement consumed;
                try
                {
                    consumed = (await GetAsync($"/api/session/{SessionId}/message/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false)).GetProperty("data");
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    await RetryUnadmittedAsync(id, path, ct).ConfigureAwait(false);
                    continue;
                }
                if (Str(consumed, "type") != "user") continue;
                if (_options.HooksDirectory is { } dir) new PendingInbox(dir).MarkDelivered(_options.Prefix, path);
                lock (_stateGate) { _pending.Remove(id); _pendingInputs.Remove(id); }
                SaveState();
            }
            var assistant = messages.EnumerateArray().FirstOrDefault(m => Str(m, "type") == "assistant");
            if (assistant.ValueKind != JsonValueKind.Undefined)
            {
                if (assistant.TryGetProperty("tokens", out var tokens))
                {
                    var cache = tokens.TryGetProperty("cache", out var cached) ? cached : default;
                    var used = Num(tokens, "input") + Num(cache, "read") + Num(cache, "write");
                    var limit = _models.FirstOrDefault(m => m.QualifiedId == Model)?.ContextTokens ?? 0;
                    if (used > 0 && limit > 0) Usage = new(used, limit, Model);
                }
                if (assistant.TryGetProperty("content", out var parts))
                {
                    var tool = parts.EnumerateArray().LastOrDefault(p => Str(p, "type") == "tool");
                    if (!IsIdle && PendingPermissionId is null && formId is null && tool.ValueKind != JsonValueKind.Undefined && Str(tool, "id") is { } id && id != _lastToolId)
                    {
                        _lastToolId = id;
                        Emit("PreToolUse", tool: NormalizeTool(Str(tool, "tool") ?? Str(tool, "name")), target: tool.TryGetProperty("state", out var toolState) && toolState.TryGetProperty("input", out var input) ? ToolTarget(input) : null);
                    }
                }
            }
        }
        finally { _reconcile.Release(); }
    }

    private async Task RetryUnadmittedAsync(string id, string path, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A crash between persisting our receipt and native admission must not strand a message.
            // Existing parked inputs remain parked; only an absent native ID needs re-admission.
            var inbox = (await GetAsync($"/api/session/{SessionId}/inbox", ct).ConfigureAwait(false)).GetProperty("data");
            if (inbox.EnumerateArray().Any(m => Str(m, "id") == id)) return;
            string? text;
            lock (_stateGate) _pendingInputs.TryGetValue(id, out text);
            text ??= FormatNudge(new(id, _options.Prefix, BusMessageKind.Inbox, BusMessageState.New, path, null, "fleet", ""));
            await SendAsync(text, id, ct).ConfigureAwait(false);
        }
        finally { _sendGate.Release(); }
    }

    private async Task RefreshModelsAsync(CancellationToken ct)
    {
        _models = OpenCodeModelCatalog.Parse(await GetAsync("/api/model", ct).ConfigureAwait(false));
        OpenCodeModelCatalog.Update(_options.RepoRoot, _models);
        _catalogAt = DateTimeOffset.UtcNow;
    }

    private void Emit(string name, string? notification = null, string? message = null, string? tool = null, string? target = null)
        => RuntimeEvent?.Invoke(new(_options.HookId, name, notification, message, SessionId, _spawn.WorkingDirectory, tool, target));

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        return await ReadAsync(response, ct).ConfigureAwait(false);
    }
    private async Task<JsonElement> PostAsync(string path, object? body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(path, body, ct).ConfigureAwait(false);
        return await ReadAsync(response, ct).ConfigureAwait(false);
    }
    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"OpenCode API returned {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);
        if (response.StatusCode == HttpStatusCode.NoContent) return default;
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }
    private async Task DrainStderrAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is not null) { } }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private sealed record StoredSession(string SessionId, string InitialId, Dictionary<string, string> Pending, Dictionary<string, string>? Inputs = null);
    private void LoadState()
    {
        var path = Path.Combine(_directory, "session.json");
        if (!File.Exists(path)) return;
        var stored = JsonSerializer.Deserialize<StoredSession>(File.ReadAllText(path)) ?? throw new IOException("OpenCode session state could not be read.");
        SessionId = stored.SessionId;
        _initialId = stored.InitialId;
        foreach (var entry in stored.Pending) _pending[entry.Key] = entry.Value;
        if (stored.Inputs is not null) foreach (var entry in stored.Inputs) _pendingInputs[entry.Key] = entry.Value;
    }
    private void SaveState()
    {
        lock (_stateGate)
        {
            var path = Path.Combine(_directory, "session.json");
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new StoredSession(SessionId, _initialId, new(_pending), new(_pendingInputs))));
            File.Move(temp, path, overwrite: true);
        }
    }

    public ValueTask WriteAsync(string text, CancellationToken ct = default)
        => _terminal?.WriteAsync(text, ct) ?? ValueTask.FromException(new InvalidOperationException("OpenCode's terminal is not attached."));
    public void Resize(int cols, int rows) { _cols = cols; _rows = rows; _terminal?.Resize(cols, rows); }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        if (_server is { HasExited: false } && SessionId.Length > 0)
        {
            // Cancel provider work and native tool children before ending the owned server. Merely
            // closing its terminal or stdin can leave an already-started command executing.
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await InterruptAsync(resume: false, stop.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) { }
        }
        if (_terminal is not null)
        {
            _terminal.Output -= ForwardOutput;
            _terminal.Exited -= TerminalExited;
            try { await _terminal.DisposeAsync().ConfigureAwait(false); } catch (IOException) { }
        }
        if (_server is not null)
        {
            _server.Exited -= ServerExited;
            try
            {
                if (!_server.HasExited)
                {
                    _server.StandardInput.Close();
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await _server.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { _server.Kill(entireProcessTree: true); await _server.WaitForExitAsync().ConfigureAwait(false); }
                }
            }
            catch (InvalidOperationException) { }
        }
        foreach (var task in new[] { _events, _poll, _stderr, _stdout })
            if (task is not null) try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _server?.Dispose();
        _http.Dispose();
        _lease?.Dispose();
        _lifetime.Dispose();
    }

    private static string ResolveExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "opencode.exe" : "opencode";
        var candidates = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"), "/opt/homebrew/bin", "/usr/local/bin" }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        return candidates.Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists) ?? name;
    }
    private static string? Str(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long Num(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    private static string? ToolTarget(JsonElement input) => Str(input, "path") ?? Str(input, "filePath") ?? Str(input, "file_path") ?? Str(input, "command") ?? Str(input, "pattern");
    private static string? NormalizeTool(string? name) => name switch { "read" => "Read", "edit" or "patch" => "Edit", "write" => "Write", "shell" => "Bash", "grep" => "Grep", "glob" => "Glob", _ => name };
}
