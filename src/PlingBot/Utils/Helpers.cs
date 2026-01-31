using System;
using System.Text.Json;

namespace PlingBot.Utils;

public class Helpers
{
    private static int GetInt(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number) return el.GetInt32();
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int v)) return v;
        return 0;
    }
}