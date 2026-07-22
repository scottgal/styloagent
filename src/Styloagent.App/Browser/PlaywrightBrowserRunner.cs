using System.Text.Json;
using Microsoft.Playwright;
using Styloagent.Core.Browser;
using Styloagent.Core.Environments;

namespace Styloagent.App.Browser;

/// <summary>
/// Executes one approved browser job in a fresh non-persistent context. Network routing enforces the
/// environment origin allow-list and observe mode blocks non-idempotent HTTP methods.
/// </summary>
public sealed class PlaywrightBrowserRunner
{
    private static readonly JsonSerializerOptions ArtifactJson = new() { WriteIndented = true };
    private static readonly string[] SensitiveSelectors =
        ["input[type=password]", "[data-sensitive]", ".api-key", ".secret", "[autocomplete=one-time-code]"];
    private readonly string _environmentsRoot;
    private readonly string _browserRoot;
    private readonly IBrowserCredentialProvider _credentials;

    public PlaywrightBrowserRunner(string environmentsRoot, string browserRoot,
        IBrowserCredentialProvider? credentials = null)
        => (_environmentsRoot, _browserRoot, _credentials) =
            (environmentsRoot, browserRoot, credentials ?? new RejectingBrowserCredentialProvider());

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
                Screenshot = "screenshot.png",
                CompletedAt = DateTimeOffset.UtcNow,
            };
            await File.WriteAllTextAsync(Path.Combine(artifactDir, "manifest.json"),
                JsonSerializer.Serialize(manifest, ArtifactJson), ct).ConfigureAwait(false);
            return BrowserRunResult.Completed(screenshotPath);
        }
        catch (OperationCanceledException) { return BrowserRunResult.Failed("browser run cancelled"); }
        catch (Exception ex) { return BrowserRunResult.Failed(SafeFailure(ex)); }
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
