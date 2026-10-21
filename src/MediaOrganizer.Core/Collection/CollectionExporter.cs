using System.Globalization;
using System.Text;

namespace MediaOrganizer.Core.Collection;

/// <summary>Exporta a lista de episódios faltando (só temporadas regulares).</summary>
public static class CollectionExporter
{
    public const string CsvHeader = "Série;Temporada;Episódio;Título;Exibido em";

    /// <summary>CSV separado por ";" (padrão do Excel em português). Gravar em UTF-8 com BOM.</summary>
    public static string ToCsv(IEnumerable<SeriesReport> reports)
    {
        var builder = new StringBuilder().AppendLine(CsvHeader);
        foreach (var (report, episode) in Missing(reports))
        {
            builder.AppendJoin(';',
                Escape(SeriesName(report)),
                episode.Season.ToString(CultureInfo.InvariantCulture),
                episode.Episode.ToString(CultureInfo.InvariantCulture),
                Escape(episode.Title ?? ""),
                episode.AirDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "").AppendLine();
        }
        return builder.ToString();
    }

    /// <summary>Texto legível, agrupado por série.</summary>
    public static string ToText(IEnumerable<SeriesReport> reports)
    {
        var builder = new StringBuilder();
        foreach (var group in Missing(reports).GroupBy(m => m.Report))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"{SeriesName(group.Key)} — faltam {group.Count()} episódio(s)");
            foreach (var (_, episode) in group)
            {
                var date = episode.AirDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                builder.AppendLine(CultureInfo.InvariantCulture, $"  {episode.Code}  {episode.Title}{(date is null ? "" : $"  ({date})")}");
            }
            builder.AppendLine();
        }
        return builder.Length == 0 ? "Nenhum episódio faltando." + Environment.NewLine : builder.ToString();
    }

    private static IEnumerable<(SeriesReport Report, EpisodeRef Episode)> Missing(IEnumerable<SeriesReport> reports) =>
        reports
            .OrderBy(r => SeriesName(r), StringComparer.CurrentCultureIgnoreCase)
            .SelectMany(r => r.RegularSeasons.SelectMany(s => s.Missing).Select(e => (r, e)));

    private static string SeriesName(SeriesReport report) => report.Show?.PreferredTitle ?? report.Folder.Name;

    private static string Escape(string value) =>
        value.IndexOfAny([';', '"', '\n', '\r']) < 0 ? value : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
