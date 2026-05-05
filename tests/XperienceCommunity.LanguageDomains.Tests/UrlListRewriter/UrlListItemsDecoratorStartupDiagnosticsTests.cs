using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using XperienceCommunity.LanguageDomains.Extensions;
using XperienceCommunity.LanguageDomains.UrlListRewriter;

namespace XperienceCommunity.LanguageDomains.Tests.UrlListRewriter;

/// <summary>
/// Tests that startup-time failures of
/// <c>AddHostnameAwareUrlListItemsRetrieverDecorator</c> surface to the
/// logging pipeline through <see cref="UrlListItemsDecoratorStartupDiagnostics"/>.
///
/// <para>The "interface type missing" failure mode can't be exercised here -
/// the test process has the admin assembly loaded so the type lookup always
/// succeeds. The "no existing service registration" mode IS testable: call
/// <c>AddHostnameAwareUrlListItemsRetrieverDecorator</c> against a bare
/// <see cref="ServiceCollection"/> (i.e. without <c>AddKentico</c>) and the
/// decorator should fall through and register the diagnostic hosted service.</para>
/// </summary>
[TestFixture]
public class UrlListItemsDecoratorStartupDiagnosticsTests
{
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        // Hoisted out of the (generic) RecordingLogger class - a static field
        // inside a generic owner gets one instance per closed type, which
        // Sonar S2743 flags. The shared instance lives at file scope below.
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => SharedNullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class SharedNullScope : IDisposable
    {
        public static readonly SharedNullScope Instance = new();
        public void Dispose() { }
    }

    [Test]
    public async Task NoExistingServiceRegistration_RegistersHostedDiagnostic_LogsErrorOnStart()
    {
        // Bare ServiceCollection - the admin assembly is loaded (UrlListItem
        // is reachable) so the interface lookup succeeds, but no Kentico-
        // registered IWebPageUrlListItemsRetriever exists. The extension
        // should detect that and register an IHostedService that emits a
        // single Error log on app start.
        var services = new ServiceCollection();
        var recorder = new RecordingLogger<UrlListItemsDecoratorStartupDiagnostics>();
        services.AddSingleton<ILogger<UrlListItemsDecoratorStartupDiagnostics>>(recorder);
        services.AddSingleton<ILogger<UrlListItemHostnameRewriter>>(new RecordingLogger<UrlListItemHostnameRewriter>());

        services.AddHostnameAwareUrlListItemsRetrieverDecorator();

        var hosted = services.BuildServiceProvider().GetServices<IHostedService>().ToList();
        Assert.That(hosted, Has.Count.EqualTo(1),
            "Exactly one diagnostic hosted service should be registered when there's nothing to decorate.");
        Assert.That(hosted[0], Is.InstanceOf<UrlListItemsDecoratorStartupDiagnostics>());

        await hosted[0].StartAsync(CancellationToken.None);

        Assert.That(recorder.Entries, Has.Count.EqualTo(1));
        Assert.That(recorder.Entries[0].Level, Is.EqualTo(LogLevel.Error));
        Assert.That(recorder.Entries[0].Message, Does.Contain("no existing service registration"));
        Assert.That(recorder.Entries[0].Message, Does.Contain("AddHostnameAwareUrlListItemsRetrieverDecorator"));
    }

    [Test]
    public async Task DiagnosticHostedService_StopAsync_IsNoop()
    {
        var recorder = new RecordingLogger<UrlListItemsDecoratorStartupDiagnostics>();
        var diagnostic = new UrlListItemsDecoratorStartupDiagnostics(recorder, "test message");

        await diagnostic.StopAsync(CancellationToken.None);

        Assert.That(recorder.Entries, Is.Empty, "StopAsync must not log.");
    }
}
