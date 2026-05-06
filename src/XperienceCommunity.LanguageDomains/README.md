# XperienceCommunity.LanguageDomains

Hostname-based language switching for Xperience by Kentico website channels. Serve each language on its own domain (`en.example.com`, `uk.example.com`), keep public URLs clean (`uk.example.com/about` instead of `uk.example.com/uk/about`), and have the same rewriting reflected in the admin URLs tab.

> 📖 Full README, mermaid diagrams, and rationale: [github.com/devotion-patrick/xperience-community-language-domains](https://github.com/devotion-patrick/xperience-community-language-domains#readme)

## What it does

Stock Xperience routes non-default-language pages off a path prefix on the channel's primary domain (`example.com/uk/about`). This package extends that with a hostname-based model:

- **Inbound.** Each language can have its own dedicated host. Middleware translates clean URLs into the language-prefixed paths Kentico's URL-path routing actually stores.
- **Outbound.** A URL retriever decorator strips the language prefix from generated links and rebuilds the absolute URL onto the language's primary host.
- **Admin parity.** The URLs tab in admin shows the same rewritten URLs the live site renders, via a `DispatchProxy`-based decorator over Kentico's internal URL list-items service.
- **One source of truth.** Hosts listed under `XperienceCommunity:LanguageDomains` are auto-merged into Kentico's `WebsiteChannelDomainOptions` so you don't maintain them twice.
- **Indexed hot path.** A singleton lookup index precomputes hostname/language dictionaries at startup; per-request matching is O(1) hash lookups against zero-allocation comparisons. Hot-reloads on `IOptionsMonitor` change.

## Compatibility

| | |
|---|---|
| **Xperience by Kentico** | minimum **30.6.1**, no upper bound — verified against all 43 published releases through `31.4.3` |
| **.NET** | targets `net8.0`; consumed cleanly from `net8` / `net9` / `net10` host apps |
| **Hosting** | ASP.NET Core (Kestrel direct or behind an IIS reverse proxy) |

## Install

```
dotnet add package XperienceCommunity.LanguageDomains
```

## Quick start

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

`en.example.com` serves `en` at `/` and Australian English (`au`) at `/au/...`. `uk.example.com` serves `uk` at `/`. The strings in `RootLanguage` and `Languages` are Kentico content language **codenames** — whatever you set when creating the language in admin. They double as the user-facing path prefix for non-root languages.

**2.** In `Program.cs`:

```csharp
using XperienceCommunity.LanguageDomains.Extensions;
using XperienceCommunity.LanguageDomains.Middleware;

builder.Services.AddHostnameCultureMapping(builder.Configuration);

// Optional: decorate the internal admin URL-list-items retriever so the URLs tab in admin
// shows the rewritten URLs too. Must be the last builder.Services.* call before builder.Build().
builder.Services.AddHostnameAwareUrlListItemsRetrieverDecorator();

var app = builder.Build();

// Both middlewares must run BEFORE app.UseKentico().
app.UseMiddleware<HostnameCultureCanonicalRedirectMiddleware>();
app.UseMiddleware<HostnameCulturePathPrefixMiddleware>();
app.UseKentico();
```

That's the entire setup. The decorators register themselves through `[assembly: RegisterImplementation]` and Kentico's class discovery.

## Behind a reverse proxy / CDN

If your origin sits behind a CDN/proxy that doesn't preserve the `Host` header, point `ForwardedHostHeader` at whichever header your proxy sets:

```json
"XperienceCommunity": {
  "LanguageDomains": {
    "ForwardedHostHeader": "X-Forwarded-Host",
    "Channels": { ... }
  }
}
```

The package reads its hostname from this header (taking the first entry of any comma-separated proxy chain), falling back to `Request.Host` if the header is missing. Alternatively, wire `app.UseForwardedHeaders()` globally in `Program.cs` — see the [full README](https://github.com/devotion-patrick/xperience-community-language-domains#behind-a-reverse-proxy--cdn-kentico-saas-cloudflare-afd-aws-alb-) for the standard ASP.NET Core approach.

## Validation and logging

Validation runs at startup. Each channel is checked for hostname uniqueness, a non-empty `RootLanguage` that appears in `Languages`, and the "one canonical hostname per language per channel" rule. Invalid channels are logged at `Error` and dropped from the effective options. Set `StrictValidation: true` to fail startup instead.

Logs flow through standard `ILogger<T>`; every middleware log line includes the effective host and raw forwarded headers so "the rule isn't firing on the host I expect" investigations don't require a re-run. Set `Logging:LogLevel:XperienceCommunity.LanguageDomains` to `Debug` while investigating.

## Documentation

- [Usage Guide](https://github.com/devotion-patrick/xperience-community-language-domains/blob/main/docs/Usage-Guide.md) — field-by-field config reference, inbound/outbound behaviour matrix, design notes.
- [Working sample](https://github.com/devotion-patrick/xperience-community-language-domains/tree/main/examples) — Dancing Goat-style site wired with the package.
- [Changelog](https://github.com/devotion-patrick/xperience-community-language-domains/blob/main/CHANGELOG.md)

## License

Distributed under the MIT License. See [LICENSE.md](https://github.com/devotion-patrick/xperience-community-language-domains/blob/main/LICENSE.md).

## Support

This project has [Kentico Labs limited support](https://github.com/Kentico/.github/blob/main/SUPPORT.md#labs-limited-support).
