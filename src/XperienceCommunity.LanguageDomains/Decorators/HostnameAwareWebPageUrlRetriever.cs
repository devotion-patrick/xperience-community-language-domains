using CMS;
using CMS.ContentEngine;
using CMS.DataEngine;
using CMS.Websites;
using CMS.Websites.Internal;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using XperienceCommunity.LanguageDomains.Decorators;
using XperienceCommunity.LanguageDomains.Internal;

[assembly: RegisterImplementation(
    typeof(IWebPageUrlRetriever),
    typeof(HostnameAwareWebPageUrlRetriever))]

namespace XperienceCommunity.LanguageDomains.Decorators;

/// <summary>
/// Decorator over Kentico's <see cref="IWebPageUrlRetriever"/>. Translates a
/// URL Kentico generates from its <em>storage</em> form (with a
/// <c>/{langcode}</c> prefix on non-channel-primary languages) into the
/// <em>display</em> form configured for the language - <c>/</c> for a root
/// language, <c>/{langcode}</c> otherwise.
///
/// <para>The channel needed for option lookup is derived from the inputs of
/// each overload (the webpage item id, the IWebPageFieldsSource's system
/// fields, the website channel id, or the explicit channel name) - NOT from
/// <see cref="CMS.Websites.Routing.IWebsiteChannelContext"/>, which is only
/// populated for requests scoped to a website channel and is empty for admin
/// / cross-channel calls.</para>
/// </summary>
public class HostnameAwareWebPageUrlRetriever : IWebPageUrlRetriever
{
    private readonly IWebPageUrlRetriever _inner;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly HostnameLookupIndex _index;
    private readonly IInfoProvider<WebPageItemInfo> _webPageItemProvider;
    private readonly IInfoProvider<WebsiteChannelInfo> _websiteChannelProvider;
    private readonly IInfoProvider<ChannelInfo> _channelProvider;
    private readonly IChannelPrimaryLanguageResolver _primaryLanguageResolver;
    private readonly ILogger<HostnameAwareWebPageUrlRetriever> _logger;

    public HostnameAwareWebPageUrlRetriever(
        IWebPageUrlRetriever inner,
        IHttpContextAccessor httpContextAccessor,
        HostnameLookupIndex index,
        IInfoProvider<WebPageItemInfo> webPageItemProvider,
        IInfoProvider<WebsiteChannelInfo> websiteChannelProvider,
        IInfoProvider<ChannelInfo> channelProvider,
        IChannelPrimaryLanguageResolver primaryLanguageResolver,
        ILogger<HostnameAwareWebPageUrlRetriever> logger)
    {
        _inner = inner;
        _httpContextAccessor = httpContextAccessor;
        _index = index;
        _webPageItemProvider = webPageItemProvider;
        _websiteChannelProvider = websiteChannelProvider;
        _channelProvider = channelProvider;
        _primaryLanguageResolver = primaryLanguageResolver;
        _logger = logger;
    }

    // --- Pass-through (no language param to dispatch on) ---------------------

    /// <inheritdoc />
    public Task<WebPageUrl> Retrieve(IWebPageFieldsSource webPageFieldsSource, CancellationToken cancellationToken = default)
        => _inner.Retrieve(webPageFieldsSource, cancellationToken);

    // --- Language-aware overloads --------------------------------------------

    /// <inheritdoc />
    public async Task<WebPageUrl> Retrieve(IWebPageFieldsSource webPageFieldsSource, string languageName, CancellationToken cancellationToken = default)
    {
        var url = await _inner.Retrieve(webPageFieldsSource, languageName, cancellationToken);
        string? channelName = ResolveChannelNameFromWebsiteChannelId(webPageFieldsSource.SystemFields.WebPageItemWebsiteChannelId);
        return Rewrite(url, channelName, languageName);
    }

    /// <inheritdoc />
    public async Task<WebPageUrl> Retrieve(string webPageUrlPath, string webPageTreePath, int websiteChannelId, string languageName, CancellationToken cancellationToken = default)
    {
        var url = await _inner.Retrieve(webPageUrlPath, webPageTreePath, websiteChannelId, languageName, cancellationToken);
        string? channelName = ResolveChannelNameFromWebsiteChannelId(websiteChannelId);
        return Rewrite(url, channelName, languageName);
    }

    /// <inheritdoc />
    public async Task<WebPageUrl> Retrieve(int webPageItemId, string languageName, bool forPreview = false, CancellationToken cancellationToken = default)
    {
        var url = await _inner.Retrieve(webPageItemId, languageName, forPreview, cancellationToken);
        string? channelName = ResolveChannelNameFromWebPageItemId(webPageItemId);
        return Rewrite(url, channelName, languageName);
    }

