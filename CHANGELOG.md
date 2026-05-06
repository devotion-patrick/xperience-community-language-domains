# Changelog

All notable changes to `XperienceCommunity.LanguageDomains` are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Pre-`1.0.0` releases may introduce breaking changes between minor versions.

## [Unreleased]

## [0.1.0-preview.1] — 2026-05-06

First public preview. Hostname-based language switching for Xperience by Kentico website channels.

### Added

- **Hostname-driven language resolution.** Bind each language to its own host (`en.example.com`, `es.example.com`), with the channel's primary language at the bare root.
- **Optional path-prefix stripping.** Render public URLs without the `/{langcode}/` segment Kentico stores against; routing resolves internally.
- **Admin URLs tab in sync.** The URL list shown in admin reflects the same hostname rewriting visible to end users.
- **Canonical redirect middleware.** 301s requests on non-canonical host/prefix combinations to the correct host for their language.
- **Shape-tolerant `IWebPageUrlListItemsRetriever` proxy.** Single binary handles both the 2-arg `Retrieve` (v30.6.x) and 3-arg `Retrieve(int, int, CancellationToken)` (v30.7+) signatures via reflection with arity tolerance.
- **Loud failure on unknown shapes.** Unrecognised admin API signatures log a descriptive `ILogger` error instead of silently no-op'ing.
- **Weekly drift-detection workflow.** `.github/workflows/xbyk-sweep.yml` reflects on every published `Kentico.Xperience.Admin >= 30.6.0` and re-runs the contract tests.

### Compatibility

- **Xperience by Kentico**: minimum `30.6.1`, no upper bound — verified against all 43 published releases through `31.4.3`.
- **.NET**: targets `net8.0`; consumed cleanly from `net8` / `net9` / `net10` host apps.
- **Hosting**: ASP.NET Core (Kestrel direct or behind an IIS reverse proxy).

[Unreleased]: https://github.com/devotion-patrick/xperience-community-language-domains/compare/v0.1.0-preview.1...HEAD
[0.1.0-preview.1]: https://github.com/devotion-patrick/xperience-community-language-domains/releases/tag/v0.1.0-preview.1
