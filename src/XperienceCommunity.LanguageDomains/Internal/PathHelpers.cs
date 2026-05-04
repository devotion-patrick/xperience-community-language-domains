namespace XperienceCommunity.LanguageDomains.Internal;

/// <summary>
/// Pure URL-path helpers: storage-prefix derivation plus span-based
/// segment strip / prepend. Used by the path-prefix middleware (inbound
/// rewrite) and the URL retriever decorators (outbound rewrite).
/// </summary>
internal static class PathHelpers
{
    /// <summary>
    /// Computes the storage prefix Kentico uses for URL paths in
    /// <paramref name="languageCode"/>: empty when it is the channel's
    /// primary language, <c>"/{langcode}"</c> otherwise.
    /// </summary>
    public static string GetStoragePrefix(string languageCode, string? primaryLanguageCode)
    {
        if (string.IsNullOrEmpty(languageCode))
        {
            return string.Empty;
        }
        if (string.Equals(languageCode, primaryLanguageCode, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }
        return "/" + languageCode;
    }

    /// <summary>
    /// Strips a leading <paramref name="prefix"/> segment (already starting
    /// with <c>/</c>) from <paramref name="path"/>. Returns
    /// <paramref name="path"/> unchanged when the prefix is empty or the
    /// path doesn't carry it; returns <c>"/"</c> when stripping leaves
    /// nothing.
    ///
    /// <para>Span-based: the segment-followed-by-slash check is done by
    /// length+character probe rather than allocating <c>prefix + "/"</c>
    /// on every call. The exact-match path also avoids any allocation.</para>
    /// </summary>
    public static string StripLeadingSegment(string path, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return path;
        }
        var pathSpan = path.AsSpan();
        var prefixSpan = prefix.AsSpan();
        if (pathSpan.Length > prefixSpan.Length
            && pathSpan.StartsWith(prefixSpan, StringComparison.OrdinalIgnoreCase)
            && pathSpan[prefixSpan.Length] == '/')
        {
            return path[prefix.Length..];
        }
        if (pathSpan.Equals(prefixSpan, StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }
        return path;
    }

    /// <summary>
    /// Prepends <paramref name="prefix"/> (already starting with <c>/</c>)
    /// to <paramref name="path"/>, unless the path already carries it.
    /// Returns <paramref name="path"/> unchanged when the prefix is empty.
    ///
    /// <para>Span-based: like <see cref="StripLeadingSegment"/>, the
    /// segment-followed-by-slash detection avoids the
    /// <c>prefix + "/"</c> allocation that a naive implementation pays
    /// per call. Only the final concatenation (when actually prepending)
    /// allocates.</para>
    /// </summary>
    public static string PrependSegment(string path, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return path;
        }
        var pathSpan = path.AsSpan();
        var prefixSpan = prefix.AsSpan();
        if (pathSpan.Length > prefixSpan.Length
            && pathSpan.StartsWith(prefixSpan, StringComparison.OrdinalIgnoreCase)
            && pathSpan[prefixSpan.Length] == '/')
        {
            return path;
        }
        if (pathSpan.Equals(prefixSpan, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
        return prefix + (path.StartsWith('/') ? path : "/" + path);
    }
}
