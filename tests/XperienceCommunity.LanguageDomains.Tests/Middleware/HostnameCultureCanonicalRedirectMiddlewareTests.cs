using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

using XperienceCommunity.LanguageDomains.Configuration;
using XperienceCommunity.LanguageDomains.Middleware;

namespace XperienceCommunity.LanguageDomains.Tests.Middleware;

[TestFixture]
public class HostnameCultureCanonicalRedirectMiddlewareTests
{
    private static HostnameCultureMappingOptions BuildOptions(
        bool enableCanonicalRedirect = true) => new()
        {
            EnableCanonicalRedirect = enableCanonicalRedirect,
            Channels =
        {
            ["DancingGoatPages"] = new ChannelHostnameMapping
            {
                Hostnames =
                {
                    new HostnameMapping { Hostname = "en.example.com", RootLanguage = "en", Languages = { "en" } },
                    new HostnameMapping { Hostname = "uk.example.com", RootLanguage = "uk", Languages = { "uk" } },
                    // Non-root language reachable at /fr on a multi-language host.
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

    private static HostnameCultureCanonicalRedirectMiddleware BuildMiddleware(
        HostnameCultureMappingOptions options,
        out CapturingNext next)
    {
        next = new CapturingNext();
        return new HostnameCultureCanonicalRedirectMiddleware(
            next.Delegate,
            IndexFactory.Build(options),
            NullLogger<HostnameCultureCanonicalRedirectMiddleware>.Instance);
    }

    [Test]
    public async Task Redirects_WhenLanguagePrefixOnNonPrimaryHost_ForRootLanguage()
    {
        // en.example.com/uk/articles -> https://uk.example.com/articles
        var ctx = TestSupport.BuildHttpContext(host: "en.example.com", path: "/uk/articles");
        var mw = BuildMiddleware(BuildOptions(), out var next);

        await mw.InvokeAsync(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status301MovedPermanently));
            Assert.That(ctx.Response.Headers.Location.ToString(), Is.EqualTo("https://uk.example.com/articles"));
            Assert.That(next.WasCalled, Is.False, "redirect should short-circuit the pipeline");
        });
    }

    [Test]
    public async Task Redirects_WhenPrefixKeptOnPrimaryHost_ForRootLanguage()
    {
        // uk.example.com/uk/articles -> https://uk.example.com/articles
        var ctx = TestSupport.BuildHttpContext(host: "uk.example.com", path: "/uk/articles");
        var mw = BuildMiddleware(BuildOptions(), out var next);

        await mw.InvokeAsync(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status301MovedPermanently));
            Assert.That(ctx.Response.Headers.Location.ToString(), Is.EqualTo("https://uk.example.com/articles"));
            Assert.That(next.WasCalled, Is.False);
        });
    }

    [Test]
    public async Task Redirects_WhenNonRootLanguageOnWrongHost()
    {
        // fr is a non-root language configured under domain.eu. A request on
        // en.example.com/fr/articles should redirect to its canonical:
        // domain.eu/fr/articles.
        var ctx = TestSupport.BuildHttpContext(host: "en.example.com", path: "/fr/articles");
        var mw = BuildMiddleware(BuildOptions(), out _);

        await mw.InvokeAsync(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status301MovedPermanently));
            Assert.That(ctx.Response.Headers.Location.ToString(),
                Is.EqualTo("https://domain.eu/fr/articles"));
        });
    }

    [Test]
    public async Task PreservesTrailingSlash_OnBarePrefixRedirect()
    {
        // /fr/ on the wrong host should redirect to domain.eu/fr/ (slash
        // preserved), not domain.eu/fr (slash dropped). Cosmetic but matters
        // for sites with trailing-slash conventions.
        var ctx = TestSupport.BuildHttpContext(host: "en.example.com", path: "/fr/");
        var mw = BuildMiddleware(BuildOptions(), out _);

        await mw.InvokeAsync(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status301MovedPermanently));
            Assert.That(ctx.Response.Headers.Location.ToString(),
                Is.EqualTo("https://domain.eu/fr/"));
        });
    }

    [Test]
    public async Task NoTrailingSlash_OnBarePrefixRedirect_WhenOriginalHadNone()
    {
        // /fr (no trailing slash) on the wrong host should redirect to
        // domain.eu/fr (no slash). Symmetry check for the trailing-slash fix.
        var ctx = TestSupport.BuildHttpContext(host: "en.example.com", path: "/fr");
        var mw = BuildMiddleware(BuildOptions(), out _);

        await mw.InvokeAsync(ctx);

        Assert.That(ctx.Response.Headers.Location.ToString(),
            Is.EqualTo("https://domain.eu/fr"));
    }

    [Test]
    public async Task PreservesQueryString_OnRedirect()
    {
        var ctx = TestSupport.BuildHttpContext(
            host: "en.example.com",
            path: "/uk/search",
            queryString: "?q=hello&page=2");
        var mw = BuildMiddleware(BuildOptions(), out _);

        await mw.InvokeAsync(ctx);

        Assert.That(ctx.Response.Headers.Location.ToString(),
            Is.EqualTo("https://uk.example.com/search?q=hello&page=2"));
    }

    [Test]
    public async Task PassesThrough_WhenAlreadyOnCanonical()
    {
        // uk.example.com/articles is already the canonical form.
        var ctx = TestSupport.BuildHttpContext(host: "uk.example.com", path: "/articles");
        var mw = BuildMiddleware(BuildOptions(), out var next);

        await mw.InvokeAsync(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(next.WasCalled, Is.True);
        });
    }

    [Test]
    public async Task PassesThrough_WhenLanguageCodeIsTheCanonicalDisplay()
    {
        // fr is non-root on domain.eu, so /fr/articles IS the canonical
        // display - no redirect.
        var ctx = TestSupport.BuildHttpContext(host: "domain.eu", path: "/fr/articles");
        var mw = BuildMiddleware(BuildOptions(), out var next);

        await mw.InvokeAsync(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(next.WasCalled, Is.True);
        });
    }

    [Test]
    public async Task PassesThrough_OnNonGetNonHeadMethods()
    {
        // A POST/PUT/DELETE should never be redirected - we'd silently drop
        // the body. Pass through and let the app decide.
        var ctx = TestSupport.BuildHttpContext(method: "POST", host: "en.example.com", path: "/uk/articles");
        var mw = BuildMiddleware(BuildOptions(), out var next);

        await mw.InvokeAsync(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(next.WasCalled, Is.True);
        });
    }

    [Test]
    public async Task PassesThrough_WhenEnableCanonicalRedirectIsFalse()
    {
        var ctx = TestSupport.BuildHttpContext(host: "en.example.com", path: "/uk/articles");
        var mw = BuildMiddleware(BuildOptions(enableCanonicalRedirect: false), out var next);

        await mw.InvokeAsync(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(next.WasCalled, Is.True);
        });
    }

    [TestCase("/admin/uk/login")]
    [TestCase("/api/uk/users")]
    [TestCase("/_content/foo.css")]
    public async Task PassesThrough_OnExcludedPaths(string path)
    {
        var ctx = TestSupport.BuildHttpContext(host: "en.example.com", path: path);
        var mw = BuildMiddleware(BuildOptions(), out var next);

        await mw.InvokeAsync(ctx);

        Assert.That(next.WasCalled, Is.True);
        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
    }

    [Test]
    public async Task UsesForwardedScheme_WhenForwardedHostHeaderConfigured()
    {
        // Proxy terminates TLS, sends to origin over http with X-Forwarded-Proto: https.
        var options = BuildOptions();
        options.ForwardedHostHeader = "X-Forwarded-Host";
        var ctx = TestSupport.BuildHttpContext(
            scheme: "http",
            host: "internal.local",
            path: "/uk/articles",
            headers: new Dictionary<string, string>
            {
                ["X-Forwarded-Host"] = "en.example.com",
                ["X-Forwarded-Proto"] = "https",
            });
        var mw = BuildMiddleware(options, out _);

        await mw.InvokeAsync(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status301MovedPermanently));
            Assert.That(ctx.Response.Headers.Location.ToString(),
                Is.EqualTo("https://uk.example.com/articles"));
        });
    }
}
