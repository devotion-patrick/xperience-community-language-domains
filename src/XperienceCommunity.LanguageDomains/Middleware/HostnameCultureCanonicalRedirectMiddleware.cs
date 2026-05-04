using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using XperienceCommunity.LanguageDomains.Internal;

namespace XperienceCommunity.LanguageDomains.Middleware;

/// <summary>
/// 301-redirects non-canonical inbound URLs to the canonical hostname / path
/// for their language. Triggers whenever the path begins with
/// <c>/{langcode}/</c> for a configured language and the current
/// (host, prefix) doesn't match the language's canonical (host, prefix).
///
/// <para><strong>Channel-scoped behaviour:</strong> the middleware first
/// looks up the request hostname in the index. If the hostname isn't
/// configured in any channel, the middleware passes through - it never
/// redirects requests on hosts the package doesn't own. If the hostname is
/// configured, the redirect scan considers only languages within the
/// matching hostname's channel - so the same language code in a different
/// channel can't accidentally pull a redirect across channel boundaries.</para>
///
/// <list type="bullet">
///   <item><description>Wrong host for a root language:
///         <c>en.example.com/uk/page</c> -&gt; <c>uk.example.com/page</c>.</description></item>
///   <item><description>Right host but kept the prefix on a root language:
///         <c>uk.example.com/uk/page</c> -&gt; <c>uk.example.com/page</c>.</description></item>
///   <item><description>Wrong host for a non-root language:
///         <c>en.example.com/fr/page</c> -&gt; <c>domain.eu/fr/page</c>
///         (when <c>fr</c> is non-root on <c>domain.eu</c> in the same
///         channel as <c>en.example.com</c>).</description></item>
/// </list>
///
/// Globally togglable via
/// <see cref="Configuration.HostnameCultureMappingOptions.EnableCanonicalRedirect"/>;
/// when <c>false</c>, this middleware short-circuits and lets the request
/// through unchanged.
///
/// <strong>Must run BEFORE <see cref="HostnameCulturePathPrefixMiddleware"/>.</strong>
/// The path-prefix middleware mutates <see cref="HttpRequest.Path"/>, so once
/// it has run we can no longer distinguish the user-typed URL from the
/// internally-rewritten one.
/// </summary>
public class HostnameCultureCanonicalRedirectMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HostnameLookupIndex _index;
    private readonly ILogger<HostnameCultureCanonicalRedirectMiddleware> _logger;

    public HostnameCultureCanonicalRedirectMiddleware(
        RequestDelegate next,
        HostnameLookupIndex index,
        ILogger<HostnameCultureCanonicalRedirectMiddleware> logger)
    {
        _next = next;
        _index = index;
        _logger = logger;
    }

    /// <summary>ASP.NET Core middleware entry point.</summary>
    public Task InvokeAsync(HttpContext context)
    {
        var snapshot = _index.Current;
        if (!snapshot.EnableCanonicalRedirect)
        {
            return _next(context);
        }

        // Only redirect on safe, idempotent methods - never POST/PUT/DELETE.
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            return _next(context);
        }

        string? path = context.Request.Path.Value;
        if (string.IsNullOrEmpty(path) || ExcludedPathPrefixes.IsExcluded(path, snapshot.ExcludedPathPrefixes))
        {
            return _next(context);
        }

        string effectiveHost = RequestHostResolver.GetEffectiveHost(context.Request, snapshot.ForwardedHostHeader) ?? string.Empty;

        // Channel-scope guard: if the request's hostname isn't configured in
        // any channel, we don't own this request - pass through and let
        // Kentico's stock channel resolution decide what to do. Without this
        // guard, a request like c.com/en/foo on an unknown host would
        // potentially redirect to the canonical for "en" in some arbitrary
        // configured channel, which is almost never what the user wanted.
        if (!snapshot.ByHostname.TryGetValue(effectiveHost, out var hostEntry))
        {
            return _next(context);
        }

        var nonCanonical = FindNonCanonicalLanguageInChannel(path, effectiveHost, hostEntry.ChannelName, snapshot);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var snapshotForDebug = RequestHostResolver.Snapshot(context.Request);
            _logger.LogDebug(
                "Canonical-redirect assess: {Method} {Path} effectiveHost={EffectiveHost} channel={Channel} nonCanonicalLanguage={Language} canonicalHost={CanonicalHost} canonicalDisplay={Display} | RequestHost={RequestHost} XForwardedHost={XForwardedHost} XOriginalHost={XOriginalHost} XForwardedProto={XForwardedProto}",
                context.Request.Method,
                path,
                effectiveHost,
                hostEntry.ChannelName,
                nonCanonical?.LanguageCode,
                nonCanonical?.CanonicalHost,
                nonCanonical?.CanonicalDisplay,
                snapshotForDebug.RequestHost,
                snapshotForDebug.XForwardedHost,
                snapshotForDebug.XOriginalHost,
                snapshotForDebug.XForwardedProto);
        }

        if (nonCanonical == null)
        {
            return _next(context);
        }

        var match = nonCanonical.Value;
        string prefix = "/" + match.LanguageCode;
        string remainder;
        if (path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            remainder = path[prefix.Length..];
        }
        else if (string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase))
        {
            remainder = "/";
        }
        else
        {
            // Defensive: FindNonCanonicalLanguageInChannel only returns when
            // the prefix is present, so this branch shouldn't be reachable.
            return _next(context);
        }

        // Preserve a trailing slash on the bare-prefix non-root case: e.g.
        // "/fr/" should redirect to ".../fr/" not ".../fr". The bare-prefix
        // case sets remainder = "/" both for "/fr" and "/fr/" (since
        // path.Substring("/fr".Length) = "/" for "/fr/"); without this
        // check, the non-root branch below collapses both to "/fr".
        // For the root-canonical case (CanonicalDisplay empty) the slash
        // is already part of remainder, so no special-casing needed.
        bool hadTrailingSlashAfterPrefix = path.Length > prefix.Length
            && path[^1] == '/';

        string newPath = BuildCanonicalPath(match.CanonicalDisplay, remainder, hadTrailingSlashAfterPrefix);

        static string BuildCanonicalPath(string canonicalDisplay, string remainder, bool hadTrailingSlashAfterPrefix)
        {
            if (string.IsNullOrEmpty(canonicalDisplay))
            {
                return remainder;
            }
            if (remainder == "/")
            {
                return "/" + canonicalDisplay + (hadTrailingSlashAfterPrefix ? "/" : string.Empty);
            }
            return "/" + canonicalDisplay + remainder;
        }

        string scheme = RequestHostResolver.GetEffectiveScheme(context.Request, snapshot.ForwardedHostHeader);
        string redirectUrl = $"{scheme}://{match.CanonicalHost}{newPath}{context.Request.QueryString}";

        var headers = RequestHostResolver.Snapshot(context.Request);
        _logger.LogInformation(
            "Canonical 301: {Method} {Scheme}://{EffectiveHost}{Path}{QueryString} -> {Target} (lang={LanguageCode}, display={Display}) | RequestHost={RequestHost} XForwardedHost={XForwardedHost} XOriginalHost={XOriginalHost} XForwardedProto={XForwardedProto}",
            context.Request.Method,
            scheme,
            effectiveHost,
            path,
            context.Request.QueryString,
            redirectUrl,
            match.LanguageCode,
            match.CanonicalDisplay,
            headers.RequestHost,
            headers.XForwardedHost,
            headers.XOriginalHost,
            headers.XForwardedProto);

        context.Response.Redirect(redirectUrl, permanent: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Looks for a path whose leading segment is <c>/{langcode}/</c> for a
    /// language configured in <paramref name="channelName"/> whose canonical
    /// (host, display-prefix) doesn't match the current request. Returns the
    /// canonical hostname plus display prefix (empty when the language is the
    /// canonical host's root) so the caller can build the redirect target.
    ///
    /// <para>Scoped to <paramref name="channelName"/> - the channel that owns
    /// the current host. This avoids cross-channel ambiguity when the same
    /// language code is configured in multiple channels (each on their own
    /// hostnames): the redirect always targets the canonical within the
    /// caller's channel.</para>
    /// </summary>
    private static NonCanonicalMatch? FindNonCanonicalLanguageInChannel(
        string path,
        string currentHost,
        string channelName,
        HostnameLookupIndex.Snapshot snapshot)
    {
        // Iterate the channel's hostnames + each one's languages. Typical
        // configs have a handful of hostnames per channel with 1-3 languages
        // each, so the total pass is tiny - and the prefix strings are
        // pre-interned so the comparisons allocate nothing.
        foreach (var hostEntry in snapshot.ByHostname.Values)
        {
            if (!string.Equals(hostEntry.ChannelName, channelName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string canonicalHost = hostEntry.Hostname.Hostname;
            bool sameHostAsRequest = string.Equals(canonicalHost, currentHost, StringComparison.OrdinalIgnoreCase);
            var prefixes = hostEntry.AllPrefixes;
            for (int i = 0; i < prefixes.Count; i++)
            {
                var lang = prefixes[i];
                if (!path.StartsWith(lang.SlashLangSlash, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(path, lang.SlashLang, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Already canonical? The non-root case requires both the
                // host AND prefix to be on the language's hostname; the
                // root case is never canonical because a root language
                // doesn't carry a prefix in user-facing URLs.
                if (!lang.IsRoot && sameHostAsRequest)
                {
                    return null;
                }

                string canonicalDisplay = lang.IsRoot ? string.Empty : lang.LanguageCode;
                return new NonCanonicalMatch(lang.LanguageCode, canonicalHost, canonicalDisplay);
            }
        }
        return null;
    }

    private readonly record struct NonCanonicalMatch(
        string LanguageCode,
        string CanonicalHost,
        string CanonicalDisplay);
}
