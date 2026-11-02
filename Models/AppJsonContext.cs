using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LuckyLilliaDesktop.Models;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(Dictionary<string, JsonNode?>))]
public partial class AppJsonContext : JsonSerializerContext;
