namespace MishaWeb;

internal enum AddressSuggestionSource
{
    Bookmark,
    History,
    Search
}

internal enum AddressSuggestionMatch
{
    Exact,
    Prefix,
    Fuzzy,
    Search
}

internal readonly record struct AddressSuggestion(
    int KeyboardIndex,
    AddressSuggestionSource Source,
    AddressSuggestionMatch Match,
    string Title,
    string Detail,
    string AcceptText,
    string NavigationTarget)
{
    public bool IsSearch => Source == AddressSuggestionSource.Search;
}

/// <summary>
/// Produces a small, local-only suggestion list from the current browser state.
/// The zero-based <see cref="AddressSuggestion.KeyboardIndex"/> is stable for the
/// returned list and can be used directly by Up/Down/Enter keyboard handling.
/// </summary>
internal static class AddressSuggestionEngine
{
    internal const int DefaultResultLimit = 6;
    internal const int MaximumResultLimit = 8;

    private const int MaximumBookmarksToScan = 200;
    private const int MaximumHistoryItemsToScan = 300;
    private const int MaximumQueryCharactersToMatch = 256;
    private const int MaximumTitleCharactersToMatch = 256;
    private const int MaximumUrlCharactersToMatch = 1_024;
    private const int MaximumUrlCharactersToDisplay = 512;
    private static readonly HashSet<string> EmptyUrlSet = new(BrowserPolicy.UrlComparer);
    private static readonly Dictionary<string, SuggestionUsageScore> EmptyUsageByUrl =
        new(0, BrowserPolicy.UrlComparer);
    [ThreadStatic]
    private static SuggestionIndexCache? threadIndexCache;

    public static IReadOnlyList<AddressSuggestion> GetSuggestions(
        string? input,
        BrowserState state,
        int maximumResults = DefaultResultLimit)
    {
        ArgumentNullException.ThrowIfNull(state);

        var limit = Math.Clamp(maximumResults, 0, MaximumResultLimit);
        var query = TextSafety.RemoveUnpairedSurrogates(input?.Trim() ?? string.Empty);
        if (limit == 0 || query.Length == 0) return Array.Empty<AddressSuggestion>();

        string? searchUrl = null;
        if (query.Length <= BrowserPolicy.MaximumUrlLength)
        {
            var candidateSearchUrl = BrowserPolicy.CreateSearchUrl(query, state.SearchProviderId);
            if (candidateSearchUrl.Length <= BrowserPolicy.MaximumUrlLength)
            {
                searchUrl = candidateSearchUrl;
            }
        }

        // Keep one slot for a deterministic, network-free Google search row.
        var localResultLimit = searchUrl is null ? limit : limit - 1;
        var topCandidates = localResultLimit == 0
            ? Array.Empty<Candidate>()
            : new Candidate[localResultLimit];
        var topCandidateCount = 0;
        if (localResultLimit > 0)
        {
            foreach (var indexedCandidate in GetIndexedCandidates(state))
            {
                AddCandidate(
                    indexedCandidate,
                    query,
                    topCandidates,
                    ref topCandidateCount);
            }
        }

        var localCount = topCandidateCount;
        var results = new AddressSuggestion[localCount + (searchUrl is null ? 0 : 1)];
        for (var index = 0; index < localCount; index++)
        {
            var candidate = topCandidates[index];
            results[index] = new AddressSuggestion(
                index,
                candidate.Source,
                candidate.Match,
                candidate.Title,
                TextSafety.FormatUrlForDisplay(candidate.Url, MaximumUrlCharactersToDisplay),
                candidate.Url,
                candidate.Url);
        }

        if (searchUrl is not null)
        {
            var searchIndex = results.Length - 1;
            results[searchIndex] = new AddressSuggestion(
                searchIndex,
                AddressSuggestionSource.Search,
                AddressSuggestionMatch.Search,
                $"Search {BrowserPolicy.GetSearchProviderName(state.SearchProviderId)} for \u201c{CompactForDisplay(query)}\u201d",
                $"{BrowserPolicy.GetSearchProviderName(state.SearchProviderId)} search",
                query,
                searchUrl);
        }
        return results;
    }

