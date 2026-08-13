using System.Text.Json;
using Microsoft.Playwright;
using Styloagent.BrowserBroker;
using Styloagent.Core.Environments;

namespace Styloagent.BrowserBroker;

/// <summary>
/// Executes one approved browser job in a fresh non-persistent context. Network routing enforces the
/// environment origin allow-list and observe mode blocks non-idempotent HTTP methods.
/// </summary>
public sealed class PlaywrightBrowserRunner
{
    private static readonly JsonSerializerOptions ArtifactJson = new() { WriteIndented = true };
    private static readonly string[] SensitiveSelectors =
        ["input[type=password]", "input[name=password]", "input[type=email]", "input[name=username]",
            "[data-sensitive]", ".api-key", ".secret", "[autocomplete=one-time-code]"];
    private readonly string _environmentsRoot;
    private readonly string _browserRoot;
    private readonly IBrowserCredentialProvider _credentials;
    private readonly TimeSpan _loginWaitTimeout;

    public PlaywrightBrowserRunner(string environmentsRoot, string browserRoot,
        IBrowserCredentialProvider? credentials = null, TimeSpan? loginWaitTimeout = null)
        => (_environmentsRoot, _browserRoot, _credentials, _loginWaitTimeout) =
            (environmentsRoot, browserRoot, credentials ?? new RejectingBrowserCredentialProvider(),
                loginWaitTimeout ?? TimeSpan.FromSeconds(20));

