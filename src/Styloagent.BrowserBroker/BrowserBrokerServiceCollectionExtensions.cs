using Microsoft.Extensions.DependencyInjection;

namespace Styloagent.BrowserBroker;

/// <summary>DI registration for embedding the broker in any host (the cockpit, external clients).</summary>
public static class BrowserBrokerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the single shared broker implementation. Hosts supply their environment/browser roots via
    /// <see cref="IBrowserControllerHost"/>; everything else (job lifecycle, Playwright execution, rules,
    /// credentials) comes from the package.
    /// </summary>
    public static IServiceCollection AddBrowserBroker(this IServiceCollection services,
        IBrowserControllerHost host, IBrowserCredentialProvider? credentials = null)
    {
        if (host is null) throw new ArgumentNullException(nameof(host));
        return services
            .AddSingleton(host)
            .AddSingleton(credentials ?? new EnvironmentBrowserCredentialProvider())
            .AddSingleton<IBrowserController>(sp => new BrowserController(host, sp.GetRequiredService<IBrowserCredentialProvider>()));
    }
}
