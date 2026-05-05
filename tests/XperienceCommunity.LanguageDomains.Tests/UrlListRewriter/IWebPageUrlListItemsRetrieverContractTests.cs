using Kentico.Xperience.Admin.Websites.UIPages;

namespace XperienceCommunity.LanguageDomains.Tests.UrlListRewriter;

/// <summary>
/// Drift detector for the internal Kentico admin contract that
/// <see cref="LanguageDomains.UrlListRewriter.HostnameAwareUrlListItemsRetrieverProxy"/>
/// targets via reflection.
///
/// <para>The proxy intercepts
/// <c>Kentico.Xperience.Admin.Websites.UIPages.IWebPageUrlListItemsRetriever.Retrieve</c>
/// at runtime by name + leading-shape match. We have no compile-time symbol
/// (the interface is internal to the admin assembly), so silent drift here -
/// rename, parameter reorder, return-type change - manifests in production as
/// "URLs tab no longer reflects our hostname rewriting" with no error logged.</para>
///
/// <para>This test reflects on the actually-installed <c>UrlListItem</c>'s
/// declaring assembly and asserts the contract the proxy depends on. If a
/// future XbyK release breaks the contract, this test fails at CI - update
/// the proxy and the matching tests, then revisit this contract.</para>
///
/// <para>Known shapes (verified by reflecting on each NuGet'd release):
/// <list type="bullet">
///   <item><description>v30.6.x (incl. 30.6.4): <c>Task&lt;IEnumerable&lt;UrlListItem&gt;&gt; Retrieve(int, int)</c></description></item>
///   <item><description>v30.7.0 onwards (verified through v31.4.3): <c>Task&lt;IEnumerable&lt;UrlListItem&gt;&gt; Retrieve(int, int, CancellationToken)</c></description></item>
/// </list>
/// The trailing-<c>CancellationToken</c> drift was introduced in <b>v30.7.0</b>.
/// The proxy tolerates both shapes via an "arity >= 2 + leading (int, int)"
/// match.
/// </para>
/// </summary>
[TestFixture]
public class IWebPageUrlListItemsRetrieverContractTests
{
    private const string InterfaceName = "Kentico.Xperience.Admin.Websites.UIPages.IWebPageUrlListItemsRetriever";

    private static Type? ResolveInterfaceType()
        => typeof(UrlListItem).Assembly.GetType(InterfaceName);

    [Test]
    public void InterfaceType_StillExistsInAdminAssembly()
    {
        var type = ResolveInterfaceType();

        Assert.That(type, Is.Not.Null,
            $"'{InterfaceName}' was not found on {typeof(UrlListItem).Assembly.GetName().Name}. "
            + "The proxy locates this internal interface by full name. If it has been renamed, moved, "
            + "or replaced, update HostnameCultureMappingExtensions.AddHostnameAwareUrlListItemsRetrieverDecorator() "
            + "and HostnameAwareUrlListItemsRetrieverProxy to match the new contract.");
        Assert.That(type!.IsInterface, Is.True, $"'{InterfaceName}' is no longer an interface.");
    }

    [Test]
    public void RetrieveMethod_ExistsWithLeadingIntIntShape()
    {
        var type = ResolveInterfaceType();
        Assume.That(type, Is.Not.Null, "InterfaceType_StillExistsInAdminAssembly will report the underlying drift.");

        var retrieve = type!.GetMethod("Retrieve");
        Assert.That(retrieve, Is.Not.Null,
            "'Retrieve' method missing from IWebPageUrlListItemsRetriever. The URLs tab no longer dispatches "
            + "through this entry point - the proxy can't intercept anything.");

        var parameters = retrieve!.GetParameters();
        Assert.That(parameters.Length, Is.GreaterThanOrEqualTo(2),
            $"Retrieve has {parameters.Length} parameter(s); proxy assumes leading (int webPageItemId, int languageId).");
        Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(int)),
            "Retrieve's first parameter is no longer 'int'. Proxy assumes (int webPageItemId, ...).");
        Assert.That(parameters[1].ParameterType, Is.EqualTo(typeof(int)),
            "Retrieve's second parameter is no longer 'int'. Proxy assumes (..., int languageId, ...).");
    }

    [Test]
    public void RetrieveMethod_ReturnsTaskOfEnumerableOfUrlListItem()
    {
        var type = ResolveInterfaceType();
        Assume.That(type, Is.Not.Null);

        var retrieve = type!.GetMethod("Retrieve");
        Assume.That(retrieve, Is.Not.Null);

        Assert.That(retrieve!.ReturnType, Is.EqualTo(typeof(Task<IEnumerable<UrlListItem>>)),
            $"Retrieve's return type is {retrieve.ReturnType}, but the rewriter expects "
            + "Task<IEnumerable<UrlListItem>>. Adjust HostnameAwareUrlListItemsRetrieverProxy.Invoke "
            + "(the 'result is Task<IEnumerable<UrlListItem>>' pattern) and the rewriter post-processing.");
    }
}
