using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using SoFresh.Core.Domain;

namespace SoFresh.Core.Information;

public sealed class MicrosoftLearnFileInformationProvider : IFileTypeInformationProvider
{
    private static readonly Uri SearchEndpoint = new("https://learn.microsoft.com/api/search");
    private static readonly HttpClient SharedClient = CreateSharedClient();
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public MicrosoftLearnFileInformationProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedClient;
    }

    public async Task<FileTypeInformationResult> SearchAsync(
        FileTypeInformationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = NormalizeQuery(query);
        var cacheKey = $"{normalized.Locale}|{normalized.Term}|{normalized.MaximumResults}";
        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(cacheKey, out var cached) && now - cached.StoredAtUtc <= CacheLifetime)
        {
            return cached.Result with { FromCache = true };
        }

        var requestUri = BuildRequestUri(normalized);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
            var sources = ParseSources(document.RootElement, normalized.MaximumResults);
            var result = new FileTypeInformationResult(
                normalized.Term,
                sources,
                now,
                IsOffline: false,
                FromCache: false,
                sources.Count == 0
                    ? "Microsoft Learn returned no relevant documentation. No automated decision was made."
                    : "Information from Microsoft Learn. Always review the context before modifying a file.");
            _cache[cacheKey] = new CacheEntry(now, result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return new FileTypeInformationResult(
                normalized.Term,
                Array.Empty<FileTypeInformationSource>(),
                now,
                IsOffline: true,
                FromCache: false,
                "Microsoft Learn search is unavailable. Local analysis will continue without authorizing any deletion.");
        }
    }

    private static NormalizedQuery NormalizeQuery(FileTypeInformationQuery query)
    {
        if (query.MaximumResults is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "The number of results must be between 1 and 10.");
        }

        var locale = query.Locale.Trim().ToLowerInvariant();
        if (locale.Length != 5
            || locale[2] != '-'
            || !locale[..2].All(char.IsAsciiLetter)
            || !locale[3..].All(char.IsAsciiLetter))
        {
            throw new ArgumentException("Invalid locale; use a language-region format such as en-us.", nameof(query));
        }

        string? extension = null;
        if (!string.IsNullOrWhiteSpace(query.Extension))
        {
            extension = query.Extension.Trim().ToLowerInvariant();
            if (!extension.StartsWith('.'))
            {
                extension = $".{extension}";
            }

            if (extension.Length is < 2 or > 17 || extension[1..].Any(character => !char.IsAsciiLetterOrDigit(character)))
            {
                throw new ArgumentException(
                    "Only generic extensions made up of letters and numbers are accepted, not names or paths.",
                    nameof(query));
            }
        }

        if (extension is null && query.Category is null)
        {
            throw new ArgumentException("Specify a generic extension or a category.", nameof(query));
        }

        var subject = extension is not null
            ? $"{extension} file extension"
            : $"{query.Category} file category";
        var term = $"Windows {subject} safe to delete cleanup";
        return new NormalizedQuery(term, locale, query.MaximumResults);
    }

    private static Uri BuildRequestUri(NormalizedQuery query)
    {
        var parameters = new Dictionary<string, string>
        {
            ["search"] = query.Term,
            ["locale"] = query.Locale,
            ["$filter"] = "category eq 'Documentation'",
            ["$top"] = query.MaximumResults.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var queryString = string.Join(
            "&",
            parameters.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new UriBuilder(SearchEndpoint) { Query = queryString }.Uri;
    }

    private static IReadOnlyList<FileTypeInformationSource> ParseSources(JsonElement root, int maximumResults)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<FileTypeInformationSource>();
        }

        var sources = new List<FileTypeInformationSource>(maximumResults);
        foreach (var item in results.EnumerateArray())
        {
            if (sources.Count >= maximumResults)
            {
                break;
            }

            var title = GetString(item, "title");
            var urlText = GetString(item, "url");
            if (string.IsNullOrWhiteSpace(title)
                || !Uri.TryCreate(urlText, UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps
                || !url.Host.Equals("learn.microsoft.com", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var description = GetString(item, "description") ?? string.Empty;
            var lastUpdatedText = GetString(item, "lastUpdatedDate") ?? GetString(item, "lastUpdatedDateTime");
            DateTimeOffset? lastUpdated = DateTimeOffset.TryParse(
                lastUpdatedText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsedDate)
                ? parsedDate
                : null;

            sources.Add(new FileTypeInformationSource(
                title,
                url,
                description,
                lastUpdated,
                InformationConfidence.OfficialMicrosoftSource));
        }

        return sources;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static HttpClient CreateSharedClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SoFresh/1.0");
        return client;
    }

    private sealed record NormalizedQuery(string Term, string Locale, int MaximumResults);

    private sealed record CacheEntry(DateTimeOffset StoredAtUtc, FileTypeInformationResult Result);
}
