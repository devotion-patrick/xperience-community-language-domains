using System.Reflection;

using CMS.Websites;

using Kentico.Xperience.Admin.Websites.UIPages;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using XperienceCommunity.LanguageDomains.Configuration;
using XperienceCommunity.LanguageDomains.Decorators;
using XperienceCommunity.LanguageDomains.Internal;
using XperienceCommunity.LanguageDomains.UrlListRewriter;

namespace XperienceCommunity.LanguageDomains.Extensions;

/// <summary>
/// Service-collection extensions for wiring up XperienceCommunity.LanguageDomains.
/// </summary>
public static class HostnameCultureMappingExtensions
{
    /// <summary>
    /// Binds <see cref="HostnameCultureMappingOptions"/> from configuration.
    /// The decorators (<see cref="HostnameAwarePreferredLanguageRetriever"/>
    /// and <see cref="HostnameAwareWebPageUrlRetriever"/>) live in this same
    /// assembly and register themselves through
    /// <c>[assembly: RegisterImplementation]</c>; Kentico's class discovery
    /// (this assembly is marked <c>[assembly: AssemblyDiscoverable]</c>)
    /// turns those into a factory chain over the inner implementation - the
    /// standard XbyK decorator pattern.
    ///
    /// <para>Validation runs eagerly at startup. Each channel is checked for
    /// hostname uniqueness, a non-empty <see cref="HostnameMapping.RootLanguage"/>
    /// that appears in <see cref="HostnameMapping.Languages"/>, and the
    /// "one canonical hostname per language" rule. Invalid channels are
    /// logged at <c>Error</c> and dropped from the effective options
    /// (routing for those channels falls back to stock Kentico behavior); set
    /// <see cref="HostnameCultureMappingOptions.StrictValidation"/> to
    /// <c>true</c> to fail startup instead.</para>
    ///
    /// <para>Also auto-populates <see cref="WebsiteChannelDomainOptions"/>
    /// with hostnames listed in <see cref="HostnameCultureMappingOptions"/>:
    /// <list type="bullet">
    ///   <item><description>You do <strong>not</strong> need to also list
    ///         these hostnames under <c>WebsiteChannelDomains</c> - one
    ///         config section is enough.</description></item>
    ///   <item><description>Anything already in <c>WebsiteChannelDomains</c>
    ///         (e.g. legacy aliases or vanity hostnames not used for language
    ///         routing) is <strong>preserved</strong>; we union onto, never
    ///         replace.</description></item>
    ///   <item><description>Listing the same host in both sections is fine -
    ///         the merge is case-insensitive and de-duplicates.</description></item>
    ///   <item><description>A host that's only in <c>WebsiteChannelDomains</c>
    ///         remains a valid channel domain for Kentico, but this package
    ///         won't apply language-routing rules to it.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public static IServiceCollection AddHostnameCultureMapping(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<HostnameCultureMappingOptions>()
            .Bind(configuration.GetSection(HostnameCultureMappingOptions.SectionKey))
            .PostConfigure<ILoggerFactory>(ValidateAndPrune)
            // No-op validator - exists so ValidateOnStart() forces eager
            // option resolution at app start (so PostConfigure runs and a
            // misconfigured StrictValidation=true fails fast).
            .Validate(_ => true)
            .ValidateOnStart();

        // Singleton: the resolver carries a process-lifetime cache of the
        // (channel name -> primary content language code) mapping. Channel
        // primary-language is configured in admin and effectively immutable
        // at runtime; caching it across requests avoids a per-link DB
        // round-trip on the foreign-key WebsiteChannelInfo lookup, which is
        // not covered by Kentico's hash-table cache.
        services.AddSingleton<IChannelPrimaryLanguageResolver, ChannelPrimaryLanguageResolver>();

        // Singleton: the index is rebuilt only when options change (rare;
        // typically once at startup). Per-request reads are lock-free against
        // a volatile snapshot reference.
        services.AddSingleton<HostnameLookupIndex>();

        // Union our hosts into Kentico's WebsiteChannelDomainOptions. PostConfigure
        // runs after the consumer's own Configure<WebsiteChannelDomainOptions>(...)
        // call, so we see (and keep) anything that section already populated.
        // Net result: a host listed in either config section ends up in the
        // channel's domain list. Listing in both is a no-op (case-insensitive
        // dedupe).
        services.AddOptions<WebsiteChannelDomainOptions>()
            .PostConfigure<IOptions<HostnameCultureMappingOptions>>((domainOptions, cultureAccessor) =>
            {
                var culture = cultureAccessor.Value;
                foreach (var (channelName, channelMapping) in culture.Channels)
                {
                    if (!domainOptions.DomainOverrides.TryGetValue(channelName, out var entry))
                    {
                        entry = new WebsiteChannelDomains();
                        domainOptions.DomainOverrides[channelName] = entry;
                    }

                    // Preserve whatever WebsiteChannelDomains added (legacy
                    // aliases, non-language-related hostnames) and add our
                    // language hosts on top.
                    var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (entry.Domains != null)
                    {
                        foreach (string? d in entry.Domains)
                        {
                            merged.Add(d);
                        }
                    }
                    foreach (var hm in channelMapping.Hostnames.Where(hm => !string.IsNullOrWhiteSpace(hm.Hostname)))
                    {
                        merged.Add(hm.Hostname);
                    }

                    entry.Domains = merged.ToList();
                }
            });

