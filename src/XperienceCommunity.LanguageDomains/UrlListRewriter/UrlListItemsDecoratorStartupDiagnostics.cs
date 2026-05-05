using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace XperienceCommunity.LanguageDomains.UrlListRewriter;

/// <summary>
/// Surfaces a registration-time failure of
/// <c>HostnameCultureMappingExtensions.AddHostnameAwareUrlListItemsRetrieverDecorator</c>
/// to the application's logging pipeline (and so to Kentico's event log)
/// once at app startup.
///
/// <para>The decorator's failure modes - the internal admin interface type
/// can't be resolved by name, or no existing service registration was found -
/// are detected during <c>IServiceCollection</c> wiring, long before any
/// logger is available. This hosted service is registered conditionally on
/// failure and re-emits the captured message during <c>StartAsync</c>, when
/// <see cref="ILogger"/> is fully resolvable.</para>
///
/// <para>Without this, those failures would be silent - the admin URLs tab
/// would simply not reflect language-domain rewriting and the operator would
/// have nothing to grep for.</para>
/// </summary>
internal sealed class UrlListItemsDecoratorStartupDiagnostics : IHostedService
{
    private readonly ILogger _logger;
    private readonly string _message;

    public UrlListItemsDecoratorStartupDiagnostics(ILogger logger, string message)
    {
        _logger = logger;
        _message = message;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogError("{Message}", _message);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
