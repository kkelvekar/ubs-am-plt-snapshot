using System.Text.Json;

namespace Ubs.Advantage.Core.Messaging.Kafka;

/// <summary>
/// Wire format for message values: JSON, PascalCase on the way out, case-insensitive on the way
/// in so a producer sending camelCase still binds.
/// </summary>
internal static class MessageSerialization
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static string Serialize<TValue>(TValue? value)
        => JsonSerializer.Serialize(value, Options);

    public static TValue? Deserialize<TValue>(string? text)
        => string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<TValue>(text, Options);

    /// <summary>Keys travel as text; anything else is carried as its default.</summary>
    public static TKey ToKey<TKey>(string? key)
        => key is TKey typed ? typed : default!;

    public static string FromKey<TKey>(TKey key)
        => key?.ToString() ?? string.Empty;
}
