using System.Text.Json;

namespace Midora.Application;

internal static class StrictApplicationJson
{
    public static void RejectDuplicateProperties(ReadOnlySpan<byte> json)
    {
        Utf8JsonReader reader = new(
            json,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        Stack<HashSet<string>> objectProperties = new();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectProperties.Push(new(StringComparer.Ordinal));
                    break;
                case JsonTokenType.PropertyName:
                    string name = reader.GetString()
                        ?? throw new InvalidDataException("A JSON property name is null.");
                    if (objectProperties.Count == 0 || !objectProperties.Peek().Add(name))
                    {
                        throw new InvalidDataException($"Duplicate JSON property '{name}'.");
                    }
                    break;
                case JsonTokenType.EndObject:
                    if (objectProperties.Count == 0)
                    {
                        throw new InvalidDataException("The JSON object structure is unbalanced.");
                    }
                    _ = objectProperties.Pop();
                    break;
            }
        }
        if (objectProperties.Count != 0)
        {
            throw new InvalidDataException("The JSON object structure is incomplete.");
        }
    }
}
