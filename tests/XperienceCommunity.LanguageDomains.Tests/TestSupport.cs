using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using XperienceCommunity.LanguageDomains.Configuration;
using XperienceCommunity.LanguageDomains.Internal;

namespace XperienceCommunity.LanguageDomains.Tests;

/// <summary>
/// Shared test helpers / stubs used across the test suite. Hand-rolled instead
/// of pulling in a mocking library - the surface we need is small and explicit
/// stubs read better than fluent mock setup for middleware-style tests.
/// </summary>
internal static class TestSupport
{
    /// <summary>
    /// Builds a <see cref="DefaultHttpContext"/> populated with the given
    /// scheme/host/path/headers, ready to feed into a middleware under test.
    /// </summary>
    public static DefaultHttpContext BuildHttpContext(
        string method = "GET",
        string scheme = "https",
        string host = "example.com",
        string path = "/",
        string? queryString = null,
        IDictionary<string, string>? headers = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Scheme = scheme;
        ctx.Request.Host = new HostString(host);
        ctx.Request.Path = path;
        if (queryString != null)
        {
            ctx.Request.QueryString = new QueryString(queryString.StartsWith('?') ? queryString : "?" + queryString);
        }
        if (headers != null)
        {
            foreach (var (k, v) in headers)
            {
                ctx.Request.Headers[k] = v;
            }
        }
        return ctx;
    }
}

/// <summary>
/// Minimal <see cref="IOptionsMonitor{T}"/> stub. Returns a fixed value;
/// <see cref="OnChange"/> is a no-op (we never need to drive change
/// notifications in tests).
/// </summary>
internal sealed class FixedOptionsMonitor<T> : IOptionsMonitor<T>
{
    public FixedOptionsMonitor(T value) => CurrentValue = value;
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal static class IndexFactory
{
    /// <summary>
    /// Builds a <see cref="HostnameLookupIndex"/> over the given options for
    /// use in middleware/decorator unit tests. The index reads from a
    /// <see cref="FixedOptionsMonitor{T}"/>, so it captures one snapshot and
    /// never rebuilds.
    /// </summary>
    public static HostnameLookupIndex Build(HostnameCultureMappingOptions options)
        => new(new FixedOptionsMonitor<HostnameCultureMappingOptions>(options));
}

/// <summary>
/// <see cref="IOptionsMonitor{T}"/> stub whose value can be replaced at any
/// point, firing every <c>OnChange</c> listener with the new value. Used by
/// index-rebuild tests that need to verify the snapshot updates when
/// upstream options change.
/// </summary>
internal sealed class MutableOptionsMonitor<T> : IOptionsMonitor<T>
{
    private readonly List<Action<T, string?>> _listeners = [];

    public MutableOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; private set; }
    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);
        return new Subscription(() => _listeners.Remove(listener));
    }

    public void Set(T newValue)
    {
        CurrentValue = newValue;
        // Snapshot the listener list to avoid re-entrancy issues if a
        // listener disposes its own subscription mid-callback.
        var snapshot = _listeners.ToArray();
        foreach (var listener in snapshot)
        {
            listener(newValue, null);
        }
    }

    public int ListenerCount => _listeners.Count;

    private sealed class Subscription : IDisposable
    {
        private readonly Action _onDispose;
        public Subscription(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }
}

/// <summary>
/// Captured-state <see cref="RequestDelegate"/>. Records that it was invoked
/// so tests can assert whether the middleware called <c>_next</c> (passed
/// through) or short-circuited.
/// </summary>
internal sealed class CapturingNext
{
    public bool WasCalled { get; private set; }

    public RequestDelegate Delegate => _ =>
    {
        WasCalled = true;
        return Task.CompletedTask;
    };
}

/// <summary>
/// Lambda-backed <see cref="IChannelPrimaryLanguageResolver"/> for tests.
/// Pass a <see cref="Func{T, TResult}"/> that maps channel name to primary
/// language code (or <c>null</c> for unknown).
/// </summary>
internal sealed class FakePrimaryLanguageResolver(Func<string, string?> resolve)
    : IChannelPrimaryLanguageResolver
{
    public string? GetPrimaryLanguageCode(string channelName) => resolve(channelName);
}
