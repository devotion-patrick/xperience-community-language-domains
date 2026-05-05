using System.Reflection;

using Kentico.Xperience.Admin.Websites.UIPages;

using Microsoft.Extensions.Logging;

using XperienceCommunity.LanguageDomains.UrlListRewriter;

namespace XperienceCommunity.LanguageDomains.Tests.UrlListRewriter;

/// <summary>
/// Behavioural tests for <see cref="HostnameAwareUrlListItemsRetrieverProxy"/>.
///
/// The proxy targets an internal Kentico interface
/// (<c>IWebPageUrlListItemsRetriever</c>) we can't reference at compile time,
/// so we shadow the method shape with locally-defined test interfaces - one per
/// XbyK version flavour we care about - and drive the proxy through
/// <see cref="DispatchProxy.Create{T, TProxy}"/> exactly as production does.
///
/// The shape contract (method must exist, return type must match) is enforced
/// separately by <see cref="IWebPageUrlListItemsRetrieverContractTests"/> -
/// that test fails noisily if a future XbyK release drifts the surface.
/// </summary>
[TestFixture]
public class HostnameAwareUrlListItemsRetrieverProxyTests
{
    /// <summary>v30.6.x shape: <c>(int webPageItemId, int languageId)</c>.</summary>
    public interface IV30Style
    {
        public Task<IEnumerable<UrlListItem>> Retrieve(int webPageItemId, int languageId);
    }

    /// <summary>v31.x shape: trailing <see cref="CancellationToken"/> added.</summary>
    public interface IV31Style
    {
        public Task<IEnumerable<UrlListItem>> Retrieve(int webPageItemId, int languageId, CancellationToken cancellationToken);
    }

    /// <summary>Method named differently - must blind-pass-through.</summary>
    public interface INonRetrieve
    {
        public Task<IEnumerable<UrlListItem>> SomethingElse(int webPageItemId, int languageId);
    }

    /// <summary>Same name and arity as Retrieve, but different return type.</summary>
    public interface IWrongReturn
    {
        public Task<string> Retrieve(int webPageItemId, int languageId);
    }

    private sealed class V30Inner : IV30Style
    {
        public List<UrlListItem> ReturnedItems { get; } = [];
        public int CallCount { get; private set; }

        public Task<IEnumerable<UrlListItem>> Retrieve(int webPageItemId, int languageId)
        {
            CallCount++;
            return Task.FromResult<IEnumerable<UrlListItem>>(ReturnedItems);
        }
    }

    private sealed class V31Inner : IV31Style
    {
        public List<UrlListItem> ReturnedItems { get; } = [];
        public int CallCount { get; private set; }
        public CancellationToken? LastToken { get; private set; }

        public Task<IEnumerable<UrlListItem>> Retrieve(int webPageItemId, int languageId, CancellationToken cancellationToken)
        {
            CallCount++;
            LastToken = cancellationToken;
            return Task.FromResult<IEnumerable<UrlListItem>>(ReturnedItems);
        }
    }

    private sealed class NonRetrieveInner : INonRetrieve
    {
        public int CallCount { get; private set; }
        public Task<IEnumerable<UrlListItem>> SomethingElse(int webPageItemId, int languageId)
        {
            CallCount++;
            return Task.FromResult<IEnumerable<UrlListItem>>([]);
        }
    }

    private sealed class WrongReturnInner : IWrongReturn
    {
        public Task<string> Retrieve(int webPageItemId, int languageId)
            => Task.FromResult("not a UrlListItem collection");
    }

    /// <summary>
    /// Test double for <see cref="UrlListItemHostnameRewriter"/>. Records every
    /// call and returns a sentinel collection so the proxy's choice between
    /// "rewrite" and "pass-through" is observable in the assertion.
    /// </summary>
    private sealed class SpyRewriter : UrlListItemHostnameRewriter
    {
        // Base ctor takes a wall of dependencies that the override never
        // touches - null-bang each so we don't have to spin up a real DI graph
        // for what is fundamentally an interception test.
        public SpyRewriter()
            : base(null!, null!, null!, null!, null!, null!, null!) { }

        public List<(int WebPageItemId, int LanguageId, IEnumerable<UrlListItem> Items)> Calls { get; } = [];
        public IEnumerable<UrlListItem>? OverrideResult { get; set; }

