using System.Text.Json.Serialization;

namespace AutoSkipper.Models;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ConfigData))]
internal partial class JsonContext : JsonSerializerContext
{
}
