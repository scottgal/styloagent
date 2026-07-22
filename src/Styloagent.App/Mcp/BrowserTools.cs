using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using Styloagent.Core.Mcp;

namespace Styloagent.App.Mcp;

/// <summary>MCP surface for owner-governed, broker-executed Playwright screenshot requests.</summary>
[McpServerToolType]
public sealed class BrowserTools
{
    private readonly IHttpContextAccessor _http;
    private readonly IBrowserController _controller;
    private readonly McpAuth _auth;

    public BrowserTools(IHttpContextAccessor http, IBrowserController controller, McpAuth auth)
        => (_http, _controller, _auth) = (http, controller, auth);

#pragma warning disable CA1707
    [McpServerTool, Description("Request a governed Playwright screenshot against a registered environment. relative_path must begin with '/'; mode is observe, test, or operate. Credential values are forbidden—credential_ref may only be an environment-approved opaque secret reference.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name.")]
    public async Task<string> request_browser_run(string environment, string mode, string purpose,
        string relative_path, string selector, bool full_page, string credential_ref)
    {
        var caller = Caller();
        return caller is null ? "unauthorized" : await _controller.RequestAsync(caller, environment, mode,
            purpose, relative_path, Empty(selector), full_page, Empty(credential_ref)).ConfigureAwait(false);
    }

    [McpServerTool, Description("Approve and start a pending Playwright request. Only the environment owner or environment control owner may approve it.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name.")]
    public async Task<string> approve_browser_run(string request_id)
    {
        var caller = Caller();
        return caller is null ? "unauthorized" : await _controller.ApproveAsync(caller, request_id).ConfigureAwait(false);
    }

    [McpServerTool, Description("Cancel a queued or running browser request. The requester, environment owner, or control owner may cancel it.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name.")]
    public async Task<string> cancel_browser_run(string request_id)
    {
        var caller = Caller();
        return caller is null ? "unauthorized" : await _controller.CancelAsync(caller, request_id).ConfigureAwait(false);
    }

    [McpServerTool, Description("Show browser request state. Pass empty request_id and environment to list recent requests.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name.")]
    public async Task<string> browser_status(string request_id, string environment)
    {
        var caller = Caller();
        return caller is null ? "unauthorized" : await _controller.StatusAsync(Empty(request_id), Empty(environment)).ConfigureAwait(false);
    }

    [McpServerTool, Description("Return the sanitized screenshot path for a completed browser request. Raw traces, cookies and credentials are never returned.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name.")]
    public async Task<string> browser_artifacts(string request_id)
    {
        var caller = Caller();
        return caller is null ? "unauthorized" : await _controller.ArtifactsAsync(caller, request_id).ConfigureAwait(false);
    }
#pragma warning restore CA1707

    private string? Caller()
    {
        var ctx = _http.HttpContext;
        return ctx is not null && _auth.TokenOk(ctx) ? McpAuth.CallerPrefix(ctx) : null;
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
