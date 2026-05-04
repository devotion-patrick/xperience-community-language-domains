using System.Reflection;

using Kentico.Xperience.Admin.Websites.UIPages;

namespace XperienceCommunity.LanguageDomains.UrlListRewriter;

/// <summary>
/// Runtime <see cref="DispatchProxy"/> that decorates Kentico's
/// <c>Kentico.Xperience.Admin.Websites.UIPages.IWebPageUrlListItemsRetriever</c>.
/// We use a dispatch proxy because that interface is <c>internal</c> to the
/// admin assembly - we cannot reference it directly to write a normal
/// decorator class or use <c>[assembly: RegisterImplementation]</c>.
///
/// Two methods are forwarded:
/// <list type="bullet">
///   <item><description><c>Retrieve(int webPageItemId, int languageId, CancellationToken)</c>
///         - intercepted; the returned collection of <see cref="UrlListItem"/>
///         is post-processed by <see cref="Rewriter"/>.</description></item>
///   <item><description>Anything else - blind pass-through to <see cref="Inner"/>.</description></item>
/// </list>
///
/// Fragile by nature: any rename or signature change in the internal admin
/// API silently breaks this. Keep an eye on the package upgrades.
/// </summary>
public class HostnameAwareUrlListItemsRetrieverProxy : DispatchProxy
{
    /// <summary>The inner Kentico-provided retriever the proxy forwards to.</summary>
    public object? Inner { get; set; }

    /// <summary>The post-processor that rewrites the returned list items.</summary>
    public UrlListItemHostnameRewriter? Rewriter { get; set; }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
        {
            return null;
        }

        object? result = targetMethod.Invoke(Inner, args);

        // Post-process the Retrieve result. Identifying by method name + arity
        // since we cannot reference the interface symbol at compile time.
        if (targetMethod.Name == "Retrieve"
            && args is { Length: 3 }
            && args[0] is int webPageItemId
            && args[1] is int languageId
            && result is Task<IEnumerable<UrlListItem>> task
            && Rewriter != null)
        {
            return RewriteAsync(task, webPageItemId, languageId);
        }

        return result;
    }

    private async Task<IEnumerable<UrlListItem>> RewriteAsync(
        Task<IEnumerable<UrlListItem>> innerTask,
        int webPageItemId,
        int languageId)
    {
        var items = await innerTask;
        return Rewriter!.Rewrite(webPageItemId, languageId, items);
    }
}
