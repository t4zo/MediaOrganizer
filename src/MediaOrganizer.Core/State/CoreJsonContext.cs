using System.Text.Json.Serialization;
using MediaOrganizer.Core.Execution;

namespace MediaOrganizer.Core.State;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ProcessedState))]
[JsonSerializable(typeof(RenameJournal))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;