        public override IEnumerable<UrlListItem> Rewrite(int webPageItemId, int languageId, IEnumerable<UrlListItem> items)
        {
            Calls.Add((webPageItemId, languageId, items));
            return OverrideResult ?? items;
        }
    }

    private static (TInterface Face, HostnameAwareUrlListItemsRetrieverProxy Proxy) BuildProxy<TInterface>(
        object inner,
        UrlListItemHostnameRewriter? rewriter,
        ILogger? logger = null)
    {
        var face = DispatchProxy.Create<TInterface, HostnameAwareUrlListItemsRetrieverProxy>();
        // Production code casts to the proxy base type to set the state
        // properties (Inner, Rewriter, Logger); the generated proxy class
        // inherits from HostnameAwareUrlListItemsRetrieverProxy, so the cast
        // is valid.
        var proxy = (HostnameAwareUrlListItemsRetrieverProxy)(object)face!;
        proxy.Inner = inner;
        proxy.Rewriter = rewriter;
        proxy.Logger = logger;
        return (face!, proxy);
    }

    /// <summary>
    /// Recording <see cref="ILogger"/>. Used to assert the proxy emits the
    /// drift-detection error when a future XbyK rev changes the
    /// <c>Retrieve</c> shape in a way our compatibility window can't handle.
    /// </summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    [Test]
    public async Task V30_TwoArgRetrieve_RoutesThroughRewriter()
    {
        // Drift case: v30.6.x and earlier expose Retrieve(int, int). Earlier
        // implementations of this proxy required args.Length == 3 and silently
        // missed v30.6.x - this test pins the 2-arg shape so a regression of
        // that bug fails here, not in production at the URLs tab. Verified
        // against the NuGet'd releases up to 30.6.4 (the last 30.6 hotfix).
        var inner = new V30Inner();
        inner.ReturnedItems.Add(new UrlListItem { WebPageUrl = "https://from-inner/" });
        var spy = new SpyRewriter();

        var (face, _) = BuildProxy<IV30Style>(inner, spy);

        var result = await face.Retrieve(42, 7);

        Assert.That(inner.CallCount, Is.EqualTo(1), "Inner Retrieve must be invoked exactly once.");
        Assert.That(spy.Calls, Has.Count.EqualTo(1), "Rewriter must be called for the v30 2-arg shape.");
        Assert.That(spy.Calls[0].WebPageItemId, Is.EqualTo(42));
        Assert.That(spy.Calls[0].LanguageId, Is.EqualTo(7));
        Assert.That(result, Is.SameAs(inner.ReturnedItems));
    }

    [Test]
    public async Task V30Plus_ThreeArgRetrieve_RoutesThroughRewriter()
    {
        // Drift case: v30.7.0 added a trailing CancellationToken (3-arg shape
        // verified through v31.4.3). The proxy's "Length >= 2 + first-two-ints"
        // check accepts this without compile-time knowledge of the third arg.
        var inner = new V31Inner();
        inner.ReturnedItems.Add(new UrlListItem { WebPageUrl = "https://from-inner/" });
        var spy = new SpyRewriter();

        var (face, _) = BuildProxy<IV31Style>(inner, spy);

        using var cts = new CancellationTokenSource();
        await face.Retrieve(99, 13, cts.Token);

        Assert.That(inner.CallCount, Is.EqualTo(1));
        Assert.That(inner.LastToken, Is.EqualTo(cts.Token), "Token must reach the inner unchanged.");
        Assert.That(spy.Calls, Has.Count.EqualTo(1), "Rewriter must be called for the v31 3-arg shape.");
        Assert.That(spy.Calls[0].WebPageItemId, Is.EqualTo(99));
        Assert.That(spy.Calls[0].LanguageId, Is.EqualTo(13));
    }

    [Test]
    public async Task RewriterReturnValue_PropagatesAsTaskResult()
    {
        // The proxy must hand back what Rewrite() produced, not what the inner
        // returned - otherwise rewriting silently has no effect.
        var inner = new V30Inner();
        inner.ReturnedItems.Add(new UrlListItem { WebPageUrl = "https://before-rewrite/" });
        var rewritten = new List<UrlListItem> { new() { WebPageUrl = "https://after-rewrite/" } };
        var spy = new SpyRewriter { OverrideResult = rewritten };

        var (face, _) = BuildProxy<IV30Style>(inner, spy);

        var result = await face.Retrieve(1, 2);

        Assert.That(result, Is.SameAs(rewritten));
    }

