namespace XperienceCommunity.LanguageDomains.Configuration;

/// <summary>
/// Options binding for the <c>XperienceCommunity:LanguageDomains</c> appsettings
/// section. Maps website channels to the hostnames that serve them and, for
/// each hostname, the languages reachable on it (with one designated as the
/// root language served at <c>/</c>).
/// </summary>
public class HostnameCultureMappingOptions
{
    /// <summary>
    /// Configuration section path. Per Xperience community-package convention,
    /// settings live under a <c>XperienceCommunity</c> parent key.
    /// </summary>
    public const string SectionKey = "XperienceCommunity:LanguageDomains";

    /// <summary>
    /// Channel code name (e.g. <c>"DancingGoatPages"</c>) -> mapping describing
    /// the hostnames that serve the channel.
    /// </summary>
    public Dictionary<string, ChannelHostnameMapping> Channels { get; set; } = [];

    /// <summary>
    /// When <c>true</c> (the default), the canonical-redirect middleware
    /// 301-redirects requests with a language path prefix on the wrong host
    /// (or with the prefix kept on the language's root host) to the canonical
    /// clean URL. Set to <c>false</c> to disable the redirect entirely while
    /// keeping inbound prefix manipulation and outbound URL rewriting active.
    /// </summary>
    public bool EnableCanonicalRedirect { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, an invalid channel configuration fails app startup
    /// (via <c>ValidateOnStart</c>). When <c>false</c> (the default), invalid
    /// channels are logged at <c>Error</c> level and silently dropped from the
    /// effective options - the rest of the app stays up, the broken channel
    /// falls back to stock Kentico routing.
    /// </summary>
    public bool StrictValidation { get; set; }

    /// <summary>
    /// Additional URL path prefixes the package should ignore - merged on top
    /// of the package-default list (which already covers Kentico admin,
    /// content-asset endpoints, Razor static content, etc.). Use this for
    /// custom routes that should never be touched by language-routing rules:
    /// webhooks, health checks, custom API surfaces, etc.
    ///
    /// <para>Each entry is a leading-slash prefix (case-insensitive). A path
    /// is excluded when it starts with the entry; trailing slashes are
    /// honoured. Example: <c>"/health"</c> matches <c>/health</c>,
    /// <c>/healthz</c>, and <c>/health/check</c>; use <c>"/health/"</c> if
    /// you want only sub-paths of <c>/health/</c>.</para>
    /// </summary>
    public List<string> AdditionalExcludedPathPrefixes { get; set; } = [];

    /// <summary>
    /// Optional header name to read the public-facing hostname from when this
    /// app sits behind a reverse proxy / CDN that does not preserve the
    /// <c>Host</c> header (Kentico SaaS, Azure Front Door, AWS ALB, custom
    /// nginx, ...). When set (e.g. <c>"X-Forwarded-Host"</c>), all rule
    /// matching in this package reads the host from this header instead of
    /// <see cref="Microsoft.AspNetCore.Http.HttpRequest.Host"/>; if the header
    /// is missing or empty on a given request, the package falls back to
    /// <see cref="Microsoft.AspNetCore.Http.HttpRequest.Host"/>.
    ///
    /// <para>Most deployments should prefer the standard ASP.NET Core
    /// approach instead: configure
    /// <see cref="Microsoft.AspNetCore.Builder.ForwardedHeadersOptions"/> to
    /// include <c>XForwardedHost</c>, list trusted proxies, and call
    /// <c>app.UseForwardedHeaders()</c> early in the pipeline. That fixes
    /// <see cref="Microsoft.AspNetCore.Http.HttpRequest.Host"/> for every
    /// middleware in the app, not just this package.</para>
    /// </summary>
    public string? ForwardedHostHeader { get; set; }
}

/// <summary>
/// Per-channel mapping under <see cref="HostnameCultureMappingOptions.Channels"/>.
/// </summary>
public class ChannelHostnameMapping
{
    /// <summary>
    /// Hostnames that serve this channel. Each hostname designates exactly
    /// one root language (served at <c>/</c>) plus zero or more non-root
    /// languages reached via path prefixes (e.g. <c>/au/...</c>).
    /// </summary>
    public List<HostnameMapping> Hostnames { get; set; } = [];
}

/// <summary>
/// Single hostname under
/// <see cref="ChannelHostnameMapping.Hostnames"/>. Names the host (e.g.
/// <c>"en.example.com"</c>), the languages reachable on it, and the one
/// served at the host's root.
///
/// <para>Validation: when <see cref="RootLanguage"/> is set it must appear
/// in <see cref="Languages"/>; <see cref="Hostname"/> must be unique within
/// the channel; a given language can only appear under one hostname per
/// channel (same content on multiple hostnames is duplicate-content / bad
/// SEO and is rejected by design).</para>
/// </summary>
public class HostnameMapping
{
    /// <summary>
    /// The hostname (host[:port]) that serves the languages listed below.
    /// </summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>
    /// Codename of the language served at the root of this hostname (no path
    /// prefix in user-facing URLs). When set, must also appear in
    /// <see cref="Languages"/>.
    ///
    /// <para><strong>Optional.</strong> Leave empty (or omit from
    /// appsettings) when no language should claim the bare <c>/</c> on this
    /// host - every configured language is then reached via its
    /// <c>/{langcode}/...</c> prefix, and requests to the host's bare root
    /// fall through to Kentico's stock routing (which serves the channel's
    /// primary content language). This shape is useful when you have
    /// multiple domains for the same channel and want each to behave like
    /// stock Kentico path-prefix routing rather than designating a different
    /// language as the host's root.</para>
    /// </summary>
    public string RootLanguage { get; set; } = string.Empty;

    /// <summary>
    /// Codenames of every language served on this hostname. The codenames are
    /// matched verbatim against Kentico's stored <c>ContentLanguageName</c>
    /// values and double as the user-facing path prefix for non-root
    /// languages (the codename <c>au</c> is reached at
    /// <c>{Hostname}/au/...</c>).
    ///
    /// <para>Codenames here are not required to be ISO 639 / BCP-47 culture
    /// codes - they are whatever you set as the language codename in Kentico
    /// admin. Codenames like <c>au</c>, <c>german</c>, or <c>default</c> are
    /// valid.</para>
    /// </summary>
    public List<string> Languages { get; set; } = [];
}
