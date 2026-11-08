using System.Text;
using System.Text.Json;

namespace WinSight.Infrastructure.Media;

/// <summary>Text recovered from a legacy binary Office file by the isolated host (<c>extract-legacy-text</c>).</summary>
public sealed record NativeLegacyOfficeText(string State, string? Detail, string Format, string Text);

internal static class NativeLegacyOfficeTextProtocol
{
    /// <summary>The reply is one JSON line; text is bounded by the host to 4 million characters.</summary>
    internal const int MaxOutputBytes = 16 * 1024 * 1024;
    internal const int MaxTextCharacters = 4_000_000;

    internal static NativeLegacyOfficeText Parse(string output, string extension)
    {
        try
        {
            using var document = JsonDocument.Parse(output.Trim(), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Legacy Office reply must be an object.");
            var members = new HashSet<string>(StringComparer.Ordinal) { "protocol", "state", "detail", "format", "text" };
            foreach (var property in root.EnumerateObject())
                if (!members.Remove(property.Name)) throw new InvalidDataException("Unknown or duplicate legacy Office reply member: " + property.Name);
            if (members.Count != 0) throw new InvalidDataException("Legacy Office reply is incomplete.");
            if (root.GetProperty("protocol").GetInt32() != 1) throw new InvalidDataException("Unsupported legacy Office reply protocol.");
            var state = root.GetProperty("state").GetString();
            var detailElement = root.GetProperty("detail");
            var detail = detailElement.ValueKind == JsonValueKind.Null ? null : detailElement.GetString();
            var format = root.GetProperty("format").GetString();
            var text = root.GetProperty("text").GetString();
            if (state is not ("done" or "partial" or "skipped") || text is null || format is null || text.Length > MaxTextCharacters
                || detail is { Length: > 4096 } || !string.Equals(format, extension.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
                || (state == "skipped" && text.Length != 0) || (state != "skipped" && string.IsNullOrWhiteSpace(text))
                || (state != "done" && string.IsNullOrWhiteSpace(detail)))
                throw new InvalidDataException("Legacy Office reply violates its completion contract.");
            return new NativeLegacyOfficeText(state, detail, format, text);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InvalidDataException("Malformed legacy Office reply.", error);
        }
    }
}
