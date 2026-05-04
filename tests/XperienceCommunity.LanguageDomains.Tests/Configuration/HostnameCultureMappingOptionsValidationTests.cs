using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using XperienceCommunity.LanguageDomains.Configuration;
using XperienceCommunity.LanguageDomains.Extensions;

namespace XperienceCommunity.LanguageDomains.Tests.Configuration;

[TestFixture]
public class HostnameCultureMappingOptionsValidationTests
{
    /// <summary>
    /// Builds a minimal <see cref="IServiceProvider"/> with the package wired
    /// up against the given inline options dictionary, then resolves
    /// <see cref="IOptions{TOptions}"/>.<see cref="IOptions{TOptions}.Value"/>
    /// to trigger PostConfigure (where validation runs).
    /// </summary>
    private static (HostnameCultureMappingOptions? Resolved, OptionsValidationException? Error) TryResolve(
        Action<HostnameCultureMappingOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddHostnameCultureMapping(new ConfigurationBuilder().Build());
        services.Configure(configure);

        using var sp = services.BuildServiceProvider();
        try
        {
            var resolved = sp.GetRequiredService<IOptions<HostnameCultureMappingOptions>>().Value;
            return (resolved, null);
        }
        catch (OptionsValidationException ex)
        {
            return (null, ex);
        }
    }

    // ---------------------------------------------------------------------
    // Valid configurations
    // ---------------------------------------------------------------------

    [Test]
    public void EmptyConfig_IsValid()
    {
        var (resolved, ex) = TryResolve(_ => { });

        Assert.That(ex, Is.Null);
        Assert.That(resolved!.Channels, Is.Empty);
    }

    [Test]
    public void SingleHostname_SingleLanguage_IsValid()
    {
        var (resolved, ex) = TryResolve(o => o.Channels["DancingGoatPages"] = new ChannelHostnameMapping
        {
            Hostnames =
                {
                    new HostnameMapping
                    {
                        Hostname = "en.example.com",
                        RootLanguage = "en",
                        Languages = { "en" },
                    },
                },
        });

        Assert.That(ex, Is.Null);
        Assert.That(resolved!.Channels.ContainsKey("DancingGoatPages"), Is.True);
    }

    [Test]
    public void MultipleHostnames_DistinctLanguages_IsValid()
    {
        var (_, ex) = TryResolve(o => o.Channels["DancingGoatPages"] = new ChannelHostnameMapping
        {
            Hostnames =
                {
                    new HostnameMapping { Hostname = "en.example.com", RootLanguage = "en", Languages = { "en" } },
                    new HostnameMapping { Hostname = "fr.example.com", RootLanguage = "fr", Languages = { "fr" } },
                    new HostnameMapping { Hostname = "de.example.com", RootLanguage = "de", Languages = { "de" } },
                },
        });

        Assert.That(ex, Is.Null);
    }

    [Test]
    public void HostnameWithRootPlusNonRootLanguages_IsValid()
    {
        // The multi-language-per-host model: one root (served at /) plus
        // non-root languages reached via path prefixes.
        var (_, ex) = TryResolve(o => o.Channels["DancingGoatPages"] = new ChannelHostnameMapping
        {
            Hostnames =
                {
                    new HostnameMapping
                    {
                        Hostname = "en.example.com",
                        RootLanguage = "en",
                        Languages = { "en", "au" },
                    },
                },
        });

        Assert.That(ex, Is.Null);
    }

    [Test]
    public void SameHostname_AcrossDifferentChannels_IsValid()
    {
        // The validator scopes uniqueness per-channel. Cross-channel host
        // collisions are caught by Kentico's WebsiteChannelDomains itself.
        var (_, ex) = TryResolve(o =>
        {
            o.Channels["ChannelA"] = new ChannelHostnameMapping
            {
                Hostnames = { new HostnameMapping { Hostname = "shared.example.com", RootLanguage = "en", Languages = { "en" } } },
            };
            o.Channels["ChannelB"] = new ChannelHostnameMapping
            {
                Hostnames = { new HostnameMapping { Hostname = "shared.example.com", RootLanguage = "en", Languages = { "en" } } },
            };
        });

        Assert.That(ex, Is.Null);
    }

    // ---------------------------------------------------------------------
    // Invalid configurations - lenient mode (default): channel is dropped
    // ---------------------------------------------------------------------

    [Test]
    public void DuplicateHostname_WithinChannel_DropsChannel()
    {
        var (resolved, ex) = TryResolve(o => o.Channels["DancingGoatPages"] = new ChannelHostnameMapping
        {
            Hostnames =
                {
                    new HostnameMapping { Hostname = "shared.example.com", RootLanguage = "en", Languages = { "en" } },
                    new HostnameMapping { Hostname = "shared.example.com", RootLanguage = "fr", Languages = { "fr" } },
                },
        });

        Assert.That(ex, Is.Null, "default StrictValidation=false should not throw");
        Assert.That(resolved!.Channels, Does.Not.ContainKey("DancingGoatPages"));
    }

    [Test]
    public void RootLanguageMissingFromLanguages_DropsChannel()
    {
        var (resolved, ex) = TryResolve(o => o.Channels["DancingGoatPages"] = new ChannelHostnameMapping
        {
            Hostnames =
                {
                    new HostnameMapping
                    {
                        Hostname = "en.example.com",
                        RootLanguage = "en-NOT-IN-LIST",
                        Languages = { "en", "au" },
                    },
                },
        });

        Assert.That(ex, Is.Null);
        Assert.That(resolved!.Channels, Does.Not.ContainKey("DancingGoatPages"));
    }

