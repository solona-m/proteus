using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Proteus;

/// <summary>
/// Deserialises MaterialGamePath as either a JSON string or a JSON array of strings.
/// Serialises a single-element list back as a plain string for compact output.
/// </summary>
public class StringOrStringArrayConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return [reader.GetString()!];

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                if (reader.TokenType == JsonTokenType.String)
                    list.Add(reader.GetString()!);
            return list;
        }

        throw new JsonException($"Expected string or array for MaterialGamePath, got {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        if (value.Count == 1)
            writer.WriteStringValue(value[0]);
        else
        {
            writer.WriteStartArray();
            foreach (var s in value)
                writer.WriteStringValue(s);
            writer.WriteEndArray();
        }
    }
}