    /// <inheritdoc />
    public async Task<WebPageUrl> Retrieve(Guid webPageItemGuid, string languageName, bool forPreview = false, CancellationToken cancellationToken = default)
    {
        var url = await _inner.Retrieve(webPageItemGuid, languageName, forPreview, cancellationToken);
        string? channelName = ResolveChannelNameFromWebPageItemGuid(webPageItemGuid);
        return Rewrite(url, channelName, languageName);
    }

    /// <inheritdoc />
    public async Task<IDictionary<Guid, WebPageUrl>> Retrieve(IReadOnlyCollection<Guid> webPageItemGuids, string websiteChannelName, string languageName, bool forPreview = false, CancellationToken cancellationToken = default)
    {
        var urls = await _inner.Retrieve(webPageItemGuids, websiteChannelName, languageName, forPreview, cancellationToken);
        if (urls == null || urls.Count == 0)
        {
            return urls!;
        }

        // Hoist the index lookup + primary-language resolution out of the
        // per-item loop. The whole batch shares (channelName, languageName),
        // so the lookup is identical for every entry; doing it once turns
        // O(N) hash lookups into O(1) for a 100-link batch.
        var lookup = _index.FindForLanguage(websiteChannelName, languageName);
        if (lookup == null)
        {
            return urls;
        }
        string? primaryLang = _primaryLanguageResolver.GetPrimaryLanguageCode(websiteChannelName);
        var rewriteCtx = BuildRewriteContext(lookup, websiteChannelName, languageName, primaryLang);

        var result = new Dictionary<Guid, WebPageUrl>(urls.Count);
        foreach (var (guid, url) in urls)
        {
            result[guid] = RewriteWith(url, rewriteCtx);
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<WebPageUrl> Retrieve(string webPageTreePath, string websiteChannelName, string languageName, bool forPreview = false, CancellationToken cancellationToken = default)
    {
        var url = await _inner.Retrieve(webPageTreePath, websiteChannelName, languageName, forPreview, cancellationToken);
        return Rewrite(url, websiteChannelName, languageName);
    }

    // --- Channel resolution --------------------------------------------------

    private string? ResolveChannelNameFromWebsiteChannelId(int websiteChannelId)
    {
        if (websiteChannelId <= 0)
        {
            return null;
        }
        var websiteChannel = _websiteChannelProvider.Get(websiteChannelId);
        if (websiteChannel == null)
        {
            return null;
        }
        var channel = _channelProvider.Get(websiteChannel.WebsiteChannelChannelID);
        return channel?.ChannelName;
    }

    private string? ResolveChannelNameFromWebPageItemId(int webPageItemId)
    {
        if (webPageItemId <= 0)
        {
            return null;
        }
        var item = _webPageItemProvider.Get(webPageItemId);
        return item == null ? null : ResolveChannelNameFromWebsiteChannelId(item.WebPageItemWebsiteChannelID);
    }

    private string? ResolveChannelNameFromWebPageItemGuid(Guid webPageItemGuid)
    {
        if (webPageItemGuid == Guid.Empty)
        {
            return null;
        }
        var item = _webPageItemProvider.Get()
            .WhereEquals(nameof(WebPageItemInfo.WebPageItemGUID), webPageItemGuid)
            .TopN(1)
            .GetEnumerableTypedResult()
            .FirstOrDefault();
        return item == null ? null : ResolveChannelNameFromWebsiteChannelId(item.WebPageItemWebsiteChannelID);
    }

    // --- Rewrite logic --------------------------------------------------------

    /// <summary>
    /// Single-URL Rewrite: looks up (channel, language) in the index, derives
    /// storage/display prefixes, then delegates to <see cref="RewriteWith"/>.
    /// Used by the per-call overloads. The batch overload short-circuits this
    /// and calls <see cref="RewriteWith"/> directly with a hoisted context.
    /// </summary>
    private WebPageUrl Rewrite(WebPageUrl url, string? channelName, string? languageName)
    {
        if (url == null || string.IsNullOrEmpty(channelName) || string.IsNullOrEmpty(languageName))
        {
            return url!;
        }

        var lookup = _index.FindForLanguage(channelName, languageName);
        if (lookup == null)
        {
            return url;
        }
        string? primaryLang = _primaryLanguageResolver.GetPrimaryLanguageCode(channelName);
        var ctx = BuildRewriteContext(lookup, channelName, languageName, primaryLang);
        return RewriteWith(url, ctx);
    }

    private static RewriteContext BuildRewriteContext(
        HostnameLookupIndex.OutboundEntry lookup,
        string channelName,
        string languageName,
        string? primaryLang)
    {
        // displayPrefix is precomputed on OutboundEntry (no per-call alloc).
        // storagePrefix depends on the channel's primary language, which we
        // can't precompute at index-build time (Kentico content state isn't
        // necessarily available at startup) - so we derive it here and reuse
        // it for every URL in the same batch.
        string storagePrefix = PathHelpers.GetStoragePrefix(languageName, primaryLang);
        return new RewriteContext(
            ChannelName: channelName,
            LanguageName: languageName,
            TargetHost: lookup.Hostname.Hostname,
            DisplayPrefix: lookup.DisplayPrefix,
            StoragePrefix: storagePrefix);
    }

    private WebPageUrl RewriteWith(WebPageUrl url, RewriteContext ctx)
    {
        if (url == null)
        {
            return url!;
        }

        var httpRequest = _httpContextAccessor.HttpContext?.Request;
        var snapshot = _index.Current;
        string? currentHost = httpRequest != null
            ? RequestHostResolver.GetEffectiveHost(httpRequest, snapshot.ForwardedHostHeader)
            : null;

        // Kentico's stock retriever returns RelativePath in the form "~/<path>"
        // (a Razor app-relative reference). Strip the leading "~", do prefix
        // surgery on the resulting absolute path, then re-attach.
        string rawRelative = url.RelativePath ?? "/";
        bool hasTilde = rawRelative.StartsWith("~", StringComparison.Ordinal);
        string pathOnly = hasTilde ? rawRelative[1..] : rawRelative;
        if (string.IsNullOrEmpty(pathOnly))
        {
            pathOnly = "/";
        }

        // Translate storage -> display: strip the storage prefix (Kentico's
        // /{langcode} for non-primary languages) and prepend the display
        // prefix (empty for the host's root language, /{langcode} otherwise).
        if (!string.Equals(ctx.StoragePrefix, ctx.DisplayPrefix, StringComparison.OrdinalIgnoreCase))
        {
            pathOnly = PathHelpers.StripLeadingSegment(pathOnly, ctx.StoragePrefix);
            pathOnly = PathHelpers.PrependSegment(pathOnly, ctx.DisplayPrefix);
        }

        string newRelative = hasTilde ? "~" + pathOnly : pathOnly;
        string scheme = httpRequest != null
            ? RequestHostResolver.GetEffectiveScheme(httpRequest, snapshot.ForwardedHostHeader)
            : "https";
        bool sameHost = !string.IsNullOrEmpty(currentHost) &&
                       string.Equals(ctx.TargetHost, currentHost, StringComparison.OrdinalIgnoreCase);

        // Rebuild the absolute URL against the language's configured hostname.
        // We rebuild unconditionally rather than trusting the inner result
        // because Kentico's stock retriever returns the channel's primary
        // domain in AbsoluteUrl, which is wrong for cross-language links.
        string absoluteHost = sameHost ? currentHost! : ctx.TargetHost;
        string newAbsolute = $"{scheme}://{absoluteHost}{pathOnly}";

        // Debug-level: called once per URL generated (many per page render).
        // Gate on IsEnabled and only log when something actually changed.
        if (_logger.IsEnabled(LogLevel.Debug)
            && (newRelative != url.RelativePath || newAbsolute != url.AbsoluteUrl))
        {
            _logger.LogDebug(
                "Outbound URL rewrite: {InnerRelative} ({InnerAbsolute}) -> {NewRelative} ({NewAbsolute}) (channel={ChannelName}, lang={LanguageName}, display={Display}, storage={Storage})",
                url.RelativePath,
                url.AbsoluteUrl,
                newRelative,
                newAbsolute,
                ctx.ChannelName,
                ctx.LanguageName,
                ctx.DisplayPrefix,
                ctx.StoragePrefix);
        }

        return new WebPageUrl(newRelative, newAbsolute);
    }

    /// <summary>
    /// Carries everything <see cref="RewriteWith"/> needs that's identical
    /// across a batch of URLs sharing the same (channel, language). The batch
    /// overload builds this once and reuses it; per-call overloads build it
    /// per URL.
    /// </summary>
    private readonly record struct RewriteContext(
        string ChannelName,
        string LanguageName,
        string TargetHost,
        string DisplayPrefix,
        string StoragePrefix);
}
