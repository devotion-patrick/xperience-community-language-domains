using Microsoft.Extensions.Options;

using XperienceCommunity.LanguageDomains.Configuration;

namespace XperienceCommunity.LanguageDomains.Internal;

/// <summary>
/// Precomputed lookup tables built from
/// <see cref="HostnameCultureMappingOptions"/> at startup, so the per-request
/// hot path (inbound matching, outbound URL rewriting, canonical-redirect
/// scanning) does O(1) hash lookups against pre-interned strings rather than
/// nested-loop scans with per-iteration <c>"/" + lang</c> allocations.
///
/// <para>The index also carries the option-bag flags (<c>EnableCanonicalRedirect</c>,
/// <c>ForwardedHostHeader</c>, <c>ExcludedPathPrefixes</c>) on the snapshot
/// so consumers only need one DI dependency instead of <c>IOptionsMonitor</c>
/// + the index.</para>
///
/// <para>Subscribes to <see cref="IOptionsMonitor{TOptions}.OnChange"/> so
/// that <c>appsettings.json</c> hot-reload (or any other options change)
/// rebuilds the snapshot transparently. Snapshots are immutable and
/// published via a volatile reference; readers always see a consistent view
/// without locking. The OnChange subscription is captured and disposed in
/// <see cref="Dispose"/> - prevents leaks if the index is ever recreated
/// (e.g. test scenarios that rebuild the DI container).</para>
///
/// <para>Built only from channels that survived
/// <see cref="HostnameCultureMappingOptions"/> validation. Invalid channels
/// are dropped before this point in lenient mode, or rejected at startup in
/// strict mode (see
/// <see cref="HostnameCultureMappingOptions.StrictValidation"/>).</para>
/// </summary>
public sealed class HostnameLookupIndex : IDisposable
{
    private volatile Snapshot _current;
    private readonly IDisposable? _changeSubscription;

    public HostnameLookupIndex(IOptionsMonitor<HostnameCultureMappingOptions> options)
    {
        _current = Build(options.CurrentValue);
        // Capture the subscription so Dispose can detach it. App-lifetime
        // singletons rarely need this (the GC reclaims everything together),
        // but a recreated index in a test harness or hosted-service teardown
        // would otherwise leak callbacks into a dead instance.
        _changeSubscription = options.OnChange(opts => _current = Build(opts));
    }

    public Snapshot Current => _current;

    /// <summary>
    /// Inbound match: given the request <paramref name="host"/> and
    /// <paramref name="path"/>, returns which language owns the request.
    /// O(1) hostname hash lookup + O(language-count) prefix probe against
    /// pre-interned strings (zero allocation in the comparison).
    /// </summary>
    public HostLanguageMatch? Match(string host, string path)
    {
        if (string.IsNullOrEmpty(host))
        {
            return null;
        }

        if (!_current.ByHostname.TryGetValue(host, out var hostEntry))
        {
            return null;
        }

        // Non-root prefixes win over the root fallback (more specific URL).
        // Walk AllPrefixes once, skipping the root entry on this pass, and
        // remember it for the second-pass fallback.
        LanguagePrefix? rootPrefix = null;
        var allPrefixes = hostEntry.AllPrefixes;
        for (int i = 0; i < allPrefixes.Count; i++)
        {
            var prefix = allPrefixes[i];
            if (prefix.IsRoot)
            {
                rootPrefix = prefix;
                continue;
            }
            if (path.StartsWith(prefix.SlashLangSlash, StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, prefix.SlashLang, StringComparison.OrdinalIgnoreCase))
            {
                return new HostLanguageMatch(
                    ChannelName: hostEntry.ChannelName,
                    LanguageCode: prefix.LanguageCode,
                    IsRootMatch: false);
            }
        }

        if (rootPrefix != null)
        {
            return new HostLanguageMatch(
                ChannelName: hostEntry.ChannelName,
                LanguageCode: rootPrefix.Value.LanguageCode,
                IsRootMatch: true);
        }

        return null;
    }

    /// <summary>
    /// Outbound lookup: given a (channel, language) pair, returns the
    /// hostname that serves it plus the precomputed display prefix. O(1)
    /// hash lookup. Used by the URL retriever decorator and the admin
    /// URLs-tab rewriter on every URL they emit.
    /// </summary>
    public OutboundEntry? FindForLanguage(string channelName, string languageCode)
    {
        if (string.IsNullOrEmpty(channelName) || string.IsNullOrEmpty(languageCode))
        {
            return null;
        }
        var key = MakeKey(channelName, languageCode);
        return _current.ByChannelAndLanguage.TryGetValue(key, out var entry) ? entry : null;
    }