    public async Task<BrowserRunResult> RunAsync(BrowserJob job, CancellationToken ct)
    {
        var environment = EnvironmentOwnershipStore.Read(_environmentsRoot).Environments
            .FirstOrDefault(e => e.Definition.Id == job.EnvironmentId);
        var originText = environment?.Definition.Targets.WebOrigin;
        if (environment is null || !Uri.TryCreate(originText, UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https"))
            return BrowserRunResult.Failed("environment webOrigin is missing or invalid");
        var allowedOrigins = new[] { environment.Definition.Targets.WebOrigin, environment.Definition.Targets.ApiOrigin }
            .Where(value => Uri.TryCreate(value, UriKind.Absolute, out _))
            .Select(value => new Uri(value!, UriKind.Absolute))
            .ToArray();

        IReadOnlyDictionary<string, string>? headers = null;
        if (job.CredentialRef is not null)
        {
            try { headers = await _credentials.ResolveHeadersAsync(job.CredentialRef, ct).ConfigureAwait(false); }
            catch { return BrowserRunResult.Failed("approved credential reference could not be resolved"); }
            // Fail closed: a job that carried a credential ref must never run keyless, even if a
            // custom provider returns an empty header set instead of throwing.
            if (headers is null || headers.Count == 0)
                return BrowserRunResult.Failed("approved credential reference could not be resolved");
        }

        // Login credentials are resolved up front like headers — a job that declared a login step
        // must not limp along on missing values. Values stay in locals; only refs reach job files.
        string? loginEmail = null;
        string? loginPassword = null;
        if (job.Login is { } login)
        {
            try
            {
                loginEmail = _credentials.ResolveValue(login.EmailRef);
                loginPassword = _credentials.ResolveValue(login.PasswordRef);
            }
            catch { loginEmail = loginPassword = null; }
            if (string.IsNullOrEmpty(loginEmail) || string.IsNullOrEmpty(loginPassword))
                return BrowserRunResult.Failed("login step could not resolve its email/password references");
        }

        var artifactDir = Path.Combine(_browserRoot, "artifacts", job.Id);
        Directory.CreateDirectory(artifactDir);
        var screenshotPath = Path.Combine(artifactDir, "screenshot.png");
        try
        {
            using var playwright = await Playwright.CreateAsync().WaitAsync(ct).ConfigureAwait(false);
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
            }).WaitAsync(ct).ConfigureAwait(false);
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1440, Height = 1000 },
                IgnoreHTTPSErrors = false,
                ExtraHTTPHeaders = headers is null ? null : new Dictionary<string, string>(headers),
                AcceptDownloads = false,
                Locale = "en-GB",
                TimezoneId = "Europe/London",
            }).WaitAsync(ct).ConfigureAwait(false);
            context.SetDefaultTimeout(15_000);
            await context.RouteAsync("**/*", async route =>
            {
                var request = route.Request;
                var allowedOrigin = Uri.TryCreate(request.Url, UriKind.Absolute, out var requestUri) &&
                    allowedOrigins.Any(allowed => SameOrigin(allowed, requestUri));
                var allowedMethod = job.Mode != BrowserRunMode.Observe ||
                    request.Method is "GET" or "HEAD" or "OPTIONS";
                if (!allowedOrigin || !allowedMethod) await route.AbortAsync().ConfigureAwait(false);
                else await route.ContinueAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);

            var page = await context.NewPageAsync().WaitAsync(ct).ConfigureAwait(false);
            var target = new Uri(origin, job.RelativePath);
            await page.GotoAsync(target.AbsoluteUri, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30_000,
            }).WaitAsync(ct).ConfigureAwait(false);

            // A failed login step still screenshots what is visible — the operator needs the
            // what-you-see evidence — but the run itself is marked failed.
            var loginCompleted = job.Login is null ||
                await PerformLoginAsync(page, loginEmail!, loginPassword!, job.Login, ct);

            var masks = SensitiveSelectors.Select(selector => page.Locator(selector)).ToArray();
            if (job.Selector is not null)
            {
                await page.Locator(job.Selector).ScreenshotAsync(new LocatorScreenshotOptions
                {
                    Path = screenshotPath,
                    Mask = masks,
                    Animations = ScreenshotAnimations.Disabled,
                }).WaitAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = screenshotPath,
                    FullPage = job.FullPage,
                    Mask = masks,
                    Animations = ScreenshotAnimations.Disabled,
                }).WaitAsync(ct).ConfigureAwait(false);
            }

            var manifest = new
            {
                job.Id,
                job.Requester,
                job.EnvironmentId,
                Mode = job.Mode.ToString().ToLowerInvariant(),
                Target = target.GetLeftPart(UriPartial.Path),
                job.Selector,
                job.FullPage,
                CredentialUsed = job.CredentialRef is not null,
                LoginUsed = job.Login is not null,
                LoginCompleted = loginCompleted,
                Screenshot = "screenshot.png",
                CompletedAt = DateTimeOffset.UtcNow,
            };
            await File.WriteAllTextAsync(Path.Combine(artifactDir, "manifest.json"),
                JsonSerializer.Serialize(manifest, ArtifactJson), ct).ConfigureAwait(false);
            return loginCompleted
                ? BrowserRunResult.Completed(screenshotPath)
                : BrowserRunResult.Failed("login step did not complete");
        }
        catch (OperationCanceledException) { return BrowserRunResult.Failed("browser run cancelled"); }
        catch (Exception ex) { return BrowserRunResult.Failed(SafeFailure(ex)); }
    }

    /// <summary>
    /// Fills the login form, submits, and waits for the password field to detach (the post-login
    /// signal). Values are filled directly into the page and never logged; a timeout or missing
    /// element returns false so the caller screenshots what is visible and fails the run.
    /// </summary>
    private async Task<bool> PerformLoginAsync(IPage page, string email, string password, LoginStep login,
        CancellationToken ct)
    {
        try
        {
            var emailInput = page.Locator("input[type=email]");
            if (await emailInput.CountAsync().WaitAsync(ct) == 0)
                emailInput = page.Locator("input[name=username]");
            var passwordInput = page.Locator("input[type=password]");
            if (await passwordInput.CountAsync().WaitAsync(ct) == 0)
                passwordInput = page.Locator("input[name=password]");
            await emailInput.FillAsync(email).WaitAsync(ct);
            await passwordInput.FillAsync(password).WaitAsync(ct);

            var submit = string.IsNullOrWhiteSpace(login.SubmitSelector)
                ? page.Locator("input[type=submit]")
                : page.Locator(login.SubmitSelector);
            if (await submit.CountAsync().WaitAsync(ct) == 0)
                submit = page.Locator("button[type=submit]");
            await submit.ClickAsync().WaitAsync(ct);

            await page.Locator("input[type=password]")
                .WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Detached,
                    Timeout = (int)_loginWaitTimeout.TotalMilliseconds,
                }).WaitAsync(ct);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static bool SameOrigin(Uri expected, Uri actual) =>
        expected.Scheme.Equals(actual.Scheme, StringComparison.OrdinalIgnoreCase) &&
        expected.Host.Equals(actual.Host, StringComparison.OrdinalIgnoreCase) &&
        expected.Port == actual.Port;

    // Exception text can contain target URLs but must never carry headers/cookies/credential material.
    private static string SafeFailure(Exception ex) => ex switch
    {
        PlaywrightException => "Playwright navigation or capture failed",
        _ => "browser execution failed",
    };
}
