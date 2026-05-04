using XperienceCommunity.LanguageDomains.Configuration;

namespace XperienceCommunity.LanguageDomains.Internal;

/// <summary>
/// Default URL path prefixes the package skips entirely - admin endpoints,
/// static asset serving, Kentico-internal handlers. Excluded paths short-
/// circuit both middlewares (no matching, no redirect, no logging) so
/// non-page traffic stays cheap.
///
/// <para>Add to (don't replace) this list via
/// <see cref="HostnameCultureMappingOptions.AdditionalExcludedPathPrefixes"/>.
/// Custom routes that should never be touched by language routing
/// (webhooks, health checks, custom API surfaces) belong there.</para>
/// </summary>
internal static class ExcludedPathPrefixes
{
    public static readonly IReadOnlyList<string> Defaults = new[]
    {
        "/admin",
        "/api",
        "/cmsctx/",
        "/getmedia/",
        "/getcontentasset/",
        "/kentico.activities/",
        "/kenticoactivitylogger/",
        "/kentico.components/",
        "/kenticoformwidget/",
        "/kentico.pagebuilder/",
        "/kentico.resource/",
        "/_content/",
    };

    /// <summary>
    /// Returns whether <paramref name="path"/> starts with any prefix in
    /// <paramref name="prefixes"/> (case-insensitive).
    /// </summary>
    public static bool IsExcluded(string path, IReadOnlyList<string> prefixes)
    {
        for (int i = 0; i < prefixes.Count; i++)
        {
            if (path.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