    /// <summary>
    /// Returns whether <paramref name="languageCode"/> is configured anywhere
    /// in <paramref name="channelName"/> (any hostname). Used by the
    /// preferred-language retriever to validate <c>?language=...</c> query
    /// overrides.
    /// </summary>
    public bool ChannelHasLanguage(string channelName, string languageCode)
        => FindForLanguage(channelName, languageCode) != null;

    public void Dispose() => _changeSubscription?.Dispose();

    private static (string, string) MakeKey(string channel, string language)
        => (channel.ToLowerInvariant(), language.ToLowerInvariant());

    private static Snapshot Build(HostnameCultureMappingOptions opts)
    {
        var byHostname = new Dictionary<string, HostnameEntry>(StringComparer.OrdinalIgnoreCase);
        var byChannelAndLanguage = new Dictionary<(string, string), OutboundEntry>();

        foreach (var (channelName, channelMapping) in opts.Channels)
        {
            foreach (var hm in channelMapping.Hostnames)
            {
                if (string.IsNullOrEmpty(hm.Hostname))
                {
                    continue;
                }

                var allPrefixes = new List<LanguagePrefix>();
                foreach (string lang in hm.Languages)
                {
                    if (string.IsNullOrEmpty(lang))
                    {
                        continue;
                    }

                    bool isRoot = string.Equals(lang, hm.RootLanguage, StringComparison.OrdinalIgnoreCase);
                    string slashLang = "/" + lang;
                    string slashLangSlash = slashLang + "/";
                    // Display prefix is empty for root languages (served at /
                    // with no prefix in user-facing URLs), and "/{lang}" for
                    // non-root languages. Precomputed here once so per-link
                    // outbound rewriting doesn't allocate it on every call.
                    string displayPrefix = isRoot ? string.Empty : slashLang;

                    byChannelAndLanguage[MakeKey(channelName, lang)] = new OutboundEntry(hm, isRoot, displayPrefix);
                    allPrefixes.Add(new LanguagePrefix(lang, isRoot, slashLang, slashLangSlash));
                }

                byHostname[hm.Hostname] = new HostnameEntry(channelName, hm, allPrefixes);
            }
        }

        // Fold the package-default excluded prefixes together with any
        // consumer additions so middlewares can iterate one list. Defaults
        // come first - the order is irrelevant for correctness (StartsWith
        // either matches or it doesn't), but it puts the common Kentico
        // paths at the front for marginal locality.
        var excluded = new List<string>(ExcludedPathPrefixes.Defaults.Count
            + (opts.AdditionalExcludedPathPrefixes?.Count ?? 0));
        excluded.AddRange(ExcludedPathPrefixes.Defaults);
        if (opts.AdditionalExcludedPathPrefixes != null)
        {
            foreach (string p in opts.AdditionalExcludedPathPrefixes.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                excluded.Add(p);
            }
        }

        return new Snapshot(
            byHostname,
            byChannelAndLanguage,
            excluded,
            opts.EnableCanonicalRedirect,
            opts.ForwardedHostHeader);
    }

    public sealed record Snapshot(
        IReadOnlyDictionary<string, HostnameEntry> ByHostname,
        IReadOnlyDictionary<(string, string), OutboundEntry> ByChannelAndLanguage,
        IReadOnlyList<string> ExcludedPathPrefixes,
        bool EnableCanonicalRedirect,
        string? ForwardedHostHeader);

    /// <summary>
    /// Per-hostname lookup row. <see cref="AllPrefixes"/> covers every
    /// configured language for the host (root and non-root, distinguished by
    /// <see cref="LanguagePrefix.IsRoot"/>) - one list serves both inbound
    /// matching and the channel-scoped canonical-redirect scan.
    /// </summary>
    public sealed record HostnameEntry(
        string ChannelName,
        HostnameMapping Hostname,
        IReadOnlyList<LanguagePrefix> AllPrefixes);

    /// <summary>
    /// One configured language on a hostname, with pre-interned
    /// <c>/lang</c> and <c>/lang/</c> strings. <see cref="IsRoot"/> separates
    /// the root language (served at <c>/</c>, no display prefix in
    /// user-facing URLs) from non-root languages (reached via
    /// <c>/{lang}/...</c>).
    /// </summary>
    public readonly record struct LanguagePrefix(
        string LanguageCode,
        bool IsRoot,
        string SlashLang,
        string SlashLangSlash);

    /// <summary>
    /// Outbound lookup result. <see cref="DisplayPrefix"/> is precomputed
    /// (empty for root, <c>/{lang}</c> otherwise) so the URL rewriter
    /// doesn't allocate it per call.
    /// </summary>
    public sealed record OutboundEntry(HostnameMapping Hostname, bool IsRoot, string DisplayPrefix);
}
