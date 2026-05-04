namespace XperienceCommunity.LanguageDomains.Internal;

/// <summary>
/// Result of <see cref="HostnameLookupIndex.Match"/>: which language a
/// (host, path) tuple matched, and whether the match came from the host's
/// root language (no prefix) or a non-root language (prefix-matched).
/// </summary>
public sealed record HostLanguageMatch(
    string ChannelName,
    string LanguageCode,
    bool IsRootMatch)
{
    /// <summary>
    /// Key used to stash a <see cref="HostLanguageMatch"/> on
    /// <see cref="Microsoft.AspNetCore.Http.HttpContext.Items"/> so the
    /// preferred-language retriever can read the match the path-prefix
    /// middleware computed (rather than re-running the host+path scan
    /// against a request whose path the same middleware just rewrote).
    /// </summary>
    public const string ContextItemKey = "XperienceCommunity.LanguageDomains.HostLanguageMatch";

    /// <summary>
    /// User-facing path prefix segment for this match (without leading
    /// slash): empty for a root-language match, the language codename
    /// otherwise.
    /// </summary>
    public string DisplayPrefix => IsRootMatch ? string.Empty : LanguageCode;
}
