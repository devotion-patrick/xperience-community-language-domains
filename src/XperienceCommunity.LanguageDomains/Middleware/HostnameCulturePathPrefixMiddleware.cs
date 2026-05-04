using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using XperienceCommunity.LanguageDomains.Decorators;
using XperienceCommunity.LanguageDomains.Internal;

namespace XperienceCommunity.LanguageDomains.Middleware;

/// <summary>
/// Runs BEFORE <c>app.UseKentico()</c>. Translates the user-facing
/// <em>display</em> URL into the <em>storage</em> URL Kentico routes against.
///
/// <para>For each request the middleware uses
/// <see cref="HostnameLookupIndex"/> to find the language that owns the
/// (host, path) pair, then computes:
/// <list type="bullet">
///   <item><description><strong>Display prefix</strong> - what the user sees
///         in the URL: empty for the host's root language, otherwise the
///         language codename.</description></item>
///   <item><description><strong>Storage prefix</strong> - what Kentico stores
///         in <c>WebPageUrlPath</c>: empty for the channel's primary content
///         language, <c>/{langcode}</c> for every other language.</description></item>
/// </list>
/// When the two differ, the middleware swaps display for storage on
/// <see cref="HttpRequest.Path"/>. It also stashes the match on
/// <see cref="HttpContext.Items"/> so
/// <see cref="HostnameAwarePreferredLanguageRetriever"/> can look up the
/// matched language without re-running the scan against the rewritten path.</para>
///
/// This is the inbound half of the hostname-as-language story; the outbound
/// half (translating storage back to display when generating links) lives in
/// <see cref="HostnameAwareWebPageUrlRetriever"/>.
/// </summary>
public class HostnameCulturePathPrefixMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HostnameLookupIndex _index;
    private readonly IChannelPrimaryLanguageResolver _primaryLanguageResolver;
    private readonly ILogger<HostnameCulturePathPrefixMiddleware> _logger;

    public HostnameCulturePathPrefixMiddleware(
        RequestDelegate next,
        HostnameLookupIndex index,
        IChannelPrimaryLanguageResolver primaryLanguageResolver,
        ILogger<HostnameCulturePathPrefixMiddleware> logger)
    {
        _next = next;
        _index = index;
        _primaryLanguageResolver = primaryLanguageResolver;
        _logger = logger;
    }

    /// <summary>
    /// ASP.NET Core middleware entry point.
    /// </summary>
    public Task InvokeAsync(HttpContext context)
    {
        var snapshot = _index.Current;

        string? path = context.Request.Path.Value;
        if (!string.IsNullOrEmpty(path) && ExcludedPathPrefixes.IsExcluded(path, snapshot.ExcludedPathPrefixes))
        {
            return _next(context);
        }

        string? effectiveHost = RequestHostResolver.GetEffectiveHost(context.Request, snapshot.ForwardedHostHeader);
        if (string.IsNullOrEmpty(effectiveHost))
        {
            return _next(context);
        }

        var match = _index.Match(effectiveHost!, path ?? "/");
        if (match == null)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var headers = RequestHostResolver.Snapshot(context.Request);
                _logger.LogDebug(
                    "Path-prefix assess: {Method} {Path} effectiveHost={EffectiveHost} no-match | RequestHost={RequestHost} XForwardedHost={XForwardedHost} XOriginalHost={XOriginalHost} XForwardedProto={XForwardedProto}",
                    context.Request.Method,
                    path,
                    effectiveHost,
                    headers.RequestHost,
                    headers.XForwardedHost,
                    headers.XOriginalHost,
                    headers.XForwardedProto);
            }
            return _next(context);
        }

        // Stash the match so the preferred-language retriever can read it
        // straight back without re-scanning a path we may be about to rewrite.
        context.Items[HostLanguageMatch.ContextItemKey] = match;

        string? primaryLang = _primaryLanguageResolver.GetPrimaryLanguageCode(match.ChannelName);
        string storagePrefix = PathHelpers.GetStoragePrefix(match.LanguageCode, primaryLang);
        string displayPrefix = match.IsRootMatch ? string.Empty : "/" + match.DisplayPrefix;

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var headers = RequestHostResolver.Snapshot(context.Request);
            _logger.LogDebug(
                "Path-prefix assess: {Method} {Path} effectiveHost={EffectiveHost} mapped={Language} channel={Channel} display={Display} storage={Storage} | RequestHost={RequestHost} XForwardedHost={XForwardedHost} XOriginalHost={XOriginalHost} XForwardedProto={XForwardedProto}",
                context.Request.Method,
                path,
                effectiveHost,
                match.LanguageCode,
                match.ChannelName,
                displayPrefix,
                storagePrefix,
                headers.RequestHost,
                headers.XForwardedHost,
                headers.XOriginalHost,
                headers.XForwardedProto);
        }

        if (string.Equals(displayPrefix, storagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return _next(context);
        }

        string originalPath = path ?? "/";
        string stripped = PathHelpers.StripLeadingSegment(originalPath, displayPrefix);
        string rewritten = PathHelpers.PrependSegment(stripped, storagePrefix);
        if (!string.Equals(rewritten, originalPath, StringComparison.Ordinal))
        {
            context.Request.Path = rewritten;

            var headers = RequestHostResolver.Snapshot(context.Request);
            _logger.LogInformation(
                "Path-prefix translate: effectiveHost={EffectiveHost} {OriginalPath} -> internal path {NewPath} (lang={LanguageCode}, display={Display}, storage={Storage}) | RequestHost={RequestHost} XForwardedHost={XForwardedHost} XForwardedProto={XForwardedProto}",
                effectiveHost,
                originalPath,
                rewritten,
                match.LanguageCode,
                displayPrefix,
                storagePrefix,
                headers.RequestHost,
                headers.XForwardedHost,
                headers.XForwardedProto);
        }

        return _next(context);
    }
}
