namespace PlingBot.Models;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(CouponEventJsonConverter))]
public class CouponEvent
{
    public string Key { get; set; } = "";
    public string Type { get; set; } = "";
    public int FixtureId { get; set; }
    public string? Detail { get; set; }
    public int? TeamId { get; set; }
    public string Team { get; set; } = "";
    public int Elapsed { get; set; }
    public int Extra { get; set; }
    public string Score { get; set; } = "";
    public string Text { get; set; } = "";
    public int? PlayerId { get; set; }
    public string? Player { get; set; }
    public int? AssistId { get; set; }
    public string? Assist { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class CouponEventJsonConverter : JsonConverter<CouponEvent>
{
    public override CouponEvent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new CouponEvent
            {
                Type = "Legacy",
                Text = reader.GetString() ?? "",
                CreatedUtc = DateTime.UtcNow
            };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected coupon event object or legacy string.");

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        return new CouponEvent
        {
            Key = GetString(root, nameof(CouponEvent.Key)),
            Type = GetString(root, nameof(CouponEvent.Type)),
            FixtureId = GetInt(root, nameof(CouponEvent.FixtureId)),
            Detail = GetNullableString(root, nameof(CouponEvent.Detail)),
            TeamId = GetNullableInt(root, nameof(CouponEvent.TeamId)),
            Team = GetString(root, nameof(CouponEvent.Team)),
            Elapsed = GetInt(root, nameof(CouponEvent.Elapsed)),
            Extra = GetInt(root, nameof(CouponEvent.Extra)),
            Score = GetString(root, nameof(CouponEvent.Score)),
            Text = GetString(root, nameof(CouponEvent.Text)),
            PlayerId = GetNullableInt(root, nameof(CouponEvent.PlayerId)),
            Player = GetNullableString(root, nameof(CouponEvent.Player)),
            AssistId = GetNullableInt(root, nameof(CouponEvent.AssistId)),
            Assist = GetNullableString(root, nameof(CouponEvent.Assist)),
            CreatedUtc = GetDateTime(root, nameof(CouponEvent.CreatedUtc))
        };
    }

    public override void Write(Utf8JsonWriter writer, CouponEvent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(nameof(CouponEvent.Key), value.Key);
        writer.WriteString(nameof(CouponEvent.Type), value.Type);
        writer.WriteNumber(nameof(CouponEvent.FixtureId), value.FixtureId);
        WriteNullableString(writer, nameof(CouponEvent.Detail), value.Detail);
        WriteNullableNumber(writer, nameof(CouponEvent.TeamId), value.TeamId);
        writer.WriteString(nameof(CouponEvent.Team), value.Team);
        writer.WriteNumber(nameof(CouponEvent.Elapsed), value.Elapsed);
        writer.WriteNumber(nameof(CouponEvent.Extra), value.Extra);
        writer.WriteString(nameof(CouponEvent.Score), value.Score);
        writer.WriteString(nameof(CouponEvent.Text), value.Text);
        WriteNullableNumber(writer, nameof(CouponEvent.PlayerId), value.PlayerId);
        WriteNullableString(writer, nameof(CouponEvent.Player), value.Player);
        WriteNullableNumber(writer, nameof(CouponEvent.AssistId), value.AssistId);
        WriteNullableString(writer, nameof(CouponEvent.Assist), value.Assist);
        writer.WriteString(nameof(CouponEvent.CreatedUtc), value.CreatedUtc);
        writer.WriteEndObject();
    }

    private static string GetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static string? GetNullableString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int GetInt(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
    }

    private static int? GetNullableInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        return value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
    }

    private static DateTime GetDateTime(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTime(out var dateTime)
            ? dateTime
            : DateTime.UtcNow;
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value == null)
            writer.WriteNull(propertyName);
        else
            writer.WriteString(propertyName, value);
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string propertyName, int? value)
    {
        if (value.HasValue)
            writer.WriteNumber(propertyName, value.Value);
        else
            writer.WriteNull(propertyName);
    }
}
