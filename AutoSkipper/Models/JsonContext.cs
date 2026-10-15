using System.Text.Json.Serialization;

namespace AutoSkipper.Models;

// Enums are written as names so reordering MacroActionType can't silently change
// saved macros. Integer values from older config files are still accepted on load.
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ConfigData))]
internal partial class JsonContext : JsonSerializerContext
{
}
