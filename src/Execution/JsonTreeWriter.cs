using System.Collections;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Builder.Execution;

/// <summary>
/// Writes a tree of dictionaries, lists, strings, numbers, booleans, nulls and <see cref="JsonElement"/>s as JSON.
/// The language server and <c>check --json</c> build their answers as such trees. <c>JsonSerializer</c> reads the
/// runtime types of an <c>object</c> by reflection, which a NativeAOT build does not carry, so this walks the tree
/// itself. A value of another type is a bug in the caller and fails loudly with its type name.
/// </summary>
internal static class JsonTreeWriter
{
    /// <summary>The tree as UTF-8 JSON, on one line (the language server's framing) or indented with the characters
    /// the file shows as they are (<c>check --json</c>).</summary>
    internal static byte[] ToUtf8(object? tree, bool indented)
    {
        var buffer = new MemoryStream();
        var options = new JsonWriterOptions { Indented = indented };
        if (indented)
        {
            options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        }

        using (var writer = new Utf8JsonWriter(utf8Json: buffer, options: options))
        {
            Write(writer: writer, value: tree);
        }

        return buffer.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(value: text);
                break;
            case bool flag:
                writer.WriteBooleanValue(value: flag);
                break;
            case int i:
                writer.WriteNumberValue(value: i);
                break;
            case long l:
                writer.WriteNumberValue(value: l);
                break;
            case uint u:
                writer.WriteNumberValue(value: u);
                break;
            case ulong ul:
                writer.WriteNumberValue(value: ul);
                break;
            case short s:
                writer.WriteNumberValue(value: s);
                break;
            case byte b:
                writer.WriteNumberValue(value: b);
                break;
            case double d:
                writer.WriteNumberValue(value: d);
                break;
            case float f:
                writer.WriteNumberValue(value: f);
                break;
            case decimal m:
                writer.WriteNumberValue(value: m);
                break;
            case JsonElement element:
                element.WriteTo(writer: writer);
                break;
            case IDictionary map:
                writer.WriteStartObject();
                foreach (DictionaryEntry pair in map)
                {
                    writer.WritePropertyName(propertyName: (string)pair.Key);
                    Write(writer: writer, value: pair.Value);
                }

                writer.WriteEndObject();
                break;
            case IEnumerable items:
                writer.WriteStartArray();
                foreach (object? item in items)
                {
                    Write(writer: writer, value: item);
                }

                writer.WriteEndArray();
                break;
            default:
                throw new InvalidOperationException(
                    message: $"A JSON answer holds a {value.GetType().FullName}, which the JSON writer does not know.");
        }
    }
}
