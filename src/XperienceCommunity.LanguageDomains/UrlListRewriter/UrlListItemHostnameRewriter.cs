using CMS.ContentEngine;
using CMS.DataEngine;
using CMS.Websites;
using CMS.Websites.Internal;

using Kentico.Xperience.Admin.Websites.UIPages;

using Microsoft.Extensions.Logging;

using XperienceCommunity.LanguageDomains.Internal;

namespace XperienceCommunity.LanguageDomains.UrlListRewriter;

/// <summary>
/// Post-processing logic that rewrites the URLs on a list of
/// <see cref="UrlListItem"/> entries (the rows shown on the admin URLs tab).
/// Translates Kentico's storage form
/// (channel-default-host + <c>/{langcode}/...</c>) into the configured
/// display form (language's primary host + display prefix).
///
/// This service is invoked indirectly via
/// <see cref="HostnameAwareUrlListItemsRetrieverProxy"/>, a runtime
/// <see cref="System.Reflection.DispatchProxy"/> that decorates the internal
/// <c>IWebPageUrlListItemsRetriever</c> service. The interface is internal to
/// Kentico's admin assembly, so we cannot reference it directly or use
/// <c>[assembly: RegisterImplementation]</c>; reflection plumbing is unavoidable.
/// </summary>
public class UrlListItemHostnameRewriter
{
    private readonly HostnameLookupIndex _index;
    private readonly IInfoProvider<WebPageItemInfo> _webPageItemProvider;
    private readonly IInfoProvider<WebsiteChannelInfo> _websiteChannelProvider;
    private readonly IInfoProvider<ChannelInfo> _channelProvider;
    private readonly IInfoProvider<ContentLanguageInfo> _languageProvider;
    private readonly IChannelPrimaryLanguageResolver _primaryLanguageResolver;
    private readonly ILogger<UrlListItemHostnameRewriter> _logger;

    public UrlListItemHostnameRewriter(
        HostnameLookupIndex index,
        IInfoProvider<WebPageItemInfo> webPageItemProvider,
        IInfoProvider<WebsiteChannelInfo> websiteChannelProvider,
        IInfoProvider<ChannelInfo> channelProvider,
        IInfoProvider<ContentLanguageInfo> languageProvider,
        IChannelPrimaryLanguageResolver primaryLanguageResolver,
        ILogger<UrlListItemHostnameRewriter> logger)
    {
        _index = index;
        _webPageItemProvider = webPageItemProvider;
        _websiteChannelProvider = websiteChannelProvider;
        _channelProvider = channelProvider;
        _languageProvider = languageProvider;
        _primaryLanguageResolver = primaryLanguageResolver;
        _logger = logger;
    }

    /// <summary>
    /// Applies hostname/prefix rewriting to a sequence of URL list items.
    /// </summary>
    public IEnumerable<UrlListItem> Rewrite(int webPageItemId, int languageId, IEnumerable<UrlListItem> items)
    {
        if (items == null)
        {
            return items!;
        }

        string? channelName = ResolveChannelName(webPageItemId);
        if (string.IsNullOrEmpty(channelName))
        {
            return items;
        }

        string? languageName = _languageProvider.Get(languageId)?.ContentLanguageName;
        if (string.IsNullOrEmpty(languageName))
        {
            return items;
        }

        var lookup = _index.FindForLanguage(channelName, languageName);
        if (lookup == null)
        {
            return items;
        }
        var hostnameMapping = lookup.Hostname;
        // displayPrefixWithSlash is precomputed on the OutboundEntry (empty
        // for root, "/{lang}" otherwise) - no per-call allocation.
        string displayPrefixWithSlash = lookup.DisplayPrefix;

        string? primaryLang = _primaryLanguageResolver.GetPrimaryLanguageCode(channelName);
        string storagePrefixWithSlash = PathHelpers.GetStoragePrefix(languageName, primaryLang);

        // The slug strip works on a leading bare segment (no leading slash);
        // collapse the "/segment" form to just "segment" once.
        string stripFromSlug = storagePrefixWithSlash.StartsWith('/')
            ? storagePrefixWithSlash[1..]
            : storagePrefixWithSlash;
        string prependToSlug = displayPrefixWithSlash.StartsWith('/')
            ? displayPrefixWithSlash[1..]
            : displayPrefixWithSlash;

        string targetHost = hostnameMapping.Hostname;

        var list = items as IList<UrlListItem> ?? items.ToList();
        foreach (var item in list)
        {
            RewriteItem(item, targetHost, stripFromSlug, prependToSlug, storagePrefixWithSlash, displayPrefixWithSlash);
        }

        _logger.LogInformation(
            "Admin URLs tab rewrite: webPageItemId={WebPageItemId} channel={ChannelName} lang={LanguageName} -> host={TargetHost} display={Display} storage={Storage} items={Count}",
            webPageItemId,
            channelName,
            languageName,
            targetHost,
            displayPrefixWithSlash,
            storagePrefixWithSlash,
            list.Count);

        return list;
    }