        return services;
    }

    /// <summary>
    /// Decorates the internal
    /// <c>Kentico.Xperience.Admin.Websites.UIPages.IWebPageUrlListItemsRetriever</c>
    /// service so the URLs tab in admin shows hostname-rewritten URLs to match
    /// the live site. The interface is <c>internal</c>, so we resolve its
    /// <see cref="Type"/> via reflection at runtime and substitute the
    /// existing registration with a <see cref="DispatchProxy"/>-backed factory.
    ///
    /// Must run after Kentico's services are registered so that we can capture
    /// the inner descriptor.
    /// </summary>
    public static IServiceCollection AddHostnameAwareUrlListItemsRetrieverDecorator(this IServiceCollection services)
    {
        services.AddSingleton<UrlListItemHostnameRewriter>();

        var interfaceType = typeof(UrlListItem).Assembly
            .GetType("Kentico.Xperience.Admin.Websites.UIPages.IWebPageUrlListItemsRetriever");
        if (interfaceType == null)
        {
            return services;
        }

        var existing = services.LastOrDefault(d => d.ServiceType == interfaceType);
        if (existing == null)
        {
            return services;
        }

        services.Remove(existing);

        var dispatchCreate = typeof(DispatchProxy)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == nameof(DispatchProxy.Create) && m.IsGenericMethodDefinition);
        var createForOurInterface = dispatchCreate.MakeGenericMethod(interfaceType, typeof(HostnameAwareUrlListItemsRetrieverProxy));

        services.Add(new ServiceDescriptor(
            interfaceType,
            sp =>
            {
                object inner = MaterializeInner(sp, existing);
                var proxy = (HostnameAwareUrlListItemsRetrieverProxy)createForOurInterface.Invoke(null, null)!;
                proxy.Inner = inner;
                proxy.Rewriter = sp.GetRequiredService<UrlListItemHostnameRewriter>();
                return proxy;
            },
            existing.Lifetime));

        return services;
    }

    private static object MaterializeInner(IServiceProvider sp, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is not null)
        {
            return descriptor.ImplementationInstance;
        }
        if (descriptor.ImplementationFactory is not null)
        {
            return descriptor.ImplementationFactory(sp);
        }
        if (descriptor.ImplementationType is not null)
        {
            return ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType);
        }
        throw new InvalidOperationException(
            $"ServiceDescriptor for {descriptor.ServiceType.FullName} has no implementation source.");
    }

    // --- Validation ----------------------------------------------------------

    private static void ValidateAndPrune(HostnameCultureMappingOptions opts, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("XperienceCommunity.LanguageDomains.Configuration");
        var allErrors = new List<string>();
        var invalidChannels = new List<string>();

        foreach (var (channelName, channelMapping) in opts.Channels)
        {
            var errors = ValidateChannel(channelMapping);
            if (errors.Count == 0)
            {
                continue;
            }

            foreach (string err in errors)
            {
                logger.LogError(
                    "XperienceCommunity:LanguageDomains channel '{ChannelName}' is invalid: {Error}",
                    channelName,
                    err);
                allErrors.Add($"[{channelName}] {err}");
            }
            invalidChannels.Add(channelName);
        }

        if (allErrors.Count > 0 && opts.StrictValidation)
        {
            throw new OptionsValidationException(
                nameof(HostnameCultureMappingOptions),
                typeof(HostnameCultureMappingOptions),
                allErrors);
        }

        foreach (string ch in invalidChannels)
        {
            opts.Channels.Remove(ch);
            logger.LogWarning(
                "XperienceCommunity:LanguageDomains: channel '{ChannelName}' was dropped due to validation errors. Routing for this channel falls back to stock Kentico behavior. Set StrictValidation=true in options to fail startup instead.",
                ch);
        }
    }

    private static List<string> ValidateChannel(ChannelHostnameMapping channelMapping)
    {
        var errors = new List<string>();

        if (channelMapping.Hostnames == null || channelMapping.Hostnames.Count == 0)
        {
            // Empty channel - nothing to validate, nothing to drop.
            return errors;
        }

        var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenLanguagesAcrossChannel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < channelMapping.Hostnames.Count; i++)
        {
            var hm = channelMapping.Hostnames[i];
            string loc = $"hostnames[{i}]";

            if (string.IsNullOrWhiteSpace(hm.Hostname))
            {
                errors.Add($"{loc}.Hostname is empty");
                continue;
            }

            if (!seenHosts.Add(hm.Hostname))
            {
                errors.Add($"{loc}.Hostname '{hm.Hostname}' is duplicated within the channel");
            }

            // RootLanguage is optional. When omitted, the hostname has no
            // language at "/" - all configured languages are reached via
            // /{langcode}/... prefixes, and a bare-root request falls
            // through to stock Kentico routing (which uses the channel's
            // primary content language). When set, it must appear in
            // Languages (validated below).

            if (hm.Languages == null || hm.Languages.Count == 0)
            {
                errors.Add($"{loc}.Languages is empty");
                continue;
            }

            bool rootMatched = false;
            for (int j = 0; j < hm.Languages.Count; j++)
            {
                string lang = hm.Languages[j];
                if (string.IsNullOrWhiteSpace(lang))
                {
                    errors.Add($"{loc}.Languages[{j}] is empty");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(hm.RootLanguage)
                    && string.Equals(lang, hm.RootLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    rootMatched = true;
                }

                if (!seenLanguagesAcrossChannel.Add(lang))
                {
                    errors.Add(
                        $"{loc}.Languages: language '{lang}' already appears under another hostname in this channel - each language must have exactly one canonical hostname per channel (multiple hostnames serving identical content is duplicate-content / bad SEO)");
                }
            }

            if (!string.IsNullOrWhiteSpace(hm.RootLanguage) && !rootMatched)
            {
                errors.Add($"{loc}.RootLanguage '{hm.RootLanguage}' is not listed in {loc}.Languages");
            }
        }

        return errors;
    }
}
