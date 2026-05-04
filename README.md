# Xperience by Kentico: Language Domains

[![Kentico Labs](https://img.shields.io/badge/Kentico_Labs-grey?labelColor=orange&logo=data:image/svg+xml;base64,PHN2ZyBjbGFzcz0ic3ZnLWljb24iIHN0eWxlPSJ3aWR0aDogMWVtOyBoZWlnaHQ6IDFlbTt2ZXJ0aWNhbC1hbGlnbjogbWlkZGxlO2ZpbGw6IGN1cnJlbnRDb2xvcjtvdmVyZmxvdzogaGlkZGVuOyIgdmlld0JveD0iMCAwIDEwMjQgMTAyNCIgdmVyc2lvbj0iMS4xIiB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciPjxwYXRoIGQ9Ik05NTYuMjg4IDgwNC40OEw2NDAgMjc3LjQ0VjY0aDMyYzE3LjYgMCAzMi0xNC40IDMyLTMycy0xNC40LTMyLTMyLTMyaC0zMjBjLTE3LjYgMC0zMiAxNC40LTMyIDMyczE0LjQgMzIgMzIgMzJIMzg0djIxMy40NEw2Ny43MTIgODA0LjQ4Qy00LjczNiA5MjUuMTg0IDUxLjIgMTAyNCAxOTIgMTAyNGg2NDBjMTQwLjggMCAxOTYuNzM2LTk4Ljc1MiAxMjQuMjg4LTIxOS41MnpNMjQxLjAyNCA2NDBMNDQ4IDI5NS4wNFY2NGgxMjh2MjMxLjA0TDc4Mi45NzYgNjQwSDI0MS4wMjR6IiAgLz48L3N2Zz4=)](https://github.com/Kentico/.github/blob/main/SUPPORT.md#labs-limited-support) [![CI: Build and Test](https://github.com/devotion-patrick/xperience-community-language-domains/actions/workflows/ci.yml/badge.svg)](https://github.com/devotion-patrick/xperience-community-language-domains/actions/workflows/ci.yml)

Hostname-based language switching for Xperience by Kentico website channels. Serve each language on its own domain (`en.example.com`, `uk.example.com`), keep the URLs clean (`uk.example.com/about` instead of `uk.example.com/uk/about`), and have the same rewriting reflected everywhere - including the admin URLs tab.

## Description

Stock Xperience routes non-default-language pages off a path prefix on the channel's primary domain (`example.com/uk/about`). This package extends that with a hostname-based model:

- **Inbound**: each language can have its own dedicated host. The middleware translates clean URLs into the language-prefixed paths that Kentico's URL-path routing actually stores.
- **Outbound**: the URL retriever decorator strips the language prefix from generated links and rewrites the absolute URL onto the language's primary host.
- **Admin**: the URLs tab in admin shows the same rewritten URLs the live site renders, via a `DispatchProxy`-based decorator over the (internal) URL list-items service.
- **One source of truth for hostnames**: hosts listed under `XperienceCommunity:LanguageDomains` are auto-merged into Kentico's `WebsiteChannelDomainOptions` so you don't have to maintain them twice.
- **Indexed hot path**: a singleton `HostnameLookupIndex` precomputes hostname / language dictionaries and pre-interned `/{lang}/` prefix strings at startup, so per-request matching and per-link URL rewriting are O(1) hash lookups against zero-allocation comparisons. Rebuilds on `IOptionsMonitor` change for hot-reload. See [Usage Guide → Performance](./docs/Usage-Guide.md#performance).

Configuration is hostname-first: per channel, you list the hostnames that serve it, and for each hostname you name the languages reachable on it plus the one that's served at the host's root.

- The host's **root language** is served at `/` (no prefix in user-facing URLs). E.g. `uk.example.com/about`.
- Every **non-root** language listed on the same hostname is reached via a path prefix that is literally the language codename. E.g. with codename `au`, `en.example.com/au/about`.

A single host can serve one root language plus N non-root languages on the same domain - the typical multi-region setup where one channel hosts e.g. `domain.eu` (English root + `/fr` + `/de`) and `domain.com` (US root + `/ca`).

By design, **a language can only appear under one hostname per channel**. Serving the same content on multiple domains is duplicate-content / bad SEO; the validator rejects it. Use edge-level 301 redirects if you need vanity-domain aliases.

## Supported use cases

| Shape | Example | Use when |
|---|---|---|
| **Single-language hostname** | `en.example.com` -> `en`, `es.example.com` -> `es` | Each language has its own dedicated host. |
| **Multi-language hostname** | `en.example.com` for `en` (root) + `/au/...` for `au` | A region needs sub-locales without a separate domain. |
| **Multi-region multi-host channel** | `domain.eu` (`british` root + `/french` + `/german`) and `domain.com` (`american` root + `/mexican`) | Multiple regional brands within a single channel. |
| **Hostname with no root language** | `alt.example.com` with `Languages: ["en", "fr"]` and no `RootLanguage` | Multiple domains for the same channel where non-primary domains should behave like stock Kentico (default lang at `/`, others at `/{lang}/...`). |
| **Behind a reverse proxy / CDN** | `ForwardedHostHeader: "X-Forwarded-Host"` | Origin sees an internal hostname; public host is in a forwarded header. |
| **Strict-startup mode** | `StrictValidation: true` | Production: fail fast on misconfig instead of silently dropping a channel. |
| **Hot-reloadable config** | `appsettings.json` change | Routing table updates without an app restart. |
| **Custom path exclusions** | `AdditionalExcludedPathPrefixes: ["/health", "/webhooks/"]` | Non-page routes that shouldn't go through the language router. |

**Deliberately out of scope:** the same language on multiple hostnames per channel (duplicate content - validator rejects), subpath-only routing (that's stock Kentico). The Kentico language codename **is** the URL path prefix - codenames are arbitrary strings, so set the codename to whatever you want users to see (`german`, `au`, `en-AU`, ...). Full list and rationale in the [Usage Guide](./docs/Usage-Guide.md#out-of-scope-deliberately-not-supported).

## Requirements

### Library Version Matrix

| Xperience Version | Library Version |
| ----------------- | --------------- |
| >= 31.4.3         | 0.1.0           |

### Dependencies

- [ASP.NET Core 8.0](https://dotnet.microsoft.com/en-us/download)
- [Xperience by Kentico](https://docs.kentico.com)

## Package Installation

```powershell
dotnet add package XperienceCommunity.LanguageDomains
```

## Quick Start

**1.** Add the configuration section in `appsettings.json`:

```json
{
  "XperienceCommunity": {
    "LanguageDomains": {
      "Channels": {
        "DancingGoatCore": {
          "Hostnames": [
            {
              "Hostname": "en.example.com",
              "RootLanguage": "en",
              "Languages": [ "en", "au" ]
            },
            {
              "Hostname": "uk.example.com",
              "RootLanguage": "uk",
              "Languages": [ "uk" ]
            }
          ]
        }
      }
    }
  }
}
```

`en.example.com` serves `en` at `/` and Australian English (`au`) at `/au/...`. `uk.example.com` serves `uk` at `/`. Inbound, the path-prefix middleware prepends `/{langcode}` for non-channel-primary languages so Kentico's URL-path routing finds the stored slug; outbound, the URL retriever decorator strips it again so links render bare.

> **The strings in `RootLanguage` and `Languages` are Kentico content language *codenames*** - whatever you set as the codename when creating the language in the Kentico admin UI. They are not required to be ISO 639 / BCP-47 culture codes; codenames like `au`, `german`, or `default` work just as well. The package matches them verbatim against Kentico's stored language codenames, and uses them as the user-facing URL path prefix for non-root languages.

Validation runs at startup. Each channel is checked for hostname uniqueness, a non-empty `RootLanguage` that appears in `Languages`, and the "one canonical hostname per language per channel" rule. Invalid channels are logged at `Error` and dropped from the effective options (routing for those channels falls back to stock Kentico behavior); set `StrictValidation: true` on the options to fail startup instead.

> **Note:** you do **not** need to list these hostnames under Kentico's `WebsiteChannelDomains` config as well - the package auto-merges them into `WebsiteChannelDomainOptions.DomainOverrides[<channel>].Domains` at startup. Anything already in `WebsiteChannelDomains` (legacy aliases, vanity hostnames not used for language routing) is preserved - we union onto, never replace. Listing the same host in both sections is fine: the merge is case-insensitive and de-duplicates.

**2.** In `Program.cs`, wire the package up:

```csharp
using XperienceCommunity.LanguageDomains.Extensions;
using XperienceCommunity.LanguageDomains.Middleware;

// 1) Bind options + register the decorators (via [assembly: RegisterImplementation])
//    + auto-merge hosts into WebsiteChannelDomainOptions.
builder.Services.AddHostnameCultureMapping(builder.Configuration);

// 2) (Optional) Decorate the internal admin URL-list-items retriever via
//    DispatchProxy so the URLs tab in admin shows the rewritten URLs too.
//    Must be the last builder.Services.* call before builder.Build().
builder.Services.AddHostnameAwareUrlListItemsRetrieverDecorator();

var app = builder.Build();

// 3) Middleware pair, both BEFORE app.UseKentico(). The canonical-redirect
//    runs first so it sees the user's actual URL; the path-prefix middleware
//    rewrites Request.Path into Kentico's storage form, which would
//    otherwise mask the user-typed URL from the redirect logic.
//    - 301 e.g. en.example.com/uk/page -> uk.example.com/page
app.UseMiddleware<HostnameCultureCanonicalRedirectMiddleware>();
//    - Translate display URL into storage form: prepend /<langcode>
//      on root hosts for non-primary languages so Kentico's URL-path
//      routing matches the stored slug.
app.UseMiddleware<HostnameCulturePathPrefixMiddleware>();
app.UseKentico();
```

That's the entire setup. The decorators register themselves through `[assembly: RegisterImplementation]` and Kentico's class discovery (this package is `[assembly: AssemblyDiscoverable]`).

### Behind a reverse proxy / CDN (Kentico SaaS, Cloudflare, AFD, AWS ALB, ...)

If your origin sits behind a CDN/proxy that **doesn't preserve the `Host` header**, `Request.Host` is the internal load-balancer hostname rather than the public one - hostname-based rules will match nothing.

The recommended fix is the standard ASP.NET Core approach in your host's `Program.cs`:

```csharp
using Microsoft.AspNetCore.HttpOverrides;

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedHost |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedFor;

    // Trust the proxy CIDR(s); empty defaults trust nothing.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    // options.KnownNetworks.Add(new IPNetwork(IPAddress.Parse("..."), prefix));
});

// ... after builder.Build() ...
app.UseForwardedHeaders();   // BEFORE every other middleware that reads Request.Host
app.UseMiddleware<HostnameCultureCanonicalRedirectMiddleware>();
app.UseMiddleware<HostnameCulturePathPrefixMiddleware>();
app.UseKentico();
```

That fixes `Request.Host` for every middleware in the app, not just this package.

**If you can't or don't want to wire `UseForwardedHeaders` globally**, this package has an escape hatch - point `ForwardedHostHeader` at whichever header your proxy sets:

```json
"XperienceCommunity": {
  "LanguageDomains": {
    "ForwardedHostHeader": "X-Forwarded-Host",
    "Channels": { ... }
  }
}
```

When set, the package reads its hostname from this header (taking the first entry of any comma-separated proxy chain), falling back to `Request.Host` if the header is missing on a given request. Default is unset (use `Request.Host` as ASP.NET resolved it).

Either way: when investigating "rules aren't matching the right hostname", drop `XperienceCommunity.LanguageDomains` to `Debug` and the per-request log lines include the effective host plus the raw values of `Host`, `X-Forwarded-Host`, `X-Original-Host`, and `X-Forwarded-Proto` so you can see exactly what the proxy delivered.

### Disabling the redirect

The canonical redirect can be disabled globally without removing the middleware - set `EnableCanonicalRedirect: false` on the options:

```json
"XperienceCommunity": {
  "LanguageDomains": {
    "EnableCanonicalRedirect": false,
    "Channels": { ... }
  }
}
```

Useful when you want canonical URLs only on outbound links but prefer to render whichever URL the visitor typed. Default: `true`.

### Logging

The package logs through standard `ILogger<T>`. Information-level entries fire only when a rule actually applied to the request - excluded paths (assets, admin endpoints, the API) and pass-through cases stay silent so logs don't flood.

| Category | Level | When |
|---|---|---|
| `...Middleware.HostnameCultureCanonicalRedirectMiddleware` | Information | Each 301 redirect issued. |
| `...Middleware.HostnameCultureCanonicalRedirectMiddleware` | Debug | Every page request being assessed (one per page request, with full host/header context). |
| `...Middleware.HostnameCulturePathPrefixMiddleware` | Information | Each request whose path is mutated to add the language prefix. |
| `...Middleware.HostnameCulturePathPrefixMiddleware` | Debug | Every page request being assessed. |
| `...UrlListRewriter.UrlListItemHostnameRewriter` | Information | Each admin URLs-tab call. |
| `...Decorators.HostnameAwareWebPageUrlRetriever` | Debug | Each outbound URL the decorator actually rewrites. High volume - leave at Info+ unless investigating. |
| `...Decorators.HostnameAwarePreferredLanguageRetriever` | Debug | Each call that resolved a language from query string or hostname. High volume. |

Every middleware log line includes the **effective host** (what rules matched against) and the **raw forwarded headers** (`RequestHost`, `XForwardedHost`, `XOriginalHost`, `XForwardedProto`) - so when "the rule isn't firing on the host I expect" investigations, the log line tells you exactly what the proxy / CDN actually delivered.

Sample Information-level lines:

```
info: ...HostnameCultureCanonicalRedirectMiddleware[0]
      Canonical 301: GET https://en.example.com/uk/articles -> https://uk.example.com/articles (lang=uk) | RequestHost=en.example.com XForwardedHost=(null) XOriginalHost=(null) XForwardedProto=(null)
info: ...HostnameCulturePathPrefixMiddleware[0]
      Path-prefix translate: effectiveHost=uk.example.com /articles -> internal path /uk/articles (lang=uk, display=, storage=/uk) | RequestHost=uk.example.com XForwardedHost=(null) XForwardedProto=(null)
```

Set `Logging:LogLevel:XperienceCommunity.LanguageDomains` to `Debug` in `appsettings.<env>.json` while investigating, then drop it back. At Debug, every page request through the middlewares logs an "assess" line with the full host/header snapshot, so you can see why a rule didn't match (wrong header? proxy not preserving Host? stale config?) without rerunning the request.

## Full Instructions

View the [Usage Guide](./docs/Usage-Guide.md) for the field-by-field configuration reference, the inbound/outbound behavior matrix, and the design notes that explain why the decorators live in this assembly (and why `DispatchProxy` is unavoidable for the admin URLs tab).

A working sample under [`examples/`](./examples/) shows the package wired into a Dancing Goat-style site.

## Contributing

To see the guidelines for Contributing to Kentico open source software, please see [Kentico's `CONTRIBUTING.md`](https://github.com/Kentico/.github/blob/main/CONTRIBUTING.md) for more information and follow the [Kentico's `CODE_OF_CONDUCT`](https://github.com/Kentico/.github/blob/main/CODE_OF_CONDUCT.md).

Instructions and technical details for contributing to **this** project can be found in [Contributing Setup](./docs/Contributing-Setup.md).

## License

Distributed under the MIT License. See [`LICENSE.md`](./LICENSE.md) for more information.

## Support

[![Kentico Labs](https://img.shields.io/badge/Kentico_Labs-grey?labelColor=orange&logo=data:image/svg+xml;base64,PHN2ZyBjbGFzcz0ic3ZnLWljb24iIHN0eWxlPSJ3aWR0aDogMWVtOyBoZWlnaHQ6IDFlbTt2ZXJ0aWNhbC1hbGlnbjogbWlkZGxlO2ZpbGw6IGN1cnJlbnRDb2xvcjtvdmVyZmxvdzogaGlkZGVuOyIgdmlld0JveD0iMCAwIDEwMjQgMTAyNCIgdmVyc2lvbj0iMS4xIiB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciPjxwYXRoIGQ9Ik05NTYuMjg4IDgwNC40OEw2NDAgMjc3LjQ0VjY0aDMyYzE3LjYgMCAzMi0xNC40IDMyLTMycy0xNC40LTMyLTMyLTMyaC0zMjBjLTE3LjYgMC0zMiAxNC40LTMyIDMyczE0LjQgMzIgMzIgMzJIMzg0djIxMy40NEw2Ny43MTIgODA0LjQ4Qy00LjczNiA5MjUuMTg0IDUxLjIgMTAyNCAxOTIgMTAyNGg2NDBjMTQwLjggMCAxOTYuNzM2LTk4Ljc1MiAxMjQuMjg4LTIxOS41MnpNMjQxLjAyNCA2NDBMNDQ4IDI5NS4wNFY2NGgxMjh2MjMxLjA0TDc4Mi45NzYgNjQwSDI0MS4wMjR6IiAgLz48L3N2Zz4=)](https://github.com/Kentico/.github/blob/main/SUPPORT.md#labs-limited-support)

This project has **Kentico Labs limited support**.

See [`SUPPORT.md`](https://github.com/Kentico/.github/blob/main/SUPPORT.md#labs-limited-support) for more information.

For any security issues see [`SECURITY.md`](https://github.com/Kentico/.github/blob/main/SECURITY.md).
