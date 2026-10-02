using System.Text.Json.Serialization;

namespace MkPFS.Gui.ViewModels;

/// <summary>Exported log file (Python <c>_on_export_log</c> JSON shape).</summary>
/// <param name="Log">Log lines.</param>
public sealed record ExportedLog([property: JsonPropertyName("log")] IReadOnlyList<string> Log);

/// <summary>Source-generated JSON metadata for GUI files.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ExportedLog))]
internal sealed partial class GuiJsonContext : JsonSerializerContext;
