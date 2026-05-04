using Microsoft.AspNetCore.Http;

using XperienceCommunity.LanguageDomains.Configuration;

namespace XperienceCommunity.LanguageDomains.Internal;

/// <summary>
/// Resolves the hostname this package's rules should be evaluated against,
/// and surfaces the related forwarded headers for diagnostic logging.
///
/// In a SaaS / CDN deployment (Kentico SaaS, Cloudflare, Azure Front Door,
/// AWS ALB, nginx, HAProxy, ...) the inbound <c>Host</c> header may be the
/// origin's internal hostname rather than the public one the visitor typed.
/// The standard ASP.NET Core fix is <c>app.UseForwardedHeaders()</c> with
/// <c>ForwardedHeaders.XForwardedHost</c> in
/// <see cref="Microsoft.AspNetCore.Builder.ForwardedHeadersOptions"/>; once
/// that runs, <see cref="HttpRequest.Host"/> already carries the public host
/// and this resolver returns it unchanged.
///
/// When the host project cannot or does not want to wire
/// <c>UseForwardedHeaders</c> globally, set
/// <see cref="HostnameCultureMappingOptions.ForwardedHostHeader"/> (e.g.
/// <c>"X-Forwarded-Host"</c>) and this resolver will read the value from
/// that header instead.
/// </summary>
internal static class RequestHostResolver
{
    /// <summary>
    /// Resolves the host that rule matching should be performed against.
    /// Pass <paramref name="forwardedHostHeader"/> from the index snapshot
    /// (<see cref="HostnameLookupIndex.Snapshot.ForwardedHostHeader"/>) so
    /// consumers don't have to hold both an <c>IOptionsMonitor</c> and the
    /// index just to read this one flag.
    /// </summary>
    public static string? GetEffectiveHost(HttpRequest request, string? forwardedHostHeader)
    {
        if (!string.IsNullOrWhiteSpace(forwardedHostHeader))
        {
            string raw = request.Headers[forwardedHostHeader].ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                // X-Forwarded-Host may carry a comma-separated proxy chain;
                // the original client-facing host is the first entry.
                string first = raw.Split(',', 2)[0].Trim();
                if (!string.IsNullOrEmpty(first))
                {
                    return first;
                }
            }
        }
        return request.Host.Value;
    }

    /// <summary>
    /// Resolves the request's effective scheme. Mirrors the host-resolution
    /// rule but reads <c>X-Forwarded-Proto</c> (only when a forwarded host
    /// header is configured - otherwise we trust whatever
    /// <c>UseForwardedHeaders</c> already populated).
    /// </summary>
    public static string GetEffectiveScheme(HttpRequest request, string? forwardedHostHeader)
    {
        if (!string.IsNullOrWhiteSpace(forwardedHostHeader))
        {
            string proto = request.Headers["X-Forwarded-Proto"].ToString();
            if (!string.IsNullOrWhiteSpace(proto))
            {
                string first = proto.Split(',', 2)[0].Trim();
                if (!string.IsNullOrEmpty(first))
                {
                    return first;
                }
            }
        }
        return request.Scheme;
    }

    /// <summary>
    /// Captures the raw hostname-related headers for diagnostic logging. Use
    /// the <c>@</c> prefix in a structured log message to attach as a single
    /// JSON-serialised field, or pull individual properties out by template
    /// placeholders.
    /// </summary>
    public static HostHeaderSnapshot Snapshot(HttpRequest request) => new(
        RequestHost: request.Host.Value,
        XForwardedHost: HeaderOrNull(request, "X-Forwarded-Host"),
        XOriginalHost: HeaderOrNull(request, "X-Original-Host"),
        XForwardedProto: HeaderOrNull(request, "X-Forwarded-Proto"),
        XForwardedFor: HeaderOrNull(request, "X-Forwarded-For"));

    private static string? HeaderOrNull(HttpRequest request, string name)
    {
        string v = request.Headers[name].ToString();
        return string.IsNullOrEmpty(v) ? null : v;
    }
}

/// <summary>
/// Snapshot of the hostname-related request headers, attached to log entries
/// so a misconfigured proxy can be diagnosed without re-running the request.
/// </summary>
internal readonly record struct HostHeaderSnapshot(
    string RequestHost,
    string? XForwardedHost,
    string? XOriginalHost,
    string? XForwardedProto,
    string? XForwardedFor);