    private static void AddCandidate(
        IndexedCandidate indexedCandidate,
        string query,
        Candidate[] topCandidates,
        ref int topCandidateCount)
    {
        var match = Classify(query, indexedCandidate.Title, indexedCandidate.Url);
        if (!match.IsMatch) return;

        var candidate = new Candidate(
            indexedCandidate.Source,
            match.Kind,
            match.Quality,
            indexedCandidate.SourceIndex,
            indexedCandidate.Title,
            indexedCandidate.Url,
            indexedCandidate.AcceptedCount,
            indexedCandidate.LastAcceptedUtc,
            indexedCandidate.UrlHash);

        AddTopCandidate(candidate, topCandidates, ref topCandidateCount);
    }

    private static void AddTopCandidate(
        Candidate candidate,
        Candidate[] topCandidates,
        ref int topCandidateCount)
    {
        if (topCandidateCount == topCandidates.Length
            && CompareCandidates(candidate, topCandidates[topCandidateCount - 1]) >= 0)
        {
            return;
        }

        var existingIndex = -1;
        for (var index = 0; index < topCandidateCount; index++)
        {
            if (!UrlsEqual(topCandidates[index], candidate)) continue;
            existingIndex = index;
            break;
        }

        if (existingIndex >= 0)
        {
            if (CompareCandidates(candidate, topCandidates[existingIndex]) >= 0) return;

            for (var index = existingIndex; index < topCandidateCount - 1; index++)
            {
                topCandidates[index] = topCandidates[index + 1];
            }
            topCandidateCount--;
        }
        else if (topCandidateCount == topCandidates.Length)
        {
            topCandidateCount--;
        }

        var insertionIndex = topCandidateCount;
        while (insertionIndex > 0
               && CompareCandidates(candidate, topCandidates[insertionIndex - 1]) < 0)
        {
            topCandidates[insertionIndex] = topCandidates[insertionIndex - 1];
            insertionIndex--;
        }

        topCandidates[insertionIndex] = candidate;
        topCandidateCount++;
    }

    private static bool UrlsEqual(Candidate left, Candidate right)
    {
        if (left.UrlHash != right.UrlHash) return false;
        if (string.Equals(left.Url, right.Url, StringComparison.Ordinal)) return true;
        return BrowserPolicy.UrlComparer.Equals(left.Url, right.Url);
    }

    private static bool IsLegacyDuckDuckGoSearchUri(Uri uri)
    {
        return uri.IdnHost.Equals("duckduckgo.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/", StringComparison.Ordinal)
            && (uri.Query.StartsWith("?q=", StringComparison.OrdinalIgnoreCase)
                || uri.Query.Contains("&q=", StringComparison.OrdinalIgnoreCase));
    }

