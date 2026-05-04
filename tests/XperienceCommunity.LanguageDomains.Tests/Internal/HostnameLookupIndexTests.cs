using XperienceCommunity.LanguageDomains.Configuration;
using XperienceCommunity.LanguageDomains.Internal;

namespace XperienceCommunity.LanguageDomains.Tests.Internal;

[TestFixture]
public class HostnameLookupIndexTests
{
    /// <summary>
    /// Two hostnames in one channel, one in another - covers the common
    /// shapes the index is asked about: root + non-root on the same host,
    /// single-language host, and a separate channel for cross-channel
    /// scoping checks.
    /// </summary>
    private static HostnameCultureMappingOptions BuildOptions() => new()
    {
        Channels =
        {
            ["DancingGoatPages"] = new ChannelHostnameMapping
            {
                Hostnames =
                {
                    new HostnameMapping
                    {
                        Hostname = "en.example.com",
                        RootLanguage = "en",
                        Languages = { "en", "au" },
                    },
                    new HostnameMapping
                    {
                        Hostname = "es.example.com",
                        RootLanguage = "es",
                        Languages = { "es" },
                    },
                },
            },
            ["MarketingPages"] = new ChannelHostnameMapping
            {
                Hostnames =
                {
                    new HostnameMapping
                    {
                        Hostname = "marketing.example.com",
                        RootLanguage = "en",
                        Languages = { "en" },
                    },
                },
            },
        },
    };

    // ---------------------------------------------------------------------
    // Match
    // ---------------------------------------------------------------------

    [Test]
    public void Match_KnownHostNonRootPrefix_ReturnsLanguage()
    {
        var index = IndexFactory.Build(BuildOptions());

        var match = index.Match("en.example.com", "/au/articles");

        Assert.That(match, Is.Not.Null);
        Assert.That(match!.LanguageCode, Is.EqualTo("au"));
        Assert.That(match.IsRootMatch, Is.False);
        Assert.That(match.ChannelName, Is.EqualTo("DancingGoatPages"));
    }

    [Test]
    public void Match_KnownHostExactPrefix_ReturnsLanguage()
    {
        // Edge case: path exactly equals the prefix (no trailing segment).
        var index = IndexFactory.Build(BuildOptions());

        var match = index.Match("en.example.com", "/au");

        Assert.That(match, Is.Not.Null);
        Assert.That(match!.LanguageCode, Is.EqualTo("au"));
        Assert.That(match.IsRootMatch, Is.False);
    }

    [Test]
    public void Match_KnownHostFallsBackToRoot_WhenNoPrefixMatches()
    {
        var index = IndexFactory.Build(BuildOptions());

        var match = index.Match("en.example.com", "/articles");

        Assert.That(match, Is.Not.Null);
        Assert.That(match!.LanguageCode, Is.EqualTo("en"));
        Assert.That(match.IsRootMatch, Is.True);
    }

    [Test]
    public void Match_UnknownHost_ReturnsNull()
    {
        var index = IndexFactory.Build(BuildOptions());

        var match = index.Match("unknown.example.com", "/articles");

        Assert.That(match, Is.Null);
    }

    [Test]
    public void Match_EmptyHost_ReturnsNull()
    {
        var index = IndexFactory.Build(BuildOptions());

        Assert.That(index.Match(string.Empty, "/articles"), Is.Null);
    }

    // ---------------------------------------------------------------------
    // Hostname with no root language (optional RootLanguage)
    // ---------------------------------------------------------------------

    private static HostnameCultureMappingOptions BuildNoRootOptions() => new()
    {
        Channels =
        {
            ["DancingGoatPages"] = new ChannelHostnameMapping
            {
                Hostnames =
                {
                    new HostnameMapping
                    {
                        Hostname = "noroot.example.com",
                        RootLanguage = "",
                        Languages = { "en", "fr" },
                    },
                },
            },
        },
    };

    [Test]
    public void Match_HostWithNoRootLanguage_PrefixedPath_ReturnsNonRootMatch()
    {
        // /fr/about on a no-root host still routes to fr - the prefix-based
        // lookup is unaffected by the absence of a root.
        var index = IndexFactory.Build(BuildNoRootOptions());

        var match = index.Match("noroot.example.com", "/fr/about");

        Assert.That(match, Is.Not.Null);
        Assert.That(match!.LanguageCode, Is.EqualTo("fr"));
        Assert.That(match.IsRootMatch, Is.False);
    }

    [Test]
    public void Match_HostWithNoRootLanguage_BarePath_ReturnsNull()
    {
        // No language claims the bare root; Match returns null and the
        // package's middleware passes the request through to Kentico's
        // stock routing.
        var index = IndexFactory.Build(BuildNoRootOptions());

        Assert.That(index.Match("noroot.example.com", "/"), Is.Null);
        Assert.That(index.Match("noroot.example.com", "/about"), Is.Null);
    }