    [Test]
    public async Task NonRetrieveMethod_PassesThroughWithoutCallingRewriter()
    {
        var inner = new NonRetrieveInner();
        var spy = new SpyRewriter();

        var (face, _) = BuildProxy<INonRetrieve>(inner, spy);

        await face.SomethingElse(1, 2);

        Assert.That(inner.CallCount, Is.EqualTo(1));
        Assert.That(spy.Calls, Is.Empty, "Rewriter must not run for any method other than Retrieve.");
    }

    [Test]
    public async Task RetrieveWithWrongReturnType_PassesThroughWithoutCallingRewriter()
    {
        // Defensive: even if a future XbyK rev keeps the name `Retrieve` but
        // changes the return type, we must not blow up - just pass through.
        // Drift here would also be caught by IWebPageUrlListItemsRetrieverContractTests.
        var inner = new WrongReturnInner();
        var spy = new SpyRewriter();

        var (face, _) = BuildProxy<IWrongReturn>(inner, spy);

        string result = await face.Retrieve(1, 2);

        Assert.That(result, Is.EqualTo("not a UrlListItem collection"));
        Assert.That(spy.Calls, Is.Empty);
    }

    [Test]
    public async Task RetrieveWithWrongReturnType_LogsShapeMismatchOnceAtError()
    {
        // Drift surfacing: if a future XbyK rev keeps Retrieve(int, int) but
        // changes the return type, the proxy's match condition no longer
        // triggers - production used to silently no-op on the URLs tab. We
        // now log a single Error so the operator sees the regression in the
        // event log.
        var inner = new WrongReturnInner();
        var spy = new SpyRewriter();
        var logger = new RecordingLogger();

        var (face, _) = BuildProxy<IWrongReturn>(inner, spy, logger);

        await face.Retrieve(1, 2);
        await face.Retrieve(3, 4);  // second call — must NOT produce a second log

        Assert.That(spy.Calls, Is.Empty, "Rewriter must not run for the wrong-return shape.");
        Assert.That(logger.Entries, Has.Count.EqualTo(1), "The drift log must fire exactly once per process.");
        Assert.That(logger.Entries[0].Level, Is.EqualTo(LogLevel.Error));
        Assert.That(logger.Entries[0].Message, Does.Contain("HostnameAwareUrlListItemsRetrieverProxy"));
        Assert.That(logger.Entries[0].Message, Does.Contain("doesn't match"));
    }

    [Test]
    public async Task NonRetrieveMethod_DoesNotLogShapeMismatch()
    {
        // The drift log is gated on method name == "Retrieve". Other methods
        // are blind pass-through (proxy's contract); they must not trip the
        // log even if they have an unusual return type.
        var inner = new NonRetrieveInner();
        var spy = new SpyRewriter();
        var logger = new RecordingLogger();

        var (face, _) = BuildProxy<INonRetrieve>(inner, spy, logger);

        await face.SomethingElse(1, 2);

        Assert.That(logger.Entries, Is.Empty);
    }

    [Test]
    public async Task RewriterMissing_DoesNotLogShapeMismatch()
    {
        // Rewriter == null is a "not configured" state, not drift. Logging
        // there would be noise on every call.
        var inner = new V30Inner();
        var logger = new RecordingLogger();

        var (face, _) = BuildProxy<IV30Style>(inner, rewriter: null, logger: logger);

        await face.Retrieve(1, 2);

        Assert.That(logger.Entries, Is.Empty);
    }

    [Test]
    public async Task RewriterMissing_RetrieveStillReturnsInnerResult()
    {
        // If the consumer wires the proxy in but forgets to register the
        // rewriter, behaviour must degrade to "no rewrite" rather than NRE.
        var inner = new V30Inner();
        inner.ReturnedItems.Add(new UrlListItem { WebPageUrl = "https://untouched/" });

        var (face, _) = BuildProxy<IV30Style>(inner, rewriter: null);

        var result = await face.Retrieve(1, 2);

        Assert.That(result, Is.SameAs(inner.ReturnedItems));
    }
}
