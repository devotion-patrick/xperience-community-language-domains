# XperienceCommunity.LanguageDomains

A self-contained Xperience by Kentico (XbyK) extension that adds **hostname-based culture switching** to a website channel. One website channel can serve multiple languages, each on its own dedicated hostname (`en.example.com`, `uk.example.com`), with optional language path prefixes (`/uk/...`) handled transparently in both directions.

> Working notes captured during the original implementation. Treat this as architectural background and a debugging aid; the public-facing README that ships with the submodule is curated separately.

---

## What it does

| Concern | Stock XbyK | With this package |
|---|---|---|
| Which language to serve? | Determined by the URL path prefix (e.g. `/uk/...`) and the channel's primary content language. | Determined by the hostname. `uk.example.com` -> `uk`, `en.example.com` -> `en`. |
| Inbound URL `uk.example.com/Home` | Treated as the `Home` URL path in the default language; UK content not found. | Middleware prepends `/uk` so Kentico's URL-path routing finds the UK variant (`uk/Home`). |
| Outbound link generation | `IWebPageUrlRetriever` returns `~/uk/Home` against the channel's primary domain. | Decorator strips `/uk/` and swaps the absolute URL to `https://uk.example.com/Home`. |
| Admin URLs tab "System URL" | Built directly from `WebPageUrlPath` + `IWebsiteChannelDomainProvider`, bypassing `IWebPageUrlRetriever`. | A separate `DispatchProxy` decorates the internal `IWebPageUrlListItemsRetriever` so the admin shows hostname-rewritten URLs too. |
| `WebsiteChannelDomainOptions` | Maintained separately in `appsettings.json`. | Auto-populated from this package's config so hostnames live in one place. |

---

## Configuration

Per Xperience community-package convention, settings live under a top-level `XperienceCommunity` key:

```json
{
  "XperienceCommunity": {
    "LanguageDomains": {
      "Channels": {
        "<channel-code-name>": {
          "<language-code>": {
            "Hosts": [ "host[:port]", "..." ],
            "StripPathPrefix": true
          }
        }
      }
    }
  }
}
```

### Field semantics