    [Test]
    public void FindForLanguage_NoRootLanguage_AlwaysReturnsNonRootEntry()
    {
        // With no RootLanguage set, every language on the host has IsRoot=false
        // and a non-empty DisplayPrefix. The URL retriever therefore always
        // emits "/{langcode}/..." for these languages - no language gets a
        // bare URL on this host.
        var index = IndexFactory.Build(BuildNoRootOptions());

        var en = index.FindForLanguage("DancingGoatPages", "en");
        var fr = index.FindForLanguage("DancingGoatPages", "fr");

        Assert.Multiple(() =>
        {
            Assert.That(en, Is.Not.Null);
            Assert.That(en!.IsRoot, Is.False);
            Assert.That(en.DisplayPrefix, Is.EqualTo("/en"));

            Assert.That(fr, Is.Not.Null);
            Assert.That(fr!.IsRoot, Is.False);
            Assert.That(fr.DisplayPrefix, Is.EqualTo("/fr"));
        });
    }

    [Test]
    public void Match_HostnameLookup_IsCaseInsensitive()
    {
        var index = IndexFactory.Build(BuildOptions());

        // Hosts in HTTP are case-insensitive; uppercased input still matches.
        var match = index.Match("EN.EXAMPLE.COM", "/articles");

        Assert.That(match, Is.Not.Null);
        Assert.That(match!.LanguageCode, Is.EqualTo("en"));
    }

    // ---------------------------------------------------------------------
    // FindForLanguage / OutboundEntry
    // ---------------------------------------------------------------------

    [Test]
    public void FindForLanguage_KnownChannelAndLanguage_ReturnsHostname()
    {
        var index = IndexFactory.Build(BuildOptions());

        var lookup = index.FindForLanguage("DancingGoatPages", "au");

        Assert.That(lookup, Is.Not.Null);
        Assert.That(lookup!.Hostname.Hostname, Is.EqualTo("en.example.com"));
        Assert.That(lookup.IsRoot, Is.False);
    }

    [Test]
    public void FindForLanguage_RootLanguage_DisplayPrefixIsEmpty()
    {
        var index = IndexFactory.Build(BuildOptions());

        var lookup = index.FindForLanguage("DancingGoatPages", "en");

        Assert.That(lookup, Is.Not.Null);
        Assert.That(lookup!.IsRoot, Is.True);
        Assert.That(lookup.DisplayPrefix, Is.Empty);
    }

    [Test]
    public void FindForLanguage_NonRootLanguage_DisplayPrefixIsSlashLang()
    {
        var index = IndexFactory.Build(BuildOptions());

        var lookup = index.FindForLanguage("DancingGoatPages", "au");

        Assert.That(lookup, Is.Not.Null);
        Assert.That(lookup!.DisplayPrefix, Is.EqualTo("/au"));
    }

    [Test]
    public void FindForLanguage_UnknownChannel_ReturnsNull()
    {
        var index = IndexFactory.Build(BuildOptions());

        Assert.That(index.FindForLanguage("UnknownChannel", "en"), Is.Null);
    }

    [Test]
    public void FindForLanguage_KnownChannelUnknownLanguage_ReturnsNull()
    {
        var index = IndexFactory.Build(BuildOptions());

        Assert.That(index.FindForLanguage("DancingGoatPages", "xx"), Is.Null);
    }

    [Test]
    public void FindForLanguage_IsCaseInsensitive()
    {
        var index = IndexFactory.Build(BuildOptions());

        var lookup = index.FindForLanguage("DANCINGGOATPAGES", "AU");

        Assert.That(lookup, Is.Not.Null);
        Assert.That(lookup!.Hostname.Hostname, Is.EqualTo("en.example.com"));
    }

    // ---------------------------------------------------------------------
    // ChannelHasLanguage
    // ---------------------------------------------------------------------

    [Test]
    public void ChannelHasLanguage_ConfiguredLanguage_ReturnsTrue()
    {
        var index = IndexFactory.Build(BuildOptions());

        Assert.That(index.ChannelHasLanguage("DancingGoatPages", "au"), Is.True);
    }

    [Test]
    public void ChannelHasLanguage_UnknownChannel_ReturnsFalse()
    {
        var index = IndexFactory.Build(BuildOptions());

        Assert.That(index.ChannelHasLanguage("UnknownChannel", "en"), Is.False);
    }

    [Test]
    public void ChannelHasLanguage_LanguageInOtherChannelOnly_ReturnsFalse()
    {
        // "es" is configured under DancingGoatPages but not MarketingPages.
        // The check must be channel-scoped.
        var index = IndexFactory.Build(BuildOptions());

        Assert.That(index.ChannelHasLanguage("MarketingPages", "es"), Is.False);
    }

