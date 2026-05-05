using System.Reflection;

using Kentico.Xperience.Admin.Websites.UIPages;

using Microsoft.Extensions.Logging;

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
///   <item><description><c>Retrieve(int webPageItemId, int languageId, ...)</c>
///         - intercepted; the returned collection of <see cref="UrlListItem"/>
///         is post-processed by <see cref="Rewriter"/>. Match is by name,
///         leading <c>(int, int)</c> args, and return type - tolerant of any
///         trailing optional arguments (e.g. v31 added a
///         <see cref="CancellationToken"/>; v30 has no such arg).</description></item>
///   <item><description>Anything else - blind pass-through to <see cref="Inner"/>.</description></item>
/// </list>
///
/// Fragile by nature: any rename or change to the leading <c>(int, int)</c>
/// shape in the internal admin API silently breaks this. The
/// <c>IWebPageUrlListItemsRetrieverContractTests</c> in the test project
/// reflects on the installed admin assembly at test time and fails loudly if
/// the contract drifts - check that test first when upgrading XbyK.
/// </summary>
public class HostnameAwareUrlListItemsRetrieverProxy : DispatchProxy
{
    /// <summary>The inner Kentico-provided retriever the proxy forwards to.</summary>
    public object? Inner { get; set; }

    /// <summary>The post-processor that rewrites the returned list items.</summary>
    public UrlListItemHostnameRewriter? Rewriter { get; set; }

    /// <summary>
    /// Optional logger used to emit a one-time error if a <c>Retrieve</c> call
    /// arrives with a shape we don't recognise (XbyK signature drift). Null in
    /// tests; production wires it through DI in
    /// <c>HostnameCultureMappingExtensions.AddHostnameAwareUrlListItemsRetrieverDecorator</c>.
    /// </summary>
    public ILogger? Logger { get; set; }

    // Gate so the runtime drift log fires at most once per process - one
    // surfaced error in the event log is enough to alert the operator;
    // repeating it on every URL-tab open just buries the signal.
    private int _shapeMismatchLogged;

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
        {
            return null;
        }

        object? result = targetMethod.Invoke(Inner, args);

        // Post-process the Retrieve result. Identifying by method name and the
        // (int webPageItemId, int languageId, ...) leading shape, since we
        // cannot reference the interface symbol at compile time. The arg count
        // varies across XbyK refreshes: v30.6.x and earlier ship a 2-arg
        // Retrieve(int, int), then v30.7.0 added a trailing CancellationToken
        // for a 3-arg Retrieve(int, int, CancellationToken) which has held
        // through every release on the 30.7+ and 31.x lines as of 31.4.3.
        // Trailing args after position 1 are not used by the rewrite, so we
        // accept any arity >= 2 - that's the explicit drift-tolerance.
        if (targetMethod.Name != "Retrieve")
        {
            return result;
        }

        bool argsMatch = args != null
            && args.Length >= 2
            && args[0] is int
            && args[1] is int;
        bool returnMatch = result is Task<IEnumerable<UrlListItem>>;

        if (argsMatch && returnMatch && Rewriter != null)
        {
            // Casts are safe by the conditions above. Pulling them inline keeps
            // the type-pattern matched values local to this branch.
            return RewriteAsync((Task<IEnumerable<UrlListItem>>)result!, (int)args![0]!, (int)args[1]!);
        }

        // Reached only when Retrieve was called but the shape doesn't match
        // our compatibility window. Most likely cause: a future XbyK release
        // changed the contract again. Emit a single descriptive error so the
        // operator sees the regression in the event log instead of just a
        // silent admin URLs-tab no-op. We only log if Rewriter is configured -
        // a missing rewriter is a "not wired up" state, not drift.
        if (Rewriter != null && (!argsMatch || !returnMatch))
        {
            LogShapeMismatchOnce(targetMethod, args, result);
        }

        return result;
    }

    private void LogShapeMismatchOnce(MethodInfo targetMethod, object?[]? args, object? result)
    {
        if (Logger == null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _shapeMismatchLogged, 1, 0) != 0)
        {
            return;
        }

        string argsDesc = args == null
            ? "<null>"
            : string.Join(", ", args.Select(a => a?.GetType().FullName ?? "null"));
        string returnDesc = result?.GetType().FullName ?? "<null>";

        Logger.LogError(
            "XperienceCommunity.LanguageDomains: HostnameAwareUrlListItemsRetrieverProxy "
            + "received a '{Method}' call but the shape doesn't match the expected "
            + "(int webPageItemId, int languageId, ...) -> Task<IEnumerable<UrlListItem>> "
            + "contract. Hostname rewriting on the admin URLs tab is silently disabled. "
            + "This usually means the installed Kentico.Xperience.Admin version drifted "
            + "the IWebPageUrlListItemsRetriever.Retrieve signature. Upgrade "
            + "XperienceCommunity.LanguageDomains or report it. "
            + "Got args=[{Args}], return={Return}.",
            targetMethod.Name,
            argsDesc,
            returnDesc);
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