    [Test]
    public void LanguageAppearsUnderTwoHostnames_InSameChannel_DropsChannel()
    {
        // The "no duplicate content across hostnames" rule.
        var (resolved, ex) = TryResolve(o => o.Channels["DancingGoatPages"] = new ChannelHostnameMapping
        {
            Hostnames =
                {
                    new HostnameMapping { Hostname = "a.example.com", RootLanguage = "en", Languages = { "en", "au" } },
                    new HostnameMapping { Hostname = "b.example.com", RootLanguage = "fr", Languages = { "fr", "au" } },
                },
        });

        Assert.That(ex, Is.Null);
        Assert.That(resolved!.Channels, Does.Not.ContainKey("DancingGoatPages"));
    }

    [Test]
    public void EmptyHostname_DropsChannel()
    {
        var (resolved, _) = TryResolve(o => o.Channels["DancingGoatPages"] = new ChannelHostnameMapping
        {
            Hostnames =
                {
                    new HostnameMapping { Hostname = "", RootLanguage = "en", Languages = { "en" } },
                },
        });

        Assert.That(resolved!.Channels, Does.Not.ContainKey("DancingGoatPages"));
    }

    [Test]
    public void EmptyRootLanguage_IsValid_WhenLanguagesNonEmpty()
    {
        // Optional RootLanguage: a hostname with no root delivers every
        // language via /{langcode}/... prefixes; bare-root requests fall
        // through to stock Kentico routing.
        var (resolved, ex) = TryResolve(o => o.Channels["DancingGoatPages"] = new ChannelHostnameMapping
        {
            Hostnames =
                {
                    new HostnameMapping { Hostname = "en.example.com", RootLanguage = "", Languages = { "en", "fr" } },
                },
        });

        Assert.That(ex, Is.Null);
        Assert.That(resolved!.Channels, Does.ContainKey("DancingGoatPages"));
    }

    [Test]
    public void EmptyLanguagesList_DropsChannel()
    {
        var (resolved, _) = TryResolve(o => o.Channels["DancingGoatPages"] = new ChannelHostnameMapping
        {
            Hostnames =
                {
                    new HostnameMapping { Hostname = "en.example.com", RootLanguage = "en", Languages = { } },
                },
        });

        Assert.That(resolved!.Channels, Does.Not.ContainKey("DancingGoatPages"));
    }

    [Test]
    public void InvalidChannel_DoesNotAffect_OtherValidChannels()
    {
        var (resolved, _) = TryResolve(o =>
        {
            o.Channels["BrokenChannel"] = new ChannelHostnameMapping
            {
                Hostnames =
                {
                    new HostnameMapping { Hostname = "x.example.com", RootLanguage = "missing", Languages = { "en" } },
                },
            };
            o.Channels["GoodChannel"] = new ChannelHostnameMapping
            {
                Hostnames =
                {
                    new HostnameMapping { Hostname = "good.example.com", RootLanguage = "en", Languages = { "en" } },
                },
            };
        });

        Assert.That(resolved!.Channels, Does.Not.ContainKey("BrokenChannel"));
        Assert.That(resolved.Channels, Does.ContainKey("GoodChannel"));
    }

    // ---------------------------------------------------------------------
    // Invalid configurations - strict mode: throws on startup
    // ---------------------------------------------------------------------

    [Test]
    public void StrictValidation_DuplicateHostname_Throws()
    {
        var (_, ex) = TryResolve(o =>
        {
            o.StrictValidation = true;
            o.Channels["DancingGoatPages"] = new ChannelHostnameMapping
            {
                Hostnames =
                {
                    new HostnameMapping { Hostname = "shared.example.com", RootLanguage = "en", Languages = { "en" } },
                    new HostnameMapping { Hostname = "shared.example.com", RootLanguage = "fr", Languages = { "fr" } },
                },
            };
        });

        Assert.That(ex, Is.Not.Null);
        Assert.That(ex!.Message, Does.Contain("DancingGoatPages"));
        Assert.That(ex.Message, Does.Contain("duplicated"));
    }

    [Test]
    public void StrictValidation_RootLanguageMissing_Throws()
    {
        var (_, ex) = TryResolve(o =>
        {
            o.StrictValidation = true;
            o.Channels["DancingGoatPages"] = new ChannelHostnameMapping
            {
                Hostnames =
                {
                    new HostnameMapping { Hostname = "en.example.com", RootLanguage = "missing", Languages = { "en" } },
                },
            };
        });

        Assert.That(ex, Is.Not.Null);
        Assert.That(ex!.Message, Does.Contain("not listed"));
    }

    [Test]
    public void StrictValidation_LanguageDuplicatedAcrossHostnames_Throws()
    {
        var (_, ex) = TryResolve(o =>
        {
            o.StrictValidation = true;
            o.Channels["DancingGoatPages"] = new ChannelHostnameMapping
            {
                Hostnames =
                {
                    new HostnameMapping { Hostname = "a.example.com", RootLanguage = "en", Languages = { "en" } },
                    new HostnameMapping { Hostname = "b.example.com", RootLanguage = "en", Languages = { "en" } },
                },
            };
        });

        Assert.That(ex, Is.Not.Null);
        Assert.That(ex!.Message, Does.Contain("canonical hostname"));
    }
}
