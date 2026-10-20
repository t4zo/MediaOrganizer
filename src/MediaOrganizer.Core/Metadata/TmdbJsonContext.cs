using System.Text.Json.Serialization;

namespace MediaOrganizer.Core.Metadata;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TmdbPage<TvSearchResult>))]
[JsonSerializable(typeof(TmdbPage<MovieSearchResult>))]
[JsonSerializable(typeof(TvDetails))]
[JsonSerializable(typeof(TvSeason))]
[JsonSerializable(typeof(MovieDetails))]
internal sealed partial class TmdbJsonContext : JsonSerializerContext;