    private static void RewriteItem(
        UrlListItem item,
        string targetHost,
        string stripFromSlug,
        string prependToSlug,
        string storagePrefixWithSlash,
        string displayPrefixWithSlash)
    {
        if (item == null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(item.WebPageUrlPathSlug))
        {
            string slug = item.WebPageUrlPathSlug.TrimStart('/');
            if (!string.IsNullOrEmpty(stripFromSlug))
            {
                if (slug.StartsWith(stripFromSlug + "/", StringComparison.OrdinalIgnoreCase))
                {
                    slug = slug[(stripFromSlug.Length + 1)..];
                }
                else if (string.Equals(slug, stripFromSlug, StringComparison.OrdinalIgnoreCase))
                {
                    slug = string.Empty;
                }
            }
            if (!string.IsNullOrEmpty(prependToSlug)
                && !slug.StartsWith(prependToSlug + "/", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(slug, prependToSlug, StringComparison.OrdinalIgnoreCase))
            {
                slug = string.IsNullOrEmpty(slug)
                    ? prependToSlug
                    : prependToSlug + "/" + slug;
            }
            item.WebPageUrlPathSlug = slug;
        }

        if (!string.IsNullOrEmpty(item.WebPageUrlPathBase))
        {
            item.WebPageUrlPathBase = ReplaceHost(item.WebPageUrlPathBase, targetHost);
        }

        if (!string.IsNullOrEmpty(item.WebPageUrl))
        {
            string rebuilt = ReplaceHost(item.WebPageUrl, targetHost);
            rebuilt = TranslatePathSegment(rebuilt, storagePrefixWithSlash, displayPrefixWithSlash);
            item.WebPageUrl = rebuilt;
        }
    }

    private static string ReplaceHost(string url, string targetHost)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var builder = new UriBuilder(uri);
        int colon = targetHost.IndexOf(':');
        if (colon >= 0)
        {
            builder.Host = targetHost[..colon];
            if (int.TryParse(targetHost.AsSpan(colon + 1), out int port))
            {
                builder.Port = port;
            }
        }
        else
        {
            builder.Host = targetHost;
            builder.Port = -1;
        }

        return builder.Uri.AbsoluteUri;
    }

    private static string TranslatePathSegment(string url, string storagePrefix, string displayPrefix)
    {
        if (string.Equals(storagePrefix, displayPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }
        string path = uri.AbsolutePath;
        path = PathHelpers.StripLeadingSegment(path, storagePrefix);
        path = PathHelpers.PrependSegment(path, displayPrefix);
        var builder = new UriBuilder(uri) { Path = path };
        return builder.Uri.AbsoluteUri;
    }

    private string? ResolveChannelName(int webPageItemId)
    {
        if (webPageItemId <= 0)
        {
            return null;
        }
        var item = _webPageItemProvider.Get(webPageItemId);
        if (item == null)
        {
            return null;
        }
        var websiteChannel = _websiteChannelProvider.Get(item.WebPageItemWebsiteChannelID);
        if (websiteChannel == null)
        {
            return null;
        }
        var channel = _channelProvider.Get(websiteChannel.WebsiteChannelChannelID);
        return channel?.ChannelName;
    }
}
