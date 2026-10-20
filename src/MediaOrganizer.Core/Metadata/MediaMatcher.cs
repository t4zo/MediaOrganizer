using System.Globalization;
using System.Text;

namespace MediaOrganizer.Core.Metadata;

/// <summary>Resultado da identificação: candidato escolhido e se a escolha é incerta.</summary>
public sealed record MatchResult(MediaCandidate Candidate, bool LowConfidence);

/// <summary>
/// Identifica a série/filme no TMDb a partir dos nomes da pasta e dos arquivos.
/// Busca em inglês e, se não houver correspondência exata, em pt-BR (pastas como "Round 6").
/// </summary>
public sealed partial class MediaMatcher(TmdbClient tmdb)
{
    private static readonly string[] SearchLanguages = [LanguagePolicy.English, "pt-BR"];

    public async Task<MatchResult?> FindAsync(MediaKind kind, IReadOnlyList<string> queries, int? year, CancellationToken ct)
    {
        (MediaCandidate Candidate, int Score)? best = null;

        foreach (var language in SearchLanguages)
        {
            foreach (var query in queries.Where(q => q.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var results = await SearchAsync(kind, query, year, language, ct).ConfigureAwait(false);
                if (results.Count == 0 && year is not null)
                    results = await SearchAsync(kind, query, null, language, ct).ConfigureAwait(false);

                foreach (var candidate in results.Take(10))
                {
                    var score = Score(candidate, query, year);
                    if (best is null || score > best.Value.Score) best = (candidate, score);
                }
            }

            if (best is { Score: >= ExactMatch }) break;
        }

        if (best is null) return null;

        // Busca os detalhes em inglês para ter o nome canônico (a busca em pt-BR traz o título traduzido).
        var details = await GetDetailsAsync(kind, best.Value.Candidate.Id, ct).ConfigureAwait(false) ?? best.Value.Candidate;
        var lowConfidence = best.Value.Score < ExactMatch || (year is not null && details.Year is not null && Math.Abs(details.Year.Value - year.Value) > 1);
        return new MatchResult(details, lowConfidence);
    }

    public Task<IReadOnlyList<MediaCandidate>> SearchAsync(MediaKind kind, string query, int? year, string language, CancellationToken ct) =>
        kind == MediaKind.Series
            ? tmdb.SearchTvAsync(query, year, language, ct)
            : tmdb.SearchMovieAsync(query, year, language, ct);

    public async Task<MediaCandidate?> GetDetailsAsync(MediaKind kind, int id, CancellationToken ct)
    {
        if (kind == MediaKind.Series)
            return await tmdb.GetTvAsync(id, LanguagePolicy.English, ct).ConfigureAwait(false) is { } tv ? MediaCandidate.From(tv) : null;
        return await tmdb.GetMovieAsync(id, LanguagePolicy.English, ct).ConfigureAwait(false) is { } movie ? MediaCandidate.From(movie) : null;
    }

    private const int ExactMatch = 100;

    private static int Score(MediaCandidate candidate, string query, int? year)
    {
        var q = Normalize(query);
        string[] titles = [Normalize(candidate.Title), Normalize(candidate.OriginalTitle)];

        var score = titles.Contains(q) ? ExactMatch
            : titles.Any(t => t.StartsWith(q, StringComparison.Ordinal) || q.StartsWith(t, StringComparison.Ordinal)) ? 40
            : 0;

        if (year is not null && candidate.Year is not null)
            score += candidate.Year == year ? 50 : Math.Abs(candidate.Year.Value - year.Value) <= 1 ? 20 : -30;

        // Desempate pela popularidade (até +10).
        return score + (int)Math.Min(10, Math.Log10(1 + candidate.Popularity) * 4);
    }

    /// <summary>Minúsculas, sem acentos e sem pontuação: "Shōgun" = "shogun", "Monster: X" = "monster x".</summary>
    internal static string Normalize(string text)
    {
        var decomposed = text.Replace("&", " and ", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return SpacesRegex().Replace(builder.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();
}
