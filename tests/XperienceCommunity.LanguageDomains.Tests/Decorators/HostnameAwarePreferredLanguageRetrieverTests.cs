using CMS.Websites.Routing;

using Kentico.Content.Web.Mvc.Routing;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

using XperienceCommunity.LanguageDomains.Configuration;
using XperienceCommunity.LanguageDomains.Decorators;
using XperienceCommunity.LanguageDomains.Internal;

namespace XperienceCommunity.LanguageDomains.Tests.Decorators;

[TestFixture]
public class HostnameAwarePreferredLanguageRetrieverTests
{
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

    private static HostnameAwarePreferredLanguageRetriever BuildRetriever(
        HttpContext httpContext,
        FakeInnerLanguageRetriever inner,
        FakeWebsiteChannelContext channelContext,
        HostnameCultureMappingOptions? options = null)
    {
        var resolvedOptions = options ?? BuildOptions();
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        return new HostnameAwarePreferredLanguageRetriever(
            inner,
            accessor,
            IndexFactory.Build(resolvedOptions),
            channelContext,
            NullLogger<HostnameAwarePreferredLanguageRetriever>.Instance);
    }

    [Test]
    public void Get_HostnameMatch_ReturnsConfiguredLanguage()
    {
        var ctx = TestSupport.BuildHttpContext(host: "uk.example.com");
        var inner = new FakeInnerLanguageRetriever("inner-default");
        var channel = new FakeWebsiteChannelContext { WebsiteChannelName = "DancingGoatPages" };

        string result = BuildRetriever(ctx, inner, channel).Get();

        Assert.That(result, Is.EqualTo("uk"));
    }

    [Test]
    public void Get_StashedMatch_TakesPrecedenceOverLiveScan()
    {
        // Path-prefix middleware would have computed the match against the
        // user-typed URL and stashed it. The retriever must read it back
        // rather than re-scanning a (possibly rewritten) path.
        var ctx = TestSupport.BuildHttpContext(host: "domain.eu", path: "/fr/page");
        ctx.Items[HostLanguageMatch.ContextItemKey] = new HostLanguageMatch(
            ChannelName: "DancingGoatPages",
            LanguageCode: "fr",
            IsRootMatch: false);

        var inner = new FakeInnerLanguageRetriever("inner-default");
        var channel = new FakeWebsiteChannelContext { WebsiteChannelName = "DancingGoatPages" };

        string result = BuildRetriever(ctx, inner, channel).Get();

        Assert.That(result, Is.EqualTo("fr"));
    }

    [Test]
    public void Get_QueryStringOverridesEverything()
    {
        // ?language=uk on en.example.com should resolve to uk.
        var ctx = TestSupport.BuildHttpContext(host: "en.example.com", queryString: "?language=uk");
        var inner = new FakeInnerLanguageRetriever("en");
        var channel = new FakeWebsiteChannelContext { WebsiteChannelName = "DancingGoatPages" };

        string result = BuildRetriever(ctx, inner, channel).Get();

        Assert.That(result, Is.EqualTo("uk"));
    }

    [Test]
    public void Get_QueryStringWithUnknownCode_FallsThroughToHostnameMatch()
    {
        // ?language=xx is not configured; we should ignore the override and
        // fall back to the hostname match (en).
        var ctx = TestSupport.BuildHttpContext(host: "en.example.com", queryString: "?language=xx");
        var inner = new FakeInnerLanguageRetriever("inner-default");
        var channel = new FakeWebsiteChannelContext { WebsiteChannelName = "DancingGoatPages" };

        string result = BuildRetriever(ctx, inner, channel).Get();

        Assert.That(result, Is.EqualTo("en"));
    }

    [Test]
    public void Get_HostnameNotConfigured_FallsThroughToInner()
    {
        var ctx = TestSupport.BuildHttpContext(host: "elsewhere.example.com");
        var inner = new FakeInnerLanguageRetriever("kentico-default");
        var channel = new FakeWebsiteChannelContext { WebsiteChannelName = "DancingGoatPages" };

        string result = BuildRetriever(ctx, inner, channel).Get();

        Assert.That(result, Is.EqualTo("kentico-default"));
    }

    [Test]
    public void Get_NoChannelContext_FallsThroughToInner()
    {
        var ctx = TestSupport.BuildHttpContext(host: "uk.example.com");
        var inner = new FakeInnerLanguageRetriever("kentico-default");
        var channel = new FakeWebsiteChannelContext(); // empty WebsiteChannelName

        string result = BuildRetriever(ctx, inner, channel).Get();

        Assert.That(result, Is.EqualTo("kentico-default"));
    }

    [Test]
    public void Get_UnknownChannel_FallsThroughToInner()
    {
        var ctx = TestSupport.BuildHttpContext(host: "uk.example.com");
        var inner = new FakeInnerLanguageRetriever("kentico-default");
        var channel = new FakeWebsiteChannelContext { WebsiteChannelName = "UnknownChannel" };

        string result = BuildRetriever(ctx, inner, channel).Get();

        Assert.That(result, Is.EqualTo("kentico-default"));
    }

    [Test]
    public void Get_RespectsForwardedHostHeader()
    {
        var options = BuildOptions();
        options.ForwardedHostHeader = "X-Forwarded-Host";
        var ctx = TestSupport.BuildHttpContext(
            host: "internal.local",
            headers: new Dictionary<string, string>
            {
                ["X-Forwarded-Host"] = "uk.example.com",
            });
        var inner = new FakeInnerLanguageRetriever("inner-default");
        var channel = new FakeWebsiteChannelContext { WebsiteChannelName = "DancingGoatPages" };

        string result = BuildRetriever(ctx, inner, channel, options).Get();

        Assert.That(result, Is.EqualTo("uk"));
    }

    private sealed class FakeInnerLanguageRetriever : IPreferredLanguageRetriever
    {
        private readonly string _value;
        public FakeInnerLanguageRetriever(string value) => _value = value;
        public string Get() => _value;
    }

    private sealed class FakeWebsiteChannelContext : IWebsiteChannelContext
    {
        public int WebsiteChannelID { get; set; }
        public string WebsiteChannelName { get; set; } = string.Empty;
        public bool IsPreview { get; set; }
    }
}
