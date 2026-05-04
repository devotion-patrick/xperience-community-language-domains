using CMS;
using CMS.Websites.Routing;

using Kentico.Content.Web.Mvc.Routing;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using XperienceCommunity.LanguageDomains.Decorators;
using XperienceCommunity.LanguageDomains.Internal;

[assembly: RegisterImplementation(
    typeof(IPreferredLanguageRetriever),
    typeof(HostnameAwarePreferredLanguageRetriever))]

namespace XperienceCommunity.LanguageDomains.Decorators;

/// <summary>
/// Decorator over Kentico's <see cref="IPreferredLanguageRetriever"/>. Resolves
/// the preferred language as:
///   1. The <c>?language=&lt;code&gt;</c> query string value, if present and
///      configured for the active website channel.
///   2. The <see cref="HostLanguageMatch"/> stashed by
///      <see cref="Middleware.HostnameCulturePathPrefixMiddleware"/> on
///      <see cref="HttpContext.Items"/>, if any. This is the authoritative
///      result of the (host, path) scan against the URL the user typed.
///   3. A live <see cref="HostLanguageMatcher.Match"/> against the current
///      request - safety net for paths the path-prefix middleware skipped
///      (e.g. excluded admin/static paths) and for setups where it isn't
///      registered. Less reliable on multi-language hosts because by this
///      point the path may already have been rewritten to its storage form.
///   4. The inner retriever's value (Kentico's stock fallback).
/// </summary>
public class HostnameAwarePreferredLanguageRetriever : IPreferredLanguageRetriever
{
    private readonly IPreferredLanguageRetriever _inner;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly HostnameLookupIndex _index;
    private readonly IWebsiteChannelContext _channelContext;
    private readonly ILogger<HostnameAwarePreferredLanguageRetriever> _logger;

    public HostnameAwarePreferredLanguageRetriever(
        IPreferredLanguageRetriever inner,
        IHttpContextAccessor httpContextAccessor,
        HostnameLookupIndex index,
        IWebsiteChannelContext channelContext,
        ILogger<HostnameAwarePreferredLanguageRetriever> logger)
    {
        _inner = inner;
        _httpContextAccessor = httpContextAccessor;
        _index = index;
        _channelContext = channelContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Get()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            return _inner.Get();
        }

        string channelName = _channelContext.WebsiteChannelName;
        if (string.IsNullOrEmpty(channelName))
        {
            return _inner.Get();
        }

        // 1. Query string override (?language=xx). Only honoured if the requested
        //    code is one of the channel's configured languages (across any host).
        if (httpContext.Request.Query.TryGetValue("language", out var queryLang) &&
            !string.IsNullOrWhiteSpace(queryLang))
        {
            string requested = queryLang.ToString();
            if (_index.ChannelHasLanguage(channelName, requested))
            {
                LogResolved(requested, "query-string", channelName, httpContext);
                return requested;
            }
        }

        // 2. Match stashed by the path-prefix middleware (authoritative -
        //    computed against the user-typed URL, before any path rewrite).
        if (httpContext.Items.TryGetValue(HostLanguageMatch.ContextItemKey, out object? stashed)
            && stashed is HostLanguageMatch stashedMatch
            && string.Equals(stashedMatch.ChannelName, channelName, StringComparison.OrdinalIgnoreCase))
        {
            LogResolved(stashedMatch.LanguageCode, "middleware-stash", channelName, httpContext);
            return stashedMatch.LanguageCode;
        }

        // 3. Best-effort live match. Reliable for hostname-only setups, less
        //    reliable on multi-language hosts where the path may already be
        //    rewritten.
        var snapshot = _index.Current;
        string? effectiveHost = RequestHostResolver.GetEffectiveHost(httpContext.Request, snapshot.ForwardedHostHeader);
        if (!string.IsNullOrEmpty(effectiveHost))
        {
            var liveMatch = _index.Match(effectiveHost!, httpContext.Request.Path.Value ?? "/");
            if (liveMatch != null
                && string.Equals(liveMatch.ChannelName, channelName, StringComparison.OrdinalIgnoreCase))
            {
                LogResolved(liveMatch.LanguageCode, "live-match", channelName, httpContext);
                return liveMatch.LanguageCode;
            }
        }

        // 4. Fall through to Kentico's stock retriever.
        return _inner.Get();
    }

    private void LogResolved(string language, string source, string channelName, HttpContext httpContext)
    {
        // Debug-level: Kentico calls IPreferredLanguageRetriever many times
        // per request. Avoid flooding logs by gating behind IsEnabled and
        // only logging when we override the inner (the no-match fallback path
        // is silent on purpose).
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var headers = RequestHostResolver.Snapshot(httpContext.Request);
            _logger.LogDebug(
                "Resolved preferred language {Language} from {Source} (channel={ChannelName}) | RequestHost={RequestHost} XForwardedHost={XForwardedHost} XOriginalHost={XOriginalHost} XForwardedProto={XForwardedProto}",
                language,
                source,
                channelName,
                headers.RequestHost,
                headers.XForwardedHost,
                headers.XOriginalHost,
                headers.XForwardedProto);
        }
    }
}
