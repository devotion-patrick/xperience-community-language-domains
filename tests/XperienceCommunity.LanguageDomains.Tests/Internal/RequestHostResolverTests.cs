using XperienceCommunity.LanguageDomains.Internal;

namespace XperienceCommunity.LanguageDomains.Tests.Internal;

[TestFixture]
public class RequestHostResolverTests
{
    [Test]
    public void GetEffectiveHost_WithoutForwardedHeaderConfigured_ReturnsRequestHost()
    {
        var ctx = TestSupport.BuildHttpContext(host: "example.com");

        string? result = RequestHostResolver.GetEffectiveHost(ctx.Request, forwardedHostHeader: null);

        Assert.That(result, Is.EqualTo("example.com"));
    }

    [Test]
    public void GetEffectiveHost_WithForwardedHeaderConfigured_ReturnsHeaderValue()
    {
        var ctx = TestSupport.BuildHttpContext(
            host: "internal-lb.local",
            headers: new Dictionary<string, string>
            {
                ["X-Forwarded-Host"] = "uk.example.com",
            });

        string? result = RequestHostResolver.GetEffectiveHost(ctx.Request, "X-Forwarded-Host");

        Assert.That(result, Is.EqualTo("uk.example.com"));
    }

    [Test]
    public void GetEffectiveHost_HeaderConfiguredButHeaderMissing_FallsBackToRequestHost()
    {
        var ctx = TestSupport.BuildHttpContext(host: "fallback.example.com");

        string? result = RequestHostResolver.GetEffectiveHost(ctx.Request, "X-Forwarded-Host");

        Assert.That(result, Is.EqualTo("fallback.example.com"));
    }

    [Test]
    public void GetEffectiveHost_CommaSeparatedProxyChain_TakesFirstEntry()
    {
        // Per RFC 7239 / X-Forwarded-Host convention, the original
        // client-facing host is the first entry of a comma-separated list.
        var ctx = TestSupport.BuildHttpContext(
            host: "internal.local",
            headers: new Dictionary<string, string>
            {
                ["X-Forwarded-Host"] = "public.example.com, intermediate.example.com",
            });

        string? result = RequestHostResolver.GetEffectiveHost(ctx.Request, "X-Forwarded-Host");

        Assert.That(result, Is.EqualTo("public.example.com"));
    }

    [Test]
    public void GetEffectiveHost_HeaderValueIsWhitespace_FallsBackToRequestHost()
    {
        var ctx = TestSupport.BuildHttpContext(
            host: "fallback.example.com",
            headers: new Dictionary<string, string>
            {
                ["X-Forwarded-Host"] = "   ",
            });

        string? result = RequestHostResolver.GetEffectiveHost(ctx.Request, "X-Forwarded-Host");

        Assert.That(result, Is.EqualTo("fallback.example.com"));
    }

    [Test]
    public void GetEffectiveScheme_WithoutForwardedHeaderConfigured_ReturnsRequestScheme()
    {
        var ctx = TestSupport.BuildHttpContext(scheme: "https");

        string result = RequestHostResolver.GetEffectiveScheme(ctx.Request, forwardedHostHeader: null);

        Assert.That(result, Is.EqualTo("https"));
    }

    [Test]
    public void GetEffectiveScheme_WithForwardedHeaderAndProtoHeader_PrefersProtoHeader()
    {
        // When the forwarded-host header is set, X-Forwarded-Proto is also
        // honoured (we're treating the request as proxied end-to-end).
        var ctx = TestSupport.BuildHttpContext(
            scheme: "http",
            headers: new Dictionary<string, string>
            {
                ["X-Forwarded-Host"] = "uk.example.com",
                ["X-Forwarded-Proto"] = "https",
            });

        string result = RequestHostResolver.GetEffectiveScheme(ctx.Request, "X-Forwarded-Host");

        Assert.That(result, Is.EqualTo("https"));
    }

    [Test]
    public void GetEffectiveScheme_ProxiedButProtoHeaderMissing_FallsBackToRequestScheme()
    {
        var ctx = TestSupport.BuildHttpContext(scheme: "https");

        string result = RequestHostResolver.GetEffectiveScheme(ctx.Request, "X-Forwarded-Host");

        Assert.That(result, Is.EqualTo("https"));
    }

    [Test]
    public void Snapshot_IncludesRequestHostAndAllHostHeaders()
    {
        var ctx = TestSupport.BuildHttpContext(
            host: "edge.example.com",
            headers: new Dictionary<string, string>
            {
                ["X-Forwarded-Host"] = "uk.example.com",
                ["X-Original-Host"] = "uk.example.com",
                ["X-Forwarded-Proto"] = "https",
                ["X-Forwarded-For"] = "203.0.113.10",
            });

        var snap = RequestHostResolver.Snapshot(ctx.Request);

        Assert.Multiple(() =>
        {
            Assert.That(snap.RequestHost, Is.EqualTo("edge.example.com"));
            Assert.That(snap.XForwardedHost, Is.EqualTo("uk.example.com"));
            Assert.That(snap.XOriginalHost, Is.EqualTo("uk.example.com"));
            Assert.That(snap.XForwardedProto, Is.EqualTo("https"));
            Assert.That(snap.XForwardedFor, Is.EqualTo("203.0.113.10"));
        });
    }

    [Test]
    public void Snapshot_MissingHeadersAreNull_NotEmptyString()
    {
        var ctx = TestSupport.BuildHttpContext(host: "example.com");

        var snap = RequestHostResolver.Snapshot(ctx.Request);

        Assert.Multiple(() =>
        {
            Assert.That(snap.RequestHost, Is.EqualTo("example.com"));
            Assert.That(snap.XForwardedHost, Is.Null);
            Assert.That(snap.XOriginalHost, Is.Null);
            Assert.That(snap.XForwardedProto, Is.Null);
            Assert.That(snap.XForwardedFor, Is.Null);
        });
    }
}
