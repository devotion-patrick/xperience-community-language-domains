# Usage Guide

This guide covers everything beyond the [README](../README.md) Quick Start: the full configuration schema, the inbound/outbound behavior matrix, the design notes that explain why each piece exists, and the gotchas worth knowing about.

## What it does

| Concern | Stock XbyK | With this package |
|---|---|---|
| Which language to serve? | Determined by the URL path prefix (e.g. `/uk/...`) and the channel's primary content language. | Determined by the hostname. `uk.example.com` -> `uk`, `en.example.com` -> `en`. |
| Inbound URL `uk.example.com/Home` | Treated as the `Home` URL path in the default language; UK content not found. | Middleware prepends `/uk` so Kentico's URL-path routing finds the UK variant (`uk/Home`). |
| Outbound link generation | `IWebPageUrlRetriever` returns `~/uk/Home` against the channel's primary domain. | Decorator strips `/uk/` and swaps the absolute URL to `https://uk.example.com/Home`. |
| Admin URLs tab "System URL" | Built directly from `WebPageUrlPath` + `IWebsiteChannelDomainProvider`, bypassing `IWebPageUrlRetriever`. | A separate `DispatchProxy` decorates the internal `IWebPageUrlListItemsRetriever` so the admin shows hostname-rewritten URLs too. |
| `WebsiteChannelDomainOptions` | Maintained separately in `appsettings.json`. | Auto-populated from this package's config so hostnames live in one place. |

## Supported use cases

The shapes the package is designed to handle, in increasing complexity:

### 1. Single-language hostname (the simplest case)

Each language gets its own dedicated host. Bare URLs everywhere.

```json
"DancingGoatPages": {
  "Hostnames": [
    { "Hostname": "en.example.com", "RootLanguage": "en", "Languages": [ "en" ] },
    { "Hostname": "es.example.com", "RootLanguage": "es", "Languages": [ "es" ] }
  ]
}
```

`en.example.com/about` -> English `About`. `es.example.com/about` -> Spanish `About`. No `/{lang}/` prefixes anywhere in user-facing URLs.

### 2. Multi-language hostname (one root + N path-prefixed languages)

A hostname serves a primary language at `/` plus additional languages reached via `/{langcode}/...` prefixes.

```json
"DancingGoatPages": {
  "Hostnames": [
    {
      "Hostname": "en.example.com",
      "RootLanguage": "en",
      "Languages": [ "en", "au" ]
    }
  ]
}
```

`en.example.com/about` -> `en` content. `en.example.com/au/about` -> `au` content (Australian English). The non-root language's URL prefix is the language codename verbatim.

### 3. Multi-region multi-host channel

One channel, multiple hostnames, each with its own root language and optional non-root languages. Typical for sites with regional sub-brands. The example below uses descriptive codenames to reinforce that codenames are arbitrary strings - the codename is what shows up verbatim as the URL prefix:

```json
"DancingGoatPages": {
  "Hostnames": [
    {
      "Hostname": "domain.eu",
      "RootLanguage": "british",
      "Languages": [ "british", "french", "german" ]
    },
    {
      "Hostname": "domain.com",
      "RootLanguage": "american",
      "Languages": [ "american", "mexican" ]
    }
  ]
}
```

Resulting URLs: `domain.eu/` (British root), `domain.eu/french/about`, `domain.eu/german/about`, `domain.com/` (American root), `domain.com/mexican/about`. Each Kentico content language (with its arbitrary codename) appears under exactly one hostname - the "one canonical hostname per language per channel" rule.

### 4. Hostname with no root language (stock-Kentico path-prefix routing on a non-primary domain)

Omit `RootLanguage` (or leave it empty) and the hostname has no language at `/`. Every language listed under it is reached via its `/{langcode}/...` prefix; bare-root requests fall through to Kentico's stock routing, which serves the channel's primary content language.

```json
"DancingGoatPages": {
  "Hostnames": [
    {
      "Hostname": "alt.example.com",
      "Languages": [ "en", "fr", "german" ]
    }
  ]
}
```

Resulting URLs on `alt.example.com`:
- `alt.example.com/` -> primary content language (whatever Kentico has configured for the channel).
- `alt.example.com/en/about` -> `en` content.
- `alt.example.com/fr/about` -> `fr` content.
- `alt.example.com/german/about` -> `german` content.

Use this when you have multiple domains for the same channel and want each non-primary domain to behave like stock Kentico (path-prefix routing for non-default languages, default language at `/`) rather than designating a *different* language as the host's root. Validation still enforces that any value you do put in `RootLanguage` appears in `Languages` - the field is optional but typo-resistant when set.

