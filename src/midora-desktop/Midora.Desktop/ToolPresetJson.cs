using System.IO;
using System.Text.Json;

namespace Midora.Desktop;

internal static class ToolPresetJson
{
    internal const int MaximumBytes = 262_144;
    internal static T Read<T>(string path, JsonSerializerOptions options)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("The preset exceeds its file size limit.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        CheckDuplicates(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, options) ?? throw new InvalidDataException("The preset is empty.");
    }

    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        { foreach (var value in element.EnumerateArray()) CheckDuplicates(value); return; }
        if (element.ValueKind != JsonValueKind.Object) return;
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new InvalidDataException($"Duplicate preset property '{property.Name}'.");
            CheckDuplicates(property.Value);
        }
    }
}