    private static int GetUrlHash(Uri uri)
    {
        var hash = new HashCode();
        hash.Add(uri.Scheme, StringComparer.OrdinalIgnoreCase);
        hash.Add(uri.IdnHost, StringComparer.OrdinalIgnoreCase);
        hash.Add(uri.Port);
        hash.Add(uri.UserInfo, StringComparer.Ordinal);
        hash.Add(uri.PathAndQuery, StringComparer.Ordinal);
        hash.Add(uri.Fragment, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    private static IndexedCandidate[] GetIndexedCandidates(BrowserState state)
    {
        var cache = threadIndexCache;
        if (cache is not null && cache.Matches(state)) return cache.Candidates;

        cache = SuggestionIndexCache.Create(state);
        threadIndexCache = cache;
        return cache.Candidates;
    }

    internal static void ReleaseCache(BrowserState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (threadIndexCache?.References(state) == true) threadIndexCache = null;
    }

    private static void AppendIndexedCandidate(
        List<IndexedCandidate> candidates,
        BookmarkEntry? item,
        int sourceIndex,
        HashSet<string> dismissedUrls,
        Dictionary<string, SuggestionUsageScore> usageByUrl)
    {
        if (item is null) return;
        AppendIndexedCandidate(
            candidates,
            item.Title,
            item.Url,
            AddressSuggestionSource.Bookmark,
            sourceIndex,
            dismissedUrls,
            usageByUrl);
    }

    private static void AppendIndexedCandidate(
        List<IndexedCandidate> candidates,
        HistoryEntry? item,
        int sourceIndex,
        HashSet<string> dismissedUrls,
        Dictionary<string, SuggestionUsageScore> usageByUrl)
    {
        if (item is null) return;
        AppendIndexedCandidate(
            candidates,
            item.Title,
            item.Url,
            AddressSuggestionSource.History,
            sourceIndex,
            dismissedUrls,
            usageByUrl);
    }

    private static void AppendIndexedCandidate(
        List<IndexedCandidate> candidates,
        string? title,
        string? url,
        AddressSuggestionSource source,
        int sourceIndex,
        HashSet<string> dismissedUrls,
        Dictionary<string, SuggestionUsageScore> usageByUrl)
    {
        if (string.IsNullOrWhiteSpace(url)
            || url.Length > BrowserPolicy.MaximumUrlLength
            || dismissedUrls.Contains(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl)
            || (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps)
            || IsLegacyDuckDuckGoSearchUri(parsedUrl))
        {
            return;
        }

        title = string.IsNullOrWhiteSpace(title)
            ? TextSafety.FormatUrlForDisplay(url, MaximumTitleCharactersToMatch)
            : title;
        var usage = usageByUrl.TryGetValue(url, out var recordedUsage)
            ? recordedUsage
            : default;
        candidates.Add(new IndexedCandidate(
            source,
            sourceIndex,
            title,
            url,
            GetUrlHash(parsedUrl),
            usage.AcceptedCount,
            usage.LastAcceptedUtc));
    }

    private static MatchResult Classify(string query, string title, string url)
    {
        if (title.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return MatchResult.Found(AddressSuggestionMatch.Exact, 0);
        }

        if (UrlEqualsInput(url.AsSpan(), query.AsSpan()))
        {
            return MatchResult.Found(AddressSuggestionMatch.Exact, 1);
        }

        // Oversized address-bar input still receives the search row, but does not
        // make every keystroke scan large state strings on extremely weak PCs.
        if (query.Length > MaximumQueryCharactersToMatch) return MatchResult.None;

        var querySpan = query.AsSpan();
        var titleSpan = Limit(title.AsSpan(), MaximumTitleCharactersToMatch);
        var urlSpan = Limit(url.AsSpan(), MaximumUrlCharactersToMatch);

        if (titleSpan.StartsWith(querySpan, StringComparison.OrdinalIgnoreCase))
        {
            return MatchResult.Found(AddressSuggestionMatch.Prefix, 0);
        }

        if (HasWordPrefix(titleSpan, querySpan))
        {
            return MatchResult.Found(AddressSuggestionMatch.Prefix, 1);
        }

        if (UrlStartsWithInput(urlSpan, querySpan))
        {
            return MatchResult.Found(AddressSuggestionMatch.Prefix, 2);
        }

        if (titleSpan.IndexOf(querySpan, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return MatchResult.Found(AddressSuggestionMatch.Fuzzy, 0);
        }

        if (urlSpan.IndexOf(querySpan, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return MatchResult.Found(AddressSuggestionMatch.Fuzzy, 1);
        }

        // A subsequence match handles compact inputs such as "ytb" -> "YouTube"
        // without the CPU and allocation cost of edit-distance algorithms.
        if (querySpan.Length >= 3 && IsSubsequence(querySpan, titleSpan))
        {
            return MatchResult.Found(AddressSuggestionMatch.Fuzzy, 2);
        }

        if (querySpan.Length >= 3 && IsSubsequence(querySpan, UrlWithoutScheme(urlSpan)))
        {
            return MatchResult.Found(AddressSuggestionMatch.Fuzzy, 3);
        }

        return MatchResult.None;
    }

    private static int CompareCandidates(Candidate left, Candidate right)
    {
        var comparison = ((int)left.Match).CompareTo((int)right.Match);
        if (comparison != 0) return comparison;

        comparison = right.AcceptedCount.CompareTo(left.AcceptedCount);
        if (comparison != 0) return comparison;

        comparison = right.LastAcceptedUtc.CompareTo(left.LastAcceptedUtc);
        if (comparison != 0) return comparison;

        comparison = left.Quality.CompareTo(right.Quality);
        if (comparison != 0) return comparison;

        comparison = ((int)left.Source).CompareTo((int)right.Source);
        if (comparison != 0) return comparison;

        comparison = left.SourceIndex.CompareTo(right.SourceIndex);
        if (comparison != 0) return comparison;

        return StringComparer.OrdinalIgnoreCase.Compare(left.Url, right.Url);
    }

    private static bool HasWordPrefix(ReadOnlySpan<char> value, ReadOnlySpan<char> query)
    {
        for (var index = 1; index + query.Length <= value.Length; index++)
        {
            if (char.IsLetterOrDigit(value[index - 1])) continue;
            if (value[index..].StartsWith(query, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool IsSubsequence(ReadOnlySpan<char> query, ReadOnlySpan<char> value)
    {
        var queryIndex = 0;
        for (var index = 0; index < value.Length && queryIndex < query.Length; index++)
        {
            if (char.ToUpperInvariant(value[index]) == char.ToUpperInvariant(query[queryIndex]))
            {
                queryIndex++;
            }
        }

        return queryIndex == query.Length;
    }

    private static bool UrlEqualsInput(ReadOnlySpan<char> url, ReadOnlySpan<char> query)
    {
        if (EqualsIgnoringTrailingSlash(url, query)) return true;

        var withoutScheme = UrlWithoutScheme(url);
        if (EqualsIgnoringTrailingSlash(withoutScheme, query)) return true;

        var withoutCommonPrefix = WithoutWww(withoutScheme);
        return EqualsIgnoringTrailingSlash(withoutCommonPrefix, query);
    }

    private static bool UrlStartsWithInput(ReadOnlySpan<char> url, ReadOnlySpan<char> query)
    {
        if (url.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return true;

        var withoutScheme = UrlWithoutScheme(url);
        return withoutScheme.StartsWith(query, StringComparison.OrdinalIgnoreCase)
            || WithoutWww(withoutScheme).StartsWith(query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EqualsIgnoringTrailingSlash(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        left = left.TrimEnd('/');
        right = right.TrimEnd('/');
        return left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }

    private static ReadOnlySpan<char> UrlWithoutScheme(ReadOnlySpan<char> value)
    {
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return value[8..];
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return value[7..];
        return value;
    }

    private static ReadOnlySpan<char> WithoutWww(ReadOnlySpan<char> value)
    {
        return value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? value[4..] : value;
    }

    private static ReadOnlySpan<char> Limit(ReadOnlySpan<char> value, int maximumLength)
    {
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private static string CompactForDisplay(string query)
    {
        const int maximumDisplayCharacters = 80;
        return TextSafety.TruncateWithEllipsis(query, maximumDisplayCharacters);
    }

    private sealed class SuggestionIndexCache
    {
        private SuggestionIndexCache(
            BrowserState state,
            BookmarkEntry[] bookmarks,
            HistoryEntry[] history,
            string[] dismissedUrls,
            AddressSuggestionUsage[] usage,
            IndexedCandidate[] candidates)
        {
            State = new WeakReference<BrowserState>(state);
            Bookmarks = bookmarks;
            History = history;
            DismissedUrls = dismissedUrls;
            Usage = usage;
            Candidates = candidates;
        }

        private WeakReference<BrowserState> State { get; }
        private BookmarkEntry[] Bookmarks { get; }
        private HistoryEntry[] History { get; }
        private string[] DismissedUrls { get; }
        private AddressSuggestionUsage[] Usage { get; }
        public IndexedCandidate[] Candidates { get; }

        public bool Matches(BrowserState state)
        {
            return State.TryGetTarget(out var cachedState)
                && ReferenceEquals(cachedState, state)
                && SequenceReferencesMatch(state.Bookmarks, Bookmarks, MaximumBookmarksToScan)
                && SequenceReferencesMatch(state.History, History, MaximumHistoryItemsToScan)
                && SequenceReferencesMatch(state.DismissedSuggestionUrls, DismissedUrls, int.MaxValue)
                && SequenceReferencesMatch(state.SuggestionUsage, Usage, int.MaxValue);
        }

        public bool References(BrowserState state) =>
            State.TryGetTarget(out var cachedState) && ReferenceEquals(cachedState, state);

        public static SuggestionIndexCache Create(BrowserState state)
        {
            var bookmarks = SnapshotPrefix(state.Bookmarks, MaximumBookmarksToScan);
            var history = SnapshotPrefix(state.History, MaximumHistoryItemsToScan);
            var dismissedSnapshot = SnapshotPrefix(state.DismissedSuggestionUrls, int.MaxValue);
            var usageSnapshot = SnapshotPrefix(state.SuggestionUsage, int.MaxValue);
            var dismissedUrls = dismissedSnapshot.Length == 0
                ? EmptyUrlSet
                : new HashSet<string>(dismissedSnapshot, BrowserPolicy.UrlComparer);
            var usageByUrl = BuildSuggestionUsageLookup(usageSnapshot);
            var candidates = new List<IndexedCandidate>(bookmarks.Length + history.Length);

            for (var index = 0; index < bookmarks.Length; index++)
            {
                AppendIndexedCandidate(
                    candidates,
                    bookmarks[index],
                    index,
                    dismissedUrls,
                    usageByUrl);
            }

            for (var index = 0; index < history.Length; index++)
            {
                AppendIndexedCandidate(
                    candidates,
                    history[index],
                    index,
                    dismissedUrls,
                    usageByUrl);
            }

            return new SuggestionIndexCache(
                state,
                bookmarks,
                history,
                dismissedSnapshot,
                usageSnapshot,
                candidates.ToArray());
        }

        private static T[] SnapshotPrefix<T>(IReadOnlyList<T> items, int maximumCount)
            where T : class
        {
            var count = Math.Min(items.Count, maximumCount);
            if (count == 0) return Array.Empty<T>();

            var snapshot = new T[count];
            for (var index = 0; index < count; index++) snapshot[index] = items[index];
            return snapshot;
        }

        private static bool SequenceReferencesMatch<T>(
            IReadOnlyList<T> current,
            T[] snapshot,
            int maximumCount)
            where T : class
        {
            var count = Math.Min(current.Count, maximumCount);
            if (count != snapshot.Length) return false;

            for (var index = 0; index < count; index++)
            {
                if (!ReferenceEquals(current[index], snapshot[index])) return false;
            }

            return true;
        }
    }

    private readonly record struct IndexedCandidate(
        AddressSuggestionSource Source,
        int SourceIndex,
        string Title,
        string Url,
        int UrlHash,
        int AcceptedCount,
        DateTimeOffset LastAcceptedUtc);

    private readonly record struct Candidate(
        AddressSuggestionSource Source,
        AddressSuggestionMatch Match,
        int Quality,
        int SourceIndex,
        string Title,
        string Url,
        int AcceptedCount,
        DateTimeOffset LastAcceptedUtc,
        int UrlHash);

    private static Dictionary<string, SuggestionUsageScore> BuildSuggestionUsageLookup(
        IReadOnlyList<AddressSuggestionUsage>? usageEntries)
    {
        if (usageEntries is null || usageEntries.Count == 0)
        {
            return EmptyUsageByUrl;
        }

        var usageByUrl = new Dictionary<string, SuggestionUsageScore>(usageEntries.Count, BrowserPolicy.UrlComparer);
        foreach (var usage in usageEntries)
        {
            if (usage is null
                || string.IsNullOrWhiteSpace(usage.Url)
                || !BrowserPolicy.IsHttpUrl(usage.Url))
            {
                continue;
            }
            var score = new SuggestionUsageScore(
                Math.Max(1, usage.AcceptedCount),
                usage.LastAcceptedUtc == default ? DateTimeOffset.UnixEpoch : usage.LastAcceptedUtc);
            if (!usageByUrl.TryGetValue(usage.Url, out var existing)
                || score.AcceptedCount > existing.AcceptedCount
                || (score.AcceptedCount == existing.AcceptedCount
                    && score.LastAcceptedUtc > existing.LastAcceptedUtc))
            {
                usageByUrl[usage.Url] = score;
            }
        }

        return usageByUrl;
    }

    private readonly record struct SuggestionUsageScore(
        int AcceptedCount,
        DateTimeOffset LastAcceptedUtc);

    private readonly record struct MatchResult(
        bool IsMatch,
        AddressSuggestionMatch Kind,
        int Quality)
    {
        public static MatchResult None => new(false, default, int.MaxValue);

        public static MatchResult Found(AddressSuggestionMatch kind, int quality)
        {
            return new MatchResult(true, kind, quality);
        }
    }
}
