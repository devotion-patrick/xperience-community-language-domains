using Microsoft.Extensions.Logging.Abstractions;

using XperienceCommunity.LanguageDomains.Configuration;
using XperienceCommunity.LanguageDomains.Internal;
using XperienceCommunity.LanguageDomains.Middleware;

namespace XperienceCommunity.LanguageDomains.Tests.Middleware;

[TestFixture]
public class HostnameCulturePathPrefixMiddlewareTests
{
    /// <summary>
    /// A small but representative config covering the four shapes the
    /// middleware reacts to: a primary root, a non-primary root, and a
    /// non-root reached via a path prefix on a multi-language host.
    /// </summary>
    private static HostnameCultureMappingOptions BuildOptions() => new()
    {
        Channels =
        {
            ["DancingGoatPages"] = new ChannelHostnameMapping
            {
                Hostnames =
                {
                    new HostnameMapping { Hostname = "en.example.com", RootLanguage = "en", Languages = { "en" } },
                    new HostnameMapping { Hostname = "uk.example.com", RootLanguage = "uk", Languages = { "uk" } },
                    // Multi-language host - en-GB is root (primary), fr at /fr.
                    new HostnameMapping
                    {
                        Hostname = "domain.eu",
                        RootLanguage = "en-GB",
                        Languages = { "en-GB", "fr" },
                    },
                },
            },
        },
    };

    private static HostnameCulturePathPrefixMiddleware BuildMiddleware(
        HostnameCultureMappingOptions options,
        out CapturingNext next,
        string primaryLanguage = "en")
    {
        next = new CapturingNext();
        var resolver = new FakePrimaryLanguageResolver(
            ch => string.Equals(ch, "DancingGoatPages", StringComparison.OrdinalIgnoreCase) ? primaryLanguage : null);
        return new HostnameCulturePathPrefixMiddleware(
            next.Delegate,
            IndexFactory.Build(options),
            resolver,
            NullLogger<HostnameCulturePathPrefixMiddleware>.Instance);
    }

    [Test]
    public async Task PrependsStoragePrefix_OnRootHostForNonPrimaryLanguage()
    {
        // uk is the root of uk.example.com but NOT the channel primary -
        // storage path is /uk/..., so we prepend.
        var ctx = TestSupport.BuildHttpContext(host: "uk.example.com", path: "/articles");
        var mw = BuildMiddleware(BuildOptions(), out var next);

        await mw.InvokeAsync(ctx);

        Assert.That(ctx.Request.Path.Value, Is.EqualTo("/uk/articles"));
        Assert.That(next.WasCalled, Is.True);
    }

    [Test]
    public async Task PrependsStoragePrefix_OnRootPath()
    {
        var ctx = TestSupport.BuildHttpContext(host: "uk.example.com", path: "/");
        var mw = BuildMiddleware(BuildOptions(), out _);

        await mw.InvokeAsync(ctx);

        Assert.That(ctx.Request.Path.Value, Is.EqualTo("/uk/"));
    }

    [Test]
    public async Task DoesNotRewrite_WhenRootHostIsForPrimaryLanguage()
    {
        // en is the channel primary - storage and display prefixes are both
        // empty.
        var ctx = TestSupport.BuildHttpContext(host: "en.example.com", path: "/articles");
        var mw = BuildMiddleware(BuildOptions(), out var next);

        await mw.InvokeAsync(ctx);

        Assert.That(ctx.Request.Path.Value, Is.EqualTo("/articles"));
        Assert.That(next.WasCalled, Is.True);
    }

    [Test]
    public async Task DoesNotRewrite_WhenNonRootDisplayMatchesStorage()
    {
        // fr on domain.eu: display = /fr (codename), storage = /fr (non-primary).
        var ctx = TestSupport.BuildHttpContext(host: "domain.eu", path: "/fr/articles");
        var mw = BuildMiddleware(BuildOptions(), out _);

        await mw.InvokeAsync(ctx);

        Assert.That(ctx.Request.Path.Value, Is.EqualTo("/fr/articles"));
    }

    [Test]
    public async Task RootMatchOnMultiLanguageHost_PrimaryLanguage_NoRewrite()
    {
        // en-GB is the channel primary AND the root on domain.eu - storage
        // and display prefixes are both empty.
        var ctx = TestSupport.BuildHttpContext(host: "domain.eu", path: "/about");
        var mw = BuildMiddleware(BuildOptions(), out _, primaryLanguage: "en-GB");

        await mw.InvokeAsync(ctx);

        Assert.That(ctx.Request.Path.Value, Is.EqualTo("/about"));
    }

    [Test]
    public async Task StashesMatchOnHttpContextItems()
    {
        // The preferred-language retriever consumes this stash; missing it
        // would make multi-language hosts fall back to a less reliable scan.
        var ctx = TestSupport.BuildHttpContext(host: "domain.eu", path: "/fr/page");
        var mw = BuildMiddleware(BuildOptions(), out _);

        await mw.InvokeAsync(ctx);

        Assert.That(ctx.Items.TryGetValue(HostLanguageMatch.ContextItemKey, out object? stashed), Is.True);
        Assert.That(stashed, Is.InstanceOf<HostLanguageMatch>());
        var match = (HostLanguageMatch)stashed!;
        Assert.That(match.LanguageCode, Is.EqualTo("fr"));
        Assert.That(match.IsRootMatch, Is.False);
    }

    [Test]
    public async Task PassesThrough_WhenHostnameNotInOptions()
    {
        var ctx = TestSupport.BuildHttpContext(host: "elsewhere.example.com", path: "/articles");
        var mw = BuildMiddleware(BuildOptions(), out var next);

        await mw.InvokeAsync(ctx);

        Assert.That(ctx.Request.Path.Value, Is.EqualTo("/articles"));
        Assert.That(next.WasCalled, Is.True);
    }

    [TestCase("/admin/login")]
    [TestCase("/api/users")]
    [TestCase("/_content/foo.css")]
    [TestCase("/getmedia/abc/x.png")]
    public async Task PassesThrough_OnExcludedPaths(string excludedPath)
    {
        var ctx = TestSupport.BuildHttpContext(host: "uk.example.com", path: excludedPath);
        var mw = BuildMiddleware(BuildOptions(), out _);

        await mw.InvokeAsync(ctx);

        // No mutation expected - the package shouldn't touch admin/api/static
        // even on a host that would otherwise rewrite.
        Assert.That(ctx.Request.Path.Value, Is.EqualTo(excludedPath));
    }

    [Test]
    public async Task RespectsForwardedHostHeader_WhenConfigured()
    {
        // Behind a proxy that doesn't preserve Host, the matching is against
        // the forwarded header value.
        var options = BuildOptions();
        options.ForwardedHostHeader = "X-Forwarded-Host";
        var ctx = TestSupport.BuildHttpContext(
            host: "internal-lb.local",
            path: "/articles",
            headers: new Dictionary<string, string>
            {
                ["X-Forwarded-Host"] = "uk.example.com",
            });
        var mw = BuildMiddleware(options, out _);

        await mw.InvokeAsync(ctx);

        Assert.That(ctx.Request.Path.Value, Is.EqualTo("/uk/articles"));
    }
}