    // ---------------------------------------------------------------------
    // Snapshot - HostnameEntry.AllPrefixes, ExcludedPathPrefixes
    // ---------------------------------------------------------------------

    [Test]
    public void Snapshot_HostnameEntries_CarryAllConfiguredLanguages()
    {
        var index = IndexFactory.Build(BuildOptions());

        var en = index.Current.ByHostname["en.example.com"];
        var es = index.Current.ByHostname["es.example.com"];
        var marketing = index.Current.ByHostname["marketing.example.com"];

        // Total 4 (lang, host) pairs across the three hostnames.
        Assert.Multiple(() =>
        {
            Assert.That(en.AllPrefixes, Has.Count.EqualTo(2));
            Assert.That(es.AllPrefixes, Has.Count.EqualTo(1));
            Assert.That(marketing.AllPrefixes, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Snapshot_AllPrefixes_PreInternsPrefixStrings()
    {
        var index = IndexFactory.Build(BuildOptions());

        var en = index.Current.ByHostname["en.example.com"];
        var au = en.AllPrefixes.First(p => p.LanguageCode == "au");

        Assert.That(au.SlashLang, Is.EqualTo("/au"));
        Assert.That(au.SlashLangSlash, Is.EqualTo("/au/"));
        Assert.That(au.IsRoot, Is.False);
    }

    [Test]
    public void Snapshot_ExcludedPathPrefixes_IncludesDefaults()
    {
        var index = IndexFactory.Build(BuildOptions());

        // Sanity: the package defaults made it into the snapshot.
        Assert.That(index.Current.ExcludedPathPrefixes, Does.Contain("/admin"));
        Assert.That(index.Current.ExcludedPathPrefixes, Does.Contain("/api"));
    }

    [Test]
    public void Snapshot_ExcludedPathPrefixes_AppendsAdditionalFromOptions()
    {
        var options = BuildOptions();
        options.AdditionalExcludedPathPrefixes.Add("/health");
        options.AdditionalExcludedPathPrefixes.Add("/webhooks/");

        var index = IndexFactory.Build(options);

        Assert.That(index.Current.ExcludedPathPrefixes, Does.Contain("/admin"));
        Assert.That(index.Current.ExcludedPathPrefixes, Does.Contain("/health"));
        Assert.That(index.Current.ExcludedPathPrefixes, Does.Contain("/webhooks/"));
    }

    // ---------------------------------------------------------------------
    // Snapshot - flags forwarded onto the snapshot
    // ---------------------------------------------------------------------

    [Test]
    public void Snapshot_CarriesEnableCanonicalRedirectFlag()
    {
        var options = BuildOptions();
        options.EnableCanonicalRedirect = false;

        var index = IndexFactory.Build(options);

        Assert.That(index.Current.EnableCanonicalRedirect, Is.False);
    }

    [Test]
    public void Snapshot_CarriesForwardedHostHeader()
    {
        var options = BuildOptions();
        options.ForwardedHostHeader = "X-Forwarded-Host";

        var index = IndexFactory.Build(options);

        Assert.That(index.Current.ForwardedHostHeader, Is.EqualTo("X-Forwarded-Host"));
    }

    // ---------------------------------------------------------------------
    // Hot-reload + IDisposable
    // ---------------------------------------------------------------------

    [Test]
    public void Snapshot_RebuildsOnOptionsChange()
    {
        var initial = BuildOptions();
        var monitor = new MutableOptionsMonitor<HostnameCultureMappingOptions>(initial);
        using var index = new HostnameLookupIndex(monitor);

        Assert.That(index.Match("new.example.com", "/"), Is.Null, "host shouldn't match initially");

        // Mutate: add a hostname under DancingGoatPages.
        var updated = BuildOptions();
        updated.Channels["DancingGoatPages"].Hostnames.Add(new HostnameMapping
        {
            Hostname = "new.example.com",
            RootLanguage = "fr",
            Languages = { "fr" },
        });
        monitor.Set(updated);

        var match = index.Match("new.example.com", "/");
        Assert.That(match, Is.Not.Null);
        Assert.That(match!.LanguageCode, Is.EqualTo("fr"));
    }

    [Test]
    public void Dispose_DetachesOnChangeSubscription()
    {
        var monitor = new MutableOptionsMonitor<HostnameCultureMappingOptions>(BuildOptions());
        var index = new HostnameLookupIndex(monitor);

        Assert.That(monitor.ListenerCount, Is.EqualTo(1), "index subscribed at construction");

        index.Dispose();

        Assert.That(monitor.ListenerCount, Is.EqualTo(0), "Dispose detaches subscription");
    }
}
