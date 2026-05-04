using System.Collections.Concurrent;

using CMS.ContentEngine;
using CMS.DataEngine;
using CMS.Websites;

namespace XperienceCommunity.LanguageDomains.Internal;

/// <summary>
/// Resolves the primary content language code for a given website channel
/// code name. Used to decide whether a language requires a path prefix in
/// Kentico's URL-path storage: the channel's primary language stores its
/// URL paths without a prefix, every other language stores them with a
/// <c>{langcode}/</c> prefix.
/// </summary>
public interface IChannelPrimaryLanguageResolver
{
    /// <summary>
    /// Returns the language code (e.g. <c>"en"</c>) of the channel's primary
    /// content language, or <c>null</c> if the channel can't be resolved.
    /// </summary>
    public string? GetPrimaryLanguageCode(string channelName);
}

/// <summary>
/// Default <see cref="IChannelPrimaryLanguageResolver"/> backed by Kentico's
/// <see cref="IInfoProvider{T}"/> chain
/// (<see cref="ChannelInfo"/> -> <see cref="WebsiteChannelInfo"/> ->
/// <see cref="ContentLanguageInfo"/>) with a process-lifetime cache layered
/// on top.
///
/// <para><strong>Why we cache:</strong> Kentico's hash-table cache
/// (<c>[InfoCache(InfoCacheBy.ID|Name|Guid)]</c> on the *Info class) only
/// covers lookups against the configured identifier columns. The middle leg
/// of our chain - <c>WebsiteChannelInfo</c> by
/// <c>WebsiteChannelChannelID</c> - is a foreign-key query, not by a primary
/// identifier, so it bypasses the hash-table cache and hits the database on
/// every call. Since this resolver runs once per request (path-prefix
/// middleware) and once per generated URL (the URL retriever decorator), an
/// uncached implementation would add a DB round-trip per link.</para>
///
/// <para><strong>Cache lifetime:</strong> the channel-name -&gt;
/// primary-language-code mapping is configured in the admin UI via channel
/// settings; runtime changes are rare. We cache for the lifetime of the
/// process - a config change requires an app restart to pick up. If you
/// need live invalidation, subscribe to <c>WebsiteChannelInfo.TYPEINFO</c>
/// change events and call <see cref="InvalidateAll"/>.</para>
/// </summary>
public sealed class ChannelPrimaryLanguageResolver : IChannelPrimaryLanguageResolver
{
    private readonly IInfoProvider<ChannelInfo> _channels;
    private readonly IInfoProvider<WebsiteChannelInfo> _websiteChannels;
    private readonly IInfoProvider<ContentLanguageInfo> _languages;

    // Process-lifetime cache. Sentinel - a key whose value is null means
    // we resolved the channel and confirmed it has no primary language /
    // doesn't exist; we cache the negative result too so repeated misses
    // don't keep hitting the DB.
    private readonly ConcurrentDictionary<string, string?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public ChannelPrimaryLanguageResolver(
        IInfoProvider<ChannelInfo> channels,
        IInfoProvider<WebsiteChannelInfo> websiteChannels,
        IInfoProvider<ContentLanguageInfo> languages)
    {
        _channels = channels;
        _websiteChannels = websiteChannels;
        _languages = languages;
    }

    /// <inheritdoc />
    public string? GetPrimaryLanguageCode(string channelName)
    {
        if (string.IsNullOrEmpty(channelName))
        {
            return null;
        }
        return _cache.GetOrAdd(channelName, ResolveFromKentico);
    }

    /// <summary>
    /// Drops every cached entry. Call this from a Kentico
    /// <c>WebsiteChannelInfo</c> change-event handler if you need the
    /// resolver to pick up admin-side primary-language changes without an
    /// app restart.
    /// </summary>
    public void InvalidateAll() => _cache.Clear();

    private string? ResolveFromKentico(string channelName)
    {
        var channel = _channels.Get()
            .WhereEquals(nameof(ChannelInfo.ChannelName), channelName)
            .TopN(1)
            .GetEnumerableTypedResult()
            .FirstOrDefault();
        if (channel == null)
        {
            return null;
        }

        var websiteChannel = _websiteChannels.Get()
            .WhereEquals(nameof(WebsiteChannelInfo.WebsiteChannelChannelID), channel.ChannelID)
            .TopN(1)
            .GetEnumerableTypedResult()
            .FirstOrDefault();
        if (websiteChannel == null)
        {
            return null;
        }

        var primary = _languages.Get(websiteChannel.WebsiteChannelPrimaryContentLanguageID);
        return primary?.ContentLanguageName;
    }
}