| Field | Type | Default | Notes |
|---|---|---|---|
| `Hosts` | `string[]` | `[]` | Hosts that resolve to this language. The first entry is treated as the primary host - used to rewrite absolute URLs that point at this language from another host. |
| `StripPathPrefix` | `bool` | `false` | When `true`, the language code path prefix is prepended to inbound requests on this hostname (so Kentico's URL routing matches) AND stripped from outbound URLs (so links render bare). When `false`, no prefix manipulation - URLs keep their language code prefix on this host. |

The path prefix string is **not** configured separately - it's always the language code (the inner dictionary key), since that's Kentico's convention for non-default-language URL paths (`uk/Home`, `fr/About`, etc.).

### Validation rule

**At most one language per channel** may have `StripPathPrefix: true`. Enforced at startup via `ValidateOnStart()`; misconfiguration fails the host with a clear message.

### Behavior matrix

| `StripPathPrefix` on the hostname's mapping | Inbound (middleware) | Outbound (URL retriever / list items) |
|---|---|---|
| `true` | Prepend `/<langcode>` to the request path so Kentico's URL-path routing matches the stored slug. | Strip `/<langcode>` from generated paths. Always rebuild the absolute URL against the language's primary host. |
| `false` (default) | Pass-through. (URLs are expected to include the language path segment already, e.g. `uk.example.com/uk/page`.) | Pass-through on the path. Still rebuild the absolute URL host. |

### Example

```json
{
  "XperienceCommunity": {
    "LanguageDomains": {
      "Channels": {
        "DancingGoatCore": {
          "en": {
            "Hosts": [ "en.example.com" ]
          },
          "uk": {
            "Hosts": [ "uk.example.com" ],
            "StripPathPrefix": true
          }
        }
      }
    }
  }
}
```

`en` is the channel default - its URL paths in storage have no prefix, so it doesn't need to strip anything. `uk` is a dedicated-hostname language whose URL paths in storage start with `uk/`, so we want clean URLs on the live site (`uk.example.com/page` rather than `uk.example.com/uk/page`).

---

## Wiring it up in the host

Two calls in `Program.cs`:

```csharp
using XperienceCommunity.LanguageDomains;

// 1. Bind options + register the decorators (via [assembly: RegisterImplementation])
//    + auto-merge hosts into WebsiteChannelDomainOptions.
//    Call this where the rest of your channel config goes.
builder.Services.AddHostnameCultureMapping(builder.Configuration);

// 2. Decorate the internal admin URL list items retriever via DispatchProxy.
//    This must be the LAST builder.Services.* call before builder.Build()
//    so we capture the original descriptor after Kentico has registered it.
builder.Services.AddHostnameAwareUrlListItemsRetrieverDecorator();

var app = builder.Build();
```

And one middleware registration **before** `app.UseKentico()`:

```csharp
app.UseMiddleware<HostnameCulturePathPrefixMiddleware>();
app.UseKentico();
```

This package is marked `[assembly: AssemblyDiscoverable]`, so once it's referenced the `[assembly: RegisterImplementation]` attributes inside it are discovered automatically. The `IPreferredLanguageRetriever` and `IWebPageUrlRetriever` decorators don't need explicit registration.

---

## Components

```
src/XperienceCommunity.LanguageDomains/
├── XperienceCommunity.LanguageDomains.csproj           AssemblyDiscoverable, refs WebApp + Admin packages
├── HostnameCultureMappingOptions.cs                    IOptions<> binding for the appsettings section
├── HostnameAwarePreferredLanguageRetriever.cs          Decorator: hostname -> language code
├── HostnameAwareWebPageUrlRetriever.cs                 Decorator: strips path prefix, rewrites absolute host
├── HostnameCulturePathPrefixMiddleware.cs              Inbound: prepends /<langcode> on hostnames with StripPathPrefix=true
├── HostnameAwareUrlListItemsRetrieverProxy.cs          DispatchProxy: decorates internal admin service
├── UrlListItemHostnameRewriter.cs                      Logic the proxy delegates to
└── HostnameCultureMappingExtensions.cs                 IServiceCollection extensions + validation + WebsiteChannelDomainOptions auto-merge
```

### `HostnameAwarePreferredLanguageRetriever`
Decorates `Kentico.Content.Web.Mvc.Routing.IPreferredLanguageRetriever`. Resolution order:
1. `?language=<code>` query string override (only honoured for codes configured for the active channel).
2. Hostname match against `LanguageHostMapping.Hosts`.
3. Fall through to Kentico's stock retriever.

### `HostnameAwareWebPageUrlRetriever`
Decorates `CMS.Websites.IWebPageUrlRetriever`. Implements all seven `Retrieve` overloads. For each one:
1. Calls the inner retriever to get the stock `WebPageUrl`.
2. Resolves the **channel name** from whatever input is available (webpage item ID -> websitechannel -> channel; or the explicit `websiteChannelName` parameter where present).
3. If the channel + language pair is configured here, applies the rewrite logic.

The rewrite:
1. Splits off the leading `~` (Razor app-relative marker) before doing prefix surgery, then re-attaches.
2. If `StripPathPrefix` is set, strips `/<langcode>/` (or `/<langcode>` exactly) from the absolute path.
3. **Always** rebuilds the absolute URL against the language's primary host - the inner retriever returns absolute URLs against the channel's primary domain, which is the wrong host for cross-language links.

### `HostnameCulturePathPrefixMiddleware`
Pre-`UseKentico` request mutator. For requests whose hostname maps to a language with `StripPathPrefix: true`, prepends `/<langcode>` to `HttpRequest.Path` so Kentico's URL-path routing finds the stored prefixed URL path. Skips a hard-coded list of admin/api/asset paths.

### `HostnameAwareUrlListItemsRetrieverProxy` + `UrlListItemHostnameRewriter`
The admin URLs tab is special. It does **not** use `IWebPageUrlRetriever` to build the displayed URL strings - it talks to `Kentico.Xperience.Admin.Websites.UIPages.IWebPageUrlListItemsRetriever`, which constructs URLs from `WebPageUrlPathInfo` + `IWebsiteChannelDomainProvider` directly. To make the admin reflect the same hostname/prefix rewriting:
- `IWebPageUrlListItemsRetriever` is **`internal`** to Kentico's admin assembly. We can't reference it at compile time.
- `HostnameAwareUrlListItemsRetrieverProxy` extends `System.Reflection.DispatchProxy`. Its interface type is resolved via reflection at registration time and passed to `DispatchProxy.Create<TInterface, TProxy>()` (also via reflection).
- The proxy forwards every call to the inner instance. For `Retrieve(int webPageItemId, int languageId, CancellationToken)` (matched by name + arity at runtime, since we have no compile-time symbol), it post-processes the returned `IEnumerable<UrlListItem>` through `UrlListItemHostnameRewriter`.
- The rewriter is a regular DI-friendly service that derives the channel name from `webPageItemId` (same `IInfoProvider<WebPageItemInfo>` chain used elsewhere) and applies the same strip-and-host-swap logic to each `UrlListItem.WebPageUrl`, `WebPageUrlPathBase`, and `WebPageUrlPathSlug`.

### `HostnameCultureMappingExtensions`
`IServiceCollection` extensions:
- `AddHostnameCultureMapping(configuration)` - binds options, validates ("at most one primary per channel"), and **`PostConfigure`s `WebsiteChannelDomainOptions`** to merge `XperienceCommunity:LanguageDomains` hostnames into the channel's domain list. Existing entries from the `WebsiteChannelDomains` config section win; we just union our hosts onto whatever's already there.
- `AddHostnameAwareUrlListItemsRetrieverDecorator()` - reflection-based registration that swaps Kentico's stock `IWebPageUrlListItemsRetriever` for a factory that builds the dispatch proxy.

---

## Why this design

The journey is worth recording because almost every alternative was tried and rejected. If you're reading this because something looks weird, here's why it's that way.

### Why decorators in a separate `AssemblyDiscoverable` library?

We tried two approaches first, both broken:

**Decorating from the entry assembly via `services.Decorate<>` (Scrutor) or a hand-rolled factory swap.**
On .NET 10, Kentico's class discovery scans the entry assembly during `AddKentico()` and auto-registers any class that implements `IPreferredLanguageRetriever` or `IWebPageUrlRetriever` as a **Scoped type-based** descriptor - separate from any factory chain we set up ourselves. Since the type-based descriptor is registered last, it wins; its constructor takes the same interface, the runtime resolver loops back into the same descriptor, and you get `A circular dependency was detected for the service of type ...`. Reproducible at every startup.

**Using `[assembly: RegisterImplementation]` on the decorator class while it lived in the entry assembly.**
`RegisterImplementation` does the right thing - it adds a factory-based descriptor that captures the previous Kentico registration and chains correctly. But the duplicate Scoped type-based registration from class discovery still gets added, still wins by being registered later, and still produces the circular dependency.

The fix that survives both .NET 10 strictness and Kentico class discovery is to put the decorator class in a **library assembly that's marked `[assembly: AssemblyDiscoverable]`**. In a discoverable library, Kentico processes `[assembly: RegisterImplementation]` properly (factory chain), and the entry-assembly auto-discovery rule that produces the duplicate doesn't apply.

### Why does the URL retriever derive the channel from inputs instead of `IWebsiteChannelContext`?

Earlier versions injected `IWebsiteChannelContext` and read `WebsiteChannelName` from it. That works for frontend requests (which are scoped to a channel), but breaks for admin requests (which aren't) - the property comes back empty, the options lookup fails, and the decorator falls through unchanged. So URLs displayed in admin would not be rewritten.

The webpage item ID (or its GUID, or the website-channel ID, or an explicit `websiteChannelName` argument) is always present on at least one of the seven `IWebPageUrlRetriever.Retrieve` overloads, and Kentico's stock `IInfoProvider<>` infrastructure caches the lookup chain (`WebPageItemInfo` -> `WebsiteChannelInfo` -> `ChannelInfo`). Resolving the channel from the input is reliable in both contexts and barely measurable.

### Why `StripPathPrefix` instead of `IsPrimaryDomain` (or no flag at all)?

The flag was renamed several times during design. The final name describes what the package actually does (strips the path prefix on the URL), not the abstract concept it serves (a domain being "primary"). Defaulting to `false` makes the no-op case explicit - if someone sets up a hostname mapping but doesn't think about prefixes, the path is left alone.

The original schema also had a separate `PathPrefix` string field. That was redundant: the prefix is always the language code (the dictionary key), because that's how Kentico stores non-default-language URL paths. Deriving it from the key removes a footgun (mismatch between the prefix string and the actual stored path).

### Why use `DispatchProxy` for the admin URL list items retriever?

The interface you'd want to decorate cleanly - `Kentico.Xperience.Admin.Websites.UIPages.IWebPageUrlListItemsRetriever` - is `internal` to the admin assembly. Confirmed via reflection: `IsPublic = false`. Options considered:

- `[assembly: RegisterImplementation]` - won't compile (`typeof()` against an inaccessible type).
- `services.Decorate<>` from Scrutor - same problem, the generic constraint is unsatisfiable.
- Decorate `IWebsiteChannelDomainProvider` instead (this one IS public, despite living in `CMS.Websites.Internal`). Doesn't work because its `GetDomain(int channelId, ...)` signature has no language parameter, so you can't return different hosts per language from inside it.
- Replace the `UrlsTab` UI page wholesale via `[UIPage]` extension. Substantial UI rewrite for a label change.

`DispatchProxy` lets you implement an interface you don't have a compile-time reference to, by resolving its `Type` at runtime. The interface gets swapped via service-collection surgery rather than `RegisterImplementation`. The proxy matches the target method by **name and arity** (`Retrieve` with 3 args), since there's no symbol to dispatch on. **Fragile** - any rename or signature change in the internal admin API silently breaks this. Worth a smoke test on every Kentico package upgrade.

### Why does the URL retriever decorator NOT affect the URLs tab "System URL" field?

It doesn't, by design - that's exactly why `HostnameAwareUrlListItemsRetrieverProxy` exists. The admin URLs tab populates its rows from `IWebPageUrlListItemsRetriever`, which builds URLs by directly concatenating `IWebsiteChannelDomainProvider.GetDomain(...)` with `WebPageUrlPathInfo.WebPageUrlPath` from the database. It doesn't go through `IWebPageUrlRetriever`, so our decorator on that interface never runs for those URLs. The dispatch-proxy decorator catches it at the actual code path the admin uses.

### Why merge hosts into `WebsiteChannelDomainOptions` automatically?

Without the merge, the same hostnames have to be listed in two appsettings sections (`WebsiteChannelDomains` and `XperienceCommunity:LanguageDomains`). They drift. The PostConfigure step unions the hostnames from this package's config into the channel's domain list at startup, leaving anything explicitly set in `WebsiteChannelDomains` intact (so you can still register non-language-related domain aliases there if you need them). Result: hosts are configured once.

---

## Limitations

1. **Admin URLs tab decorator is a reflection-based proxy.** `IWebPageUrlListItemsRetriever` is internal to Kentico's admin assembly; the proxy matches by method name + arity. Any change in the internal API silently breaks the admin display - the live site will still be correct, but the "System URL" field will revert to showing the un-rewritten URL.
2. **At most one `StripPathPrefix: true` per channel.** Enforced at startup. If you want clean URLs on multiple non-default-language hostnames in a single channel, the current model can't express that (and Kentico's URL path uniqueness constraint within a channel would prevent it from ever working anyway).
3. **`IPreferredLanguageRetriever` decorator depends on `IWebsiteChannelContext`** (frontend-only). For requests outside a channel scope (admin), the decorator falls through to the inner retriever; that's fine because language resolution is a frontend concern. The `IWebPageUrlRetriever` decorator does *not* depend on `IWebsiteChannelContext` (see "Why does the URL retriever derive..." above).
4. **`Kentico.Xperience.Admin` is a runtime dep** even though only one file (`HostnameAwareUrlListItemsRetrieverProxy`) needs it. Splitting it into a separate package would be cleaner but doubles the project count.

---

## Required package references

Listed in the repo's `Directory.Packages.props` (centrally managed):

```xml
<PackageVersion Include="Kentico.Xperience.WebApp" Version="31.4.2" />
<PackageVersion Include="Kentico.Xperience.Admin" Version="31.4.2" />
```

The host project that consumes this package needs at least the same versions of `Kentico.Xperience.WebApp` and `Kentico.Xperience.Admin`.

---

## Compatibility

- Built against **XbyK 31.4.2**.
- Targets **net8.0** (per the repo-template default). Verified to work when consumed by a **net10.0** host. The class-discovery / circular-dependency story is specific to .NET 10's stricter DI validation - on .NET 8 the Scoped duplicate doesn't trip the validator at startup, but the runtime cycle is the same. Putting decorators in a discoverable library is correct on both.