> **⚠ Gotcha - don't list the channel's primary content language under a no-root host.** Listing the primary in `Languages` creates a second canonical for it: stock Kentico serves the primary at `/about` (no prefix - that's how `WebPageUrlPath` is stored for the primary), and our path-prefix middleware additionally accepts `/{primary}/about` and rewrites it to `/about` for routing. Both URLs resolve to the same page on the same host - a duplicate-content situation search engines see as two distinct URLs.
>
> The package can't auto-detect this at config-validation time because the channel's primary content language is database state (resolved at runtime by `ChannelPrimaryLanguageResolver`), not part of the appsettings binding. **Recommended fix: omit the primary from `Languages`** - Kentico's stock routing serves it at `/` natively, no declaration needed. **If you must list the primary** (e.g. you want explicit-prefix URLs for it on this host), handle the second canonical with **edge-level 301 redirects** in your reverse proxy / CDN: pick one canonical (`/about` or `/{primary}/about`) and 301 the other to it. This package does not currently issue that redirect itself.

### 5. Cross-channel hostname reuse

Different channels can share a hostname (Kentico routes to whichever channel registered it). Validation is per-channel - the same hostname / language can appear in different channels independently. Less common; mostly for migrations.

### 6. Behind a reverse proxy / CDN

Set `ForwardedHostHeader` (e.g. `"X-Forwarded-Host"`) when the inbound `Host` header is the internal load-balancer hostname rather than the public one. See the [Behind a reverse proxy / CDN](#behind-a-reverse-proxy--cdn) section.

### 7. Strict-startup mode

Set `StrictValidation: true` to fail app startup on any channel misconfiguration instead of silently dropping the channel. Recommended for production where a missed config typo shouldn't degrade silently.

### 8. Custom path exclusions

Add `AdditionalExcludedPathPrefixes` to the options to skip non-page routes (webhooks, health checks, custom API surfaces) on top of the package defaults (Kentico admin / API / static).

### 9. Hot-reload of options

The lookup index subscribes to `IOptionsMonitor.OnChange`, so an `appsettings.json` reload (or any other options change) rebuilds the snapshot transparently. No app restart needed for routing-table changes; only Kentico content state (channel primary language, etc.) is cached app-lifetime.

### Out of scope (deliberately not supported)

- **The same language served on multiple hostnames in a single channel** - duplicate content is bad SEO. Validation rejects it. Use edge-level 301s for vanity-domain aliases.
- **Subpath-only routing without hostname distinction** - that's stock Kentico (`example.com/uk/about`). The package adds value when each language is on its own host or path-prefixed under a multi-language host; if you only ever route via path prefix on a single host, you don't need this package.
- **Per-language port differences** in the schema - put the differentiation in the hostname (`uk.example.com`, `fr.example.com`) or solve at the proxy layer.
- **HTTPS dev-cert provisioning for custom hostnames** - infrastructure concern. For local dev, use `*.localtest.me` hostnames with HTTP and a port.
- **Special handling of preview/draft URLs** - we delegate to Kentico's stock retriever; preview URLs go through the same rewriting as live URLs, no special-case logic.

> **On URL prefixes:** the language codename you assigned in Kentico admin **is** the URL path prefix for that language - verbatim, always. There is no separate "display prefix" or "URL slug" to configure. If you want users to see `/german/...` in URLs, you set the Kentico content language's codename to `german`. Codenames are arbitrary strings (subject only to Kentico's own codename rules); they are not required to be ISO 639 / BCP-47 culture codes.

### Local development hostnames

Hostname-based routing only works if the browser's request actually arrives with the hostname your config expects. There are two common ways to make custom hostnames resolve to your dev machine:

**1. `*.localtest.me` (recommended for local dev)**

`localtest.me` is a public DNS service that resolves every subdomain to `127.0.0.1`. So `en.dancinggoat.localtest.me`, `es.example.com.localtest.me`, anything-you-want.localtest.me - all hit your local machine, no setup required. The DancingGoat sample uses this:

```json
{ "Hostname": "en.dancinggoat.localtest.me:61154", "RootLanguage": "en", "Languages": ["en"] }
```

Just point Kestrel at any local interface (`http://localhost:port` or `http://*:port`) and navigate to `http://en.dancinggoat.localtest.me:port/`. Kestrel doesn't filter by `Host` header, so any request reaching the bound socket is accepted; this package's middleware then reads `Request.Host` and routes by it.

**2. Hosts file (when `localtest.me` won't fit)**

If you need a hostname `localtest.me` can't provide - e.g. you want to dev against the actual production hostname `en.example.com` so URL captures, screenshots, or analytics scripts match prod - add entries to the OS hosts file:

```
# Windows: C:\Windows\System32\drivers\etc\hosts (edit as Administrator)
# Mac/Linux: /etc/hosts (edit with sudo)
127.0.0.1  en.example.com
127.0.0.1  es.example.com
```

Then navigate to `http://en.example.com:port/` like normal. Same Kestrel binding (`localhost` or `*`) accepts the traffic; the package routes on the `Host` header.

**Production:** real DNS records pointing each hostname at the host (or load balancer in front of it). Same package config either way - the behavior is identical regardless of how the name resolved.

## Configuration

Settings live under the `XperienceCommunity:LanguageDomains` configuration key:

```json
{
  "XperienceCommunity": {
    "LanguageDomains": {
      "Channels": {
        "<channel-code-name>": {
          "Hostnames": [
            {
              "Hostname": "host[:port]",
              "RootLanguage": "<kentico-language-codename>",
              "Languages": [ "<kentico-language-codename>", "..." ]
            }
          ]
        }
      },
      "EnableCanonicalRedirect": true,
      "StrictValidation": false,
      "ForwardedHostHeader": null,
      "AdditionalExcludedPathPrefixes": []
    }
  }
}
```

### Field reference

**Top-level options** (`XperienceCommunity:LanguageDomains:`):

| Field | Type | Default | Notes |
|---|---|---|---|
| `Channels` | `Dictionary<channel, ChannelHostnameMapping>` | `{}` | Per-channel mapping; each value carries a `Hostnames` array. |
| `EnableCanonicalRedirect` | `bool` | `true` | When `true`, the canonical-redirect middleware 301s requests on the wrong host (or with a langcode prefix kept on the language's root host) to the canonical clean URL. Set to `false` to disable redirects globally while keeping inbound prefix prepending and outbound URL rewriting active. |
| `StrictValidation` | `bool` | `false` | When `true`, an invalid channel configuration fails app startup. When `false` (default), the channel is logged at `Error` and silently dropped from the effective options - the rest of the app stays up; the broken channel falls back to stock Kentico routing. |
| `AdditionalExcludedPathPrefixes` | `string[]` | `[]` | Extra URL path prefixes the package skips entirely (no matching, no redirect, no logging). Merged on top of the package-default list (Kentico admin / API / static-asset endpoints). Each entry is a leading-slash prefix; case-insensitive. Use for custom routes like webhooks, health checks, or non-page API surfaces. Example: `["/health", "/webhooks/"]`. |
| `ForwardedHostHeader` | `string?` | `null` | Optional. Header name to read the public-facing hostname from when this app sits behind a reverse proxy / CDN that does not preserve the `Host` header. See [Behind a reverse proxy / CDN](#behind-a-reverse-proxy--cdn) below. |

**Per-channel mapping** (`Channels:<channel>:`):

| Field | Type | Default | Notes |
|---|---|---|---|
| `Hostnames` | `HostnameMapping[]` | `[]` | The hostnames that serve this channel. Each carries a root language plus zero or more non-root languages reached via path prefixes. See below. |

**Per-hostname mapping** (`Channels:<channel>:Hostnames[*]:`):

> Codenames in `RootLanguage` and `Languages` are **Kentico content language codenames** - whatever you set as the codename when creating the language in admin. They are not required to be ISO 639 / BCP-47 culture codes; codenames like `au`, `german`, or `default` are valid. The package matches them verbatim against Kentico's stored language codenames and uses them as the user-facing URL path prefix for non-root languages.

| Field | Type | Default | Notes |
|---|---|---|---|
| `Hostname` | `string` | `""` | The hostname (host[:port]) that serves the languages listed below. Must be unique within the channel. |
| `RootLanguage` | `string` | `""` | Codename of the language served at `/` on this hostname (no display prefix). Must also appear in `Languages`. |
| `Languages` | `string[]` | `[]` | Codenames of every language served on this hostname. Non-root languages are reached at `/{codename}/...`. A given codename can only appear under one hostname per channel - the package rejects multiple hostnames serving the same content. |

The two prefixes the package juggles:
- **Display prefix** - what the user sees in the URL. Empty for the host's root language; otherwise `/{langcode}`.
- **Storage prefix** - what Kentico stores in `WebPageUrlPath`. Empty for the channel's primary content language, `/{langcode}` for every other language. The package reads the channel's primary language at runtime via Kentico's `IInfoProvider<ChannelInfo>` chain.

When the two differ, the path-prefix middleware translates display -> storage on inbound and the URL-retriever decorator translates storage -> display on outbound.

### Relationship with Kentico's `WebsiteChannelDomains` config

You do **not** need to list these hostnames under Kentico's `WebsiteChannelDomains` config section as well. `AddHostnameCultureMapping(...)` registers a `PostConfigure<WebsiteChannelDomainOptions>` hook that unions every `Hostname` listed under `XperienceCommunity:LanguageDomains:Channels:*:Hostnames[*]` into the matching `DomainOverrides[<channel>].Domains` entry at startup.

Mechanics:

| Listed in `WebsiteChannelDomains` | Listed in `XperienceCommunity:LanguageDomains` | Result |
|---|---|---|
| Yes | Yes | Single entry in `DomainOverrides[<channel>].Domains` (case-insensitive de-dupe). Package applies its language rules to it. |
| Yes | No | Stays in `DomainOverrides[<channel>].Domains` (the package preserves it). Package does **not** apply language rules to it - useful for legacy aliases / vanity hostnames not tied to a language. |
| No | Yes | Auto-added to `DomainOverrides[<channel>].Domains`. Package applies its language rules. |
| No | No | Not registered with Kentico - requests on that host won't resolve to this channel at all. |

The package only **adds** to `DomainOverrides`; it never removes or replaces. PostConfigure runs after the consumer's own `Configure<WebsiteChannelDomainOptions>(GetSection("WebsiteChannelDomains"))` call, so we see and keep whatever that section already populated.

Net practical advice: maintain hostnames in **one** of the two sections, not both. Use `XperienceCommunity:LanguageDomains` if the host is for language routing (the common case), `WebsiteChannelDomains` for non-language hostnames.

### Validation

Validation runs eagerly at startup (`ValidateOnStart()`). Each channel's `Hostnames` array is checked for the following rules:

- Every `Hostname` is non-empty.
- Hostnames are unique within the channel.
- `RootLanguage` is non-empty.
- `RootLanguage` appears in `Languages`.
- Every entry in `Languages` is non-empty.
- A given language codename appears under at most **one** hostname per channel - the "one canonical hostname per language" rule. Serving the same content on multiple hostnames is duplicate-content / bad SEO and is rejected by design; if you need vanity-domain aliases, do the 301 at the edge.

By default (`StrictValidation: false`), an invalid channel is logged at `Error` and silently dropped from the effective options - the rest of the app stays up, and the broken channel's routing falls back to stock Kentico behavior. Set `StrictValidation: true` to fail startup with an `OptionsValidationException` listing every error instead.

The hostname-first schema makes the "one root per host" constraint structural rather than something the validator has to enforce - you can't even spell two roots on the same host: each `HostnameMapping` carries a single `RootLanguage` field.

### Behavior matrix

| Mapping shape | Display prefix | Inbound (middleware) | Outbound (URL retriever / list items) |
|---|---|---|---|
| Root language, **is channel primary** | empty | Pass-through (storage prefix is also empty for the primary language). | Pass-through on the path. Still rebuild the absolute URL against the configured hostname. |
| Root language, non-primary | empty | Prepend `/<langcode>` so Kentico's URL-path routing finds the stored slug. | Strip `/<langcode>` from generated paths. Rebuild absolute URL host. |
| Non-root language (channel primary) | `/<langcode>` | Strip `/<langcode>` so Kentico routes against the primary's flat storage form. | Prepend `/<langcode>` to generated paths. Rebuild absolute URL host. |
| Non-root language, non-primary | `/<langcode>` | Pass-through (display prefix == storage prefix). | Pass-through on the path. Rebuild absolute URL host. |

## Components

```
src/XperienceCommunity.LanguageDomains/
├── Configuration/
│   └── HostnameCultureMappingOptions.cs                IOptions<> binding for the appsettings section
├── Decorators/
│   ├── HostnameAwarePreferredLanguageRetriever.cs      Decorator: hostname -> language code
│   └── HostnameAwareWebPageUrlRetriever.cs             Decorator: strips path prefix, rewrites absolute host
├── Middleware/
│   ├── HostnameCultureCanonicalRedirectMiddleware.cs   Inbound: 301 to canonical host/path when URL is non-canonical
│   └── HostnameCulturePathPrefixMiddleware.cs          Inbound: translates display URL -> storage URL Kentico routes against
├── UrlListRewriter/
│   ├── HostnameAwareUrlListItemsRetrieverProxy.cs      DispatchProxy: decorates internal admin service
│   └── UrlListItemHostnameRewriter.cs                  Logic the proxy delegates to
├── Internal/
│   ├── ChannelPrimaryLanguageResolver.cs               Channel name -> primary content language code (cached: ConcurrentDictionary)
│   ├── ExcludedPathPrefixes.cs                         Default skip-list (admin/api/static) + IsExcluded helper
│   ├── HostLanguageMatch.cs                            Result record + ContextItemKey for the HttpContext.Items stash
│   ├── HostnameLookupIndex.cs                          Singleton: precomputed dicts (host -> match, (channel, lang) -> hostname) + interned prefix strings
│   ├── PathHelpers.cs                                  Storage-prefix derivation + span-based segment strip / prepend
│   └── RequestHostResolver.cs                          Reads effective host/scheme honouring ForwardedHostHeader
└── Extensions/
    └── HostnameCultureMappingExtensions.cs             IServiceCollection extensions + validation + WebsiteChannelDomainOptions auto-merge
```

Each folder is its own namespace (`XperienceCommunity.LanguageDomains.Configuration`, `.Decorators`, `.Middleware`, `.UrlListRewriter`, `.Extensions`).

### `HostnameAwarePreferredLanguageRetriever`
Decorates `Kentico.Content.Web.Mvc.Routing.IPreferredLanguageRetriever`. Resolution order:
1. `?language=<code>` query string override (only honoured for codes configured for the active channel).
2. The `HostLanguageMatch` stashed on `HttpContext.Items` by `HostnameCulturePathPrefixMiddleware` (authoritative - computed against the user-typed URL, before any path rewrite).
3. A live `HostnameLookupIndex.Match` against the current request - safety net for paths the path-prefix middleware skipped (e.g. excluded admin/static paths) or setups where it isn't registered. Less reliable on multi-language hosts because by this point the path may already have been rewritten to its storage form.
4. Fall through to Kentico's stock retriever.

### `HostnameAwareWebPageUrlRetriever`
Decorates `CMS.Websites.IWebPageUrlRetriever`. Implements all seven `Retrieve` overloads. For each one:
1. Calls the inner retriever to get the stock `WebPageUrl`.
2. Resolves the **channel name** from whatever input is available (webpage item ID -> websitechannel -> channel; or the explicit `websiteChannelName` parameter where present).
3. If the channel + language pair is configured here, applies the rewrite logic.

The rewrite:
1. Splits off the leading `~` (Razor app-relative marker) before doing prefix surgery, then re-attaches.
2. Computes the display prefix (empty for the host's root language, `/<langcode>` otherwise) and the storage prefix (empty for the channel-primary language, `/<langcode>` for every other language).
3. If they differ, strips the storage prefix from the generated path and prepends the display prefix.
4. **Always** rebuilds the absolute URL against the language's configured `Hostname` - the inner retriever returns absolute URLs against the channel's primary domain, which is the wrong host for cross-language links.

### `HostnameCultureCanonicalRedirectMiddleware`
Pre-`UseKentico` 301 redirect. Triggers when a request path begins with `/{langcode}/` for a configured language and the current `(host, prefix)` doesn't match the language's canonical `(Hostname, display-prefix)`:
- Wrong host: `en.example.com/uk/page` -> `https://uk.example.com/page` (when `uk` is configured as the root of `uk.example.com`).
- Right host but kept the prefix on a root language: `uk.example.com/uk/page` -> `https://uk.example.com/page`.
- Wrong host for a non-root language: `en.example.com/fr/page` -> `https://domain.eu/fr/page` (when `fr` is configured as a non-root language on `domain.eu`).

Only acts on `GET`/`HEAD`. Globally togglable via `EnableCanonicalRedirect`. **Must run before `HostnameCulturePathPrefixMiddleware`** - that middleware translates `Request.Path` to its storage form, so once it runs we'd no longer see the user's actual URL and would either fail to detect the non-canonical case or trigger a redirect loop.

### `HostnameCulturePathPrefixMiddleware`
Pre-`UseKentico` request mutator. For each request, runs `HostnameLookupIndex.Match(host, path)` to find which language owns the (host, path) pair, then translates the user's display URL into the storage URL Kentico routes against:
- Strips the display prefix (empty for the host's root language, `/<langcode>` otherwise).
- Prepends the storage prefix (empty for the channel-primary language, `/<langcode>` for every other language).

Whether or not it rewrites, the middleware also stashes the `HostLanguageMatch` on `HttpContext.Items` so `HostnameAwarePreferredLanguageRetriever` can read it back without re-scanning a (possibly rewritten) path. Skips a hard-coded list of admin/api/asset paths.

### `HostnameAwareUrlListItemsRetrieverProxy` + `UrlListItemHostnameRewriter`
The admin URLs tab is special. It does **not** use `IWebPageUrlRetriever` to build the displayed URL strings - it talks to `Kentico.Xperience.Admin.Websites.UIPages.IWebPageUrlListItemsRetriever`, which constructs URLs from `WebPageUrlPathInfo` + `IWebsiteChannelDomainProvider` directly. To make the admin reflect the same hostname/prefix rewriting:
- `IWebPageUrlListItemsRetriever` is **`internal`** to Kentico's admin assembly. We can't reference it at compile time.
- `HostnameAwareUrlListItemsRetrieverProxy` extends `System.Reflection.DispatchProxy`. Its interface type is resolved via reflection at registration time and passed to `DispatchProxy.Create<TInterface, TProxy>()` (also via reflection).
- The proxy forwards every call to the inner instance. For `Retrieve(int webPageItemId, int languageId, CancellationToken)` (matched by name + arity at runtime, since we have no compile-time symbol), it post-processes the returned `IEnumerable<UrlListItem>` through `UrlListItemHostnameRewriter`.

### `HostnameCultureMappingExtensions`
`IServiceCollection` extensions:
- `AddHostnameCultureMapping(configuration)` - binds options, validates each channel's `Hostnames` array (hostname uniqueness, `RootLanguage` is in `Languages`, no duplicate canonical hostnames per language), registers `IChannelPrimaryLanguageResolver`, and `PostConfigure`s `WebsiteChannelDomainOptions` to merge configured hostnames into the channel's domain list. Existing entries from the `WebsiteChannelDomains` config section are preserved; we just union our hosts onto them. Invalid channels are logged and dropped (or rejected at startup if `StrictValidation: true`).
- `AddHostnameAwareUrlListItemsRetrieverDecorator()` - reflection-based registration that swaps Kentico's stock `IWebPageUrlListItemsRetriever` for a factory that builds the dispatch proxy.

## Performance

The package is on the request-and-render hot path - both middlewares run per request, the URL-retriever decorator runs **per generated link** (often dozens of times per page), and Kentico calls `IPreferredLanguageRetriever.Get()` many times per request. The design favours O(1) lookups against precomputed data over per-request scans.

### `HostnameLookupIndex`

A singleton service (`Internal/HostnameLookupIndex.cs`) materialises the validated options snapshot into three lookup structures, built once at startup:

| Structure | Shape | Used by |
|---|---|---|
| `ByHostname` | `Dictionary<string, HostnameEntry>` (case-insensitive) | Inbound matching - `index.Match(host, path)` is an O(1) hash + O(language-count) prefix probe per request (non-root prefixes win on the first pass; root falls back). |
| `ByChannelAndLanguage` | `Dictionary<(channel, lang), OutboundEntry>` (lower-cased keys) | Outbound URL rewriting - `index.FindForLanguage(channel, lang)` is an O(1) hash lookup, called once per generated URL. |
| `ByHostname[host].AllPrefixes` | `IReadOnlyList<LanguagePrefix>` with pre-interned `/lang` and `/lang/` strings, root entries flagged | Canonical-redirect scanning - the middleware filters `ByHostname.Values` by channel and walks each entry's prefix list, with no per-iteration string allocation. |

Each `LanguagePrefix` carries the **interned** `/lang` and `/lang/` strings - the matchers do `path.StartsWith(prefix.SlashLangSlash, ...)` directly against those, skipping the `"/" + lang` allocation that a naive implementation would pay on every request.

### Hot-reload

The index subscribes to `IOptionsMonitor<HostnameCultureMappingOptions>.OnChange`, so `appsettings.json` reloads (or any other options change) rebuild the snapshot transparently. Snapshots are immutable; the current one is published via a `volatile` reference, so concurrent readers never see a torn or partial view and no per-request locking is needed. The previous snapshot stays alive until the last reader on the old request completes - normal GC-rooted-by-locals behaviour.

### `ChannelPrimaryLanguageResolver` cache

`IChannelPrimaryLanguageResolver` is registered as **singleton** with a `ConcurrentDictionary<string, string?>` mapping channel name -> primary content language code. The resolver runs once per request (path-prefix middleware) and once per generated URL (URL retriever decorator), so caching matters.

Why we cache: Kentico's hash-table cache (`[InfoCache]` on the *Info class) covers lookups by configured identifier columns (ID/Name/GUID). The middle leg of our chain - `WebsiteChannelInfo` filtered by `WebsiteChannelChannelID` - is a **foreign-key** query, so it bypasses the hash-table cache and hits the database every call. Without our cache, that DB round-trip would happen on every link.

Channel primary-language is admin-configured and effectively immutable at runtime; an app-lifetime cache is appropriate. If you change it via admin and need the resolver to pick it up without a restart, call `ChannelPrimaryLanguageResolver.InvalidateAll()` from a `WebsiteChannelInfo.TYPEINFO` change-event handler.

### What we don't cache (and why it's fine)

- **Channel resolution from `webPageItemId`** in the URL retriever (`ResolveChannelNameFromWebPageItemId`). Routes through Kentico's `IInfoProvider<WebPageItemInfo>` -> `IInfoProvider<WebsiteChannelInfo>` -> `IInfoProvider<ChannelInfo>` chain - both legs are by primary identifier (ID), so Kentico's hash-table cache covers them after warm-up. We don't add a request-scoped cache on top because the Kentico lookups are already cheap.
- **`IPreferredLanguageRetriever.Get()`** is called many times per request by Kentico. The first call (from the path-prefix middleware) computes the match and stashes it on `HttpContext.Items[HostLanguageMatch.ContextItemKey]`; subsequent calls do an O(1) `Items` lookup and return the stashed result. So the index's `Match` is invoked once per request, not per Kentico call.

### What's eager

`AddHostnameCultureMapping` registers the options builder with `ValidateOnStart()`, which forces options resolution (and PostConfigure validation) at app start. The `HostnameLookupIndex` itself is built lazily on first DI resolution - typically the first request - which costs a few microseconds for the dictionary fill. If you want the build to happen at startup too, resolve the index from a hosted service or a startup hook; it's not necessary for correctness.

## Behind a reverse proxy / CDN

Production deployments often sit behind a reverse proxy or CDN: Kentico SaaS's ingress, Cloudflare, Azure Front Door, AWS ALB, AWS API Gateway, an nginx/HAProxy fronting an internal cluster. Whether `Request.Host` carries the public hostname (the one the visitor typed and that this package's rules are configured for) depends on whether the proxy preserves it.

| Proxy | Default Host preservation | Original-host header (when not preserved) |
|---|---|---|
| Cloudflare | Preserved | (uses standard `Host`) |
| Azure Front Door | Preserved (with passthrough); rewrites otherwise | `X-Forwarded-Host`, `X-Original-Host` |
| AWS ALB | Preserved | `X-Forwarded-Host` (when configured) |
| AWS API Gateway (HTTP) | Preserved | `X-Forwarded-Host` |
| AWS API Gateway (REST) | Often rewritten to API Gateway domain | Original may be unavailable |
| nginx (default `proxy_pass`) | Replaced with origin server | `X-Forwarded-Host` (when configured) |
| HAProxy | Configurable | `X-Forwarded-Host` (when configured) |

When the proxy doesn't preserve `Host`, this package's rules will silently fail to match because they're configured for the public hostname but seeing the internal one.

### Recommended: ASP.NET Core's `ForwardedHeaders` middleware

The standard fix is global; do this in your host's `Program.cs`:

```csharp
using Microsoft.AspNetCore.HttpOverrides;

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedHost |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedFor;

    // Trust the proxy CIDRs explicitly. Defaults trust nothing - the headers
    // would be ignored.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    // options.KnownNetworks.Add(new IPNetwork(IPAddress.Parse("..."), prefix));
});
```

```csharp
// Pipeline:
app.UseForwardedHeaders();    // FIRST - before anything that reads Request.Host

app.UseMiddleware<HostnameCultureCanonicalRedirectMiddleware>();
app.UseMiddleware<HostnameCulturePathPrefixMiddleware>();
app.UseKentico();
```

Once `UseForwardedHeaders` runs, `Request.Host` is the public hostname for every middleware - this package and any other.

### Escape hatch: `ForwardedHostHeader`

When you can't or don't want to wire `UseForwardedHeaders` globally - e.g. you want this package to honour the forwarded host but leave host resolution untouched for everything else - set the `ForwardedHostHeader` option:

```json
"XperienceCommunity": {
  "LanguageDomains": {
    "ForwardedHostHeader": "X-Forwarded-Host",
    "Channels": { ... }
  }
}
```

The package will read the hostname from this header (taking the first entry of any comma-separated proxy chain), falling back to `Request.Host` if the header is absent. The same logic applies to scheme: when `ForwardedHostHeader` is set, `X-Forwarded-Proto` is honoured; otherwise the package uses `Request.Scheme` as ASP.NET resolved it.

This option only affects the package's own host resolution. Other middleware in your app continues to see `Request.Host` unchanged.

### Diagnosing it

Every Information-level log entry from this package includes both the **effective host** (what rules matched against) and the raw values of `Host`, `X-Forwarded-Host`, `X-Original-Host`, and `X-Forwarded-Proto`. So when "the rule isn't firing on the host I expect" investigations come up, the log line tells you exactly what the proxy delivered without re-running the request.

At Debug level, every page request through the middlewares logs an "assess" line with the same host/header snapshot - useful when you need to see *why* a rule didn't match.

## Logging

Every request-affecting decision in the package logs through standard `ILogger<T>`. Levels are picked so that the default output stays useful without flooding:

| Category | Level | Fires when |
|---|---|---|
| `XperienceCommunity.LanguageDomains.Middleware.HostnameCultureCanonicalRedirectMiddleware` | Information | A 301 redirect is issued. |
| `XperienceCommunity.LanguageDomains.Middleware.HostnameCultureCanonicalRedirectMiddleware` | Debug | Each page request being assessed (one per request, with host + header snapshot). |
| `XperienceCommunity.LanguageDomains.Middleware.HostnameCulturePathPrefixMiddleware` | Information | A request's path is mutated to add the language prefix. |
| `XperienceCommunity.LanguageDomains.Middleware.HostnameCulturePathPrefixMiddleware` | Debug | Each page request being assessed. |
| `XperienceCommunity.LanguageDomains.UrlListRewriter.UrlListItemHostnameRewriter` | Information | The admin URLs tab calls the rewriter (rare; admin-only). |
| `XperienceCommunity.LanguageDomains.Decorators.HostnameAwareWebPageUrlRetriever` | Debug | An outbound URL is rewritten (high volume - many per page render). |
| `XperienceCommunity.LanguageDomains.Decorators.HostnameAwarePreferredLanguageRetriever` | Debug | A language is resolved from query string or hostname (high volume - Kentico calls this many times per request). |

### Why these levels

- **Information for middlewares + admin rewriter** - they fire at most once per page request (the redirect short-circuits, the prefix middleware mutates exactly once, the admin rewriter runs only on URLs-tab loads). Excluded paths (assets, admin endpoints, the API) don't reach the logging branch, so static-asset traffic stays silent.
- **Debug for assess lines + decorators** - the middleware "assess" lines fire once per page request, useful for debugging non-matches. Decorators are called many times per page render (every internal link, every preview URL); logging at Information would flood. Both gate the message-formatting cost behind `IsEnabled(LogLevel.Debug)` so the cost is zero unless Debug is enabled.
- **Pass-through is silent at Info.** If a request doesn't trigger any of the package's rules, no Information-level entry is written.

### Host context on every entry

Every middleware log line carries the same host context:

- `effectiveHost` - what rules matched against (after `ForwardedHostHeader` resolution).
- `RequestHost` - the raw `Request.Host.Value`.
- `XForwardedHost`, `XOriginalHost`, `XForwardedProto` - raw header values, or `(null)` if absent.

That's enough to diagnose any "wrong host being matched" issue without rerunning the request - you can see what the proxy delivered and what the package decided to use.

### Tuning

Add to `appsettings.<env>.json` to enable Debug for the package while leaving the rest of the app at Information:

```json
"Logging": {
  "LogLevel": {
    "XperienceCommunity.LanguageDomains": "Debug"
  }
}
```

Or just one component:

```json
"Logging": {
  "LogLevel": {
    "XperienceCommunity.LanguageDomains.Middleware": "Debug",
    "XperienceCommunity.LanguageDomains.Decorators": "Information"
  }
}
```

Sample Information-level lines (one each per affected page request):

```
info: XperienceCommunity.LanguageDomains.Middleware.HostnameCultureCanonicalRedirectMiddleware[0]
      Canonical 301: GET https://en.example.com/uk/articles -> https://uk.example.com/articles (lang=uk) | RequestHost=en.example.com XForwardedHost=(null) XOriginalHost=(null) XForwardedProto=(null)

info: XperienceCommunity.LanguageDomains.Middleware.HostnameCulturePathPrefixMiddleware[0]
      Path-prefix translate: effectiveHost=uk.example.com /articles -> internal path /uk/articles (lang=uk, display=, storage=/uk) | RequestHost=uk.example.com XForwardedHost=(null) XForwardedProto=(null)
```

The same lines on a SaaS deployment where the CDN forwards `X-Forwarded-Host`:

```
info: ...HostnameCultureCanonicalRedirectMiddleware[0]
      Canonical 301: GET https://internal-lb.local/uk/articles -> https://uk.example.com/articles (lang=uk) | RequestHost=internal-lb.local XForwardedHost=en.example.com XOriginalHost=(null) XForwardedProto=https
```

In that snapshot you can immediately see: rule fired against `effectiveHost=internal-lb.local` (taken from `Request.Host` because `ForwardedHostHeader` wasn't set), but `XForwardedHost=en.example.com` is what the visitor actually typed - probable next step: enable `UseForwardedHeaders` or set `ForwardedHostHeader: "X-Forwarded-Host"`.

## Why this design

If something looks weird, here's why it's that way.

### Why decorators in a separate `AssemblyDiscoverable` library?

Two approaches were tried first, both broken:

**Decorating from the entry assembly via `services.Decorate<>` (Scrutor) or a hand-rolled factory swap.**
On .NET 10, Kentico's class discovery scans the entry assembly during `AddKentico()` and auto-registers any class that implements `IPreferredLanguageRetriever` or `IWebPageUrlRetriever` as a **Scoped type-based** descriptor - separate from any factory chain set up manually. The type-based descriptor is registered last, so it wins; its constructor takes the same interface, the runtime resolver loops back into the same descriptor, and you get `A circular dependency was detected for the service of type ...` at startup.

**Using `[assembly: RegisterImplementation]` on the decorator class while it lived in the entry assembly.**
`RegisterImplementation` does the right thing - it adds a factory-based descriptor that captures the previous Kentico registration and chains correctly. But the duplicate Scoped type-based registration from class discovery still gets added, still wins, and still produces the circular dependency.

The fix that survives both .NET 10 strictness and Kentico class discovery is to put the decorator class in a **library assembly that's marked `[assembly: AssemblyDiscoverable]`**. In a discoverable library, Kentico processes `[assembly: RegisterImplementation]` properly (factory chain) and the entry-assembly auto-discovery rule that produces the duplicate doesn't apply.

### Why does the URL retriever derive the channel from inputs instead of `IWebsiteChannelContext`?

An earlier version injected `IWebsiteChannelContext` and read `WebsiteChannelName` from it. That works for frontend requests (which are scoped to a channel) but breaks for admin requests (which aren't) - the property comes back empty, the options lookup fails, and the decorator falls through unchanged. URLs displayed in admin would not be rewritten.

The webpage item ID (or its GUID, the website-channel ID, or an explicit `websiteChannelName` argument) is always present on at least one of the seven `IWebPageUrlRetriever.Retrieve` overloads, and Kentico's stock `IInfoProvider<>` infrastructure caches the lookup chain (`WebPageItemInfo` -> `WebsiteChannelInfo` -> `ChannelInfo`). Resolving the channel from the input is reliable in both contexts.

### Why use `DispatchProxy` for the admin URL list items retriever?

`Kentico.Xperience.Admin.Websites.UIPages.IWebPageUrlListItemsRetriever` is `internal` to the admin assembly. Confirmed via reflection: `IsPublic = false`. Options considered:

- `[assembly: RegisterImplementation]` - won't compile (`typeof()` against an inaccessible type).
- `services.Decorate<>` from Scrutor - same problem, the generic constraint is unsatisfiable.
- Decorate `IWebsiteChannelDomainProvider` (this one IS public, despite living in `CMS.Websites.Internal`). Doesn't work because its `GetDomain(int channelId, ...)` signature has no language parameter.
- Replace the `UrlsTab` UI page wholesale via `[UIPage]` extension. Substantial UI rewrite.

`DispatchProxy` lets you implement an interface you don't have a compile-time reference to, by resolving its `Type` at runtime. The interface gets swapped via service-collection surgery rather than `RegisterImplementation`. The proxy matches the target method by **name and arity** (`Retrieve` with 3 args), since there's no symbol to dispatch on. **Fragile** - any rename or signature change in the internal admin API silently breaks this. Worth a smoke test on every Kentico package upgrade.

### Why does the URL retriever decorator NOT affect the URLs tab "System URL" field?

It doesn't, by design - that's exactly why `HostnameAwareUrlListItemsRetrieverProxy` exists. The admin URLs tab populates its rows from `IWebPageUrlListItemsRetriever`, which builds URLs by directly concatenating `IWebsiteChannelDomainProvider.GetDomain(...)` with `WebPageUrlPathInfo.WebPageUrlPath` from the database. It doesn't go through `IWebPageUrlRetriever`, so the public-side decorator never runs for those URLs. The dispatch-proxy decorator catches it at the actual code path the admin uses.

### Why merge hosts into `WebsiteChannelDomainOptions` automatically?

Without the merge, the same hostnames have to be listed in two appsettings sections (`WebsiteChannelDomains` and `XperienceCommunity:LanguageDomains`). They drift. The PostConfigure step unions the hostnames from this package's config into the channel's domain list at startup, leaving anything explicitly set in `WebsiteChannelDomains` intact (so non-language-related domain aliases can still go there). Result: hosts are configured once.

## Limitations

1. **Admin URLs tab decorator is a reflection-based proxy.** `IWebPageUrlListItemsRetriever` is internal to Kentico's admin assembly; the proxy matches by method name + arity. Any change in the internal API silently breaks the admin display - the live site will still be correct, but the "System URL" field will revert to showing the un-rewritten URL.
2. **One root language per hostname, one canonical hostname per language.** The schema makes the first rule structural (each `HostnameMapping` has a single `RootLanguage`); the second is enforced by the validator (a language codename can only appear under one hostname per channel) on the grounds that duplicate content across hostnames is bad SEO. If you need vanity-domain aliases, do the 301 at the edge.
3. **`IPreferredLanguageRetriever` decorator depends on `IWebsiteChannelContext`** (frontend-only). For requests outside a channel scope (admin), the decorator falls through to the inner retriever. The `IWebPageUrlRetriever` decorator does *not* depend on `IWebsiteChannelContext`.
4. **`Kentico.Xperience.Admin` is a runtime dep** even though only the URL-list-items proxy needs it. Splitting it into a separate package would be cleaner but doubles the project count.

## Compatibility

- Built against **XbyK 31.4.3**.
- Targets **net8.0**. Verified to work when consumed by a **net10.0** host.
