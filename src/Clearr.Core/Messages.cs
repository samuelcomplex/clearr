using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clearr.Core;

/// <summary>
/// A translatable message: a key into the UI's language files (ui/lang-*.json) plus its values.
/// Values can be text, numbers, another <see cref="Msg"/> (translated too) or a list of <see cref="PersonArg"/>.
/// The UI renders it in the user's language; the server renders English for logs, History text and old clients.
/// </summary>
[JsonConverter(typeof(MsgJsonConverter))]
public sealed class Msg
{
    public Msg(string code, Dictionary<string, object?>? args = null)
    {
        Code = code;
        Args = args ?? new();
    }

    public string Code { get; }

    public Dictionary<string, object?> Args { get; }

    /// <summary>Plain text (an error from Sonarr, say) shown as it is.</summary>
    public static Msg Text(string text) => new("text", new() { ["text"] = text });

    public override string ToString() => Messages.English(this);
}

/// <summary>One viewer in a "Waiting on …" reason.</summary>
public sealed record PersonArg(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("watched")] int Watched,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("flags")] IReadOnlyList<string> Flags);

public static class Messages
{
    private static readonly Lazy<Dictionary<string, string>> En = new(() =>
    {
        using var s = ClearrUi.Open("lang-en.json") ?? throw new InvalidOperationException("lang-en.json missing");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
    });

    /// <summary>The English text for a message (same templates the UI uses).</summary>
    public static string English(Msg m) => Render(m.Code, m.Args);

    public static string English(IEnumerable<Msg> parts) => string.Join(" · ", parts.Select(English));

    private static string Render(string code, Dictionary<string, object?> args)
    {
        var template = En.Value.TryGetValue(code, out var t) ? t : code;
        foreach (var (key, value) in args)
        {
            template = template.Replace("{" + key + "}", Value(value), StringComparison.Ordinal);
        }

        return template;
    }

    private static string Value(object? v) => v switch
    {
        null => "",
        Msg m => English(m),
        IEnumerable<PersonArg> people => string.Join(", ", people.Select(p => Render("reason.person", new()
        {
            ["name"] = p.Name,
            ["watched"] = p.Watched,
            ["total"] = p.Total,
            ["flags"] = p.Flags.Count == 0 ? "" : ", " + string.Join(", ", p.Flags.Select(f => Render("flagInline." + f, new()))),
        }))),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };
}

/// <summary>Serializes a <see cref="Msg"/> as { "code": …, "args": { … } } for the UI.</summary>
public sealed class MsgJsonConverter : JsonConverter<Msg>
{
    public override Msg Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() ?? "text" : "text";
        var args = new Dictionary<string, object?>();
        if (doc.RootElement.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in a.EnumerateObject())
            {
                args[p.Name] = ReadValue(p.Value);
            }
        }

        return new Msg(code, args);
    }

    public override void Write(Utf8JsonWriter writer, Msg value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("code", value.Code);
        writer.WritePropertyName("args");
        writer.WriteStartObject();
        foreach (var (k, v) in value.Args)
        {
            writer.WritePropertyName(k);
            JsonSerializer.Serialize(writer, v, v?.GetType() ?? typeof(object), options);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    // History is stored as JSON: read nested messages and people lists back into their types.
    private static object? ReadValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Object when e.TryGetProperty("code", out _) => JsonSerializer.Deserialize<Msg>(e.GetRawText(), Options),
        JsonValueKind.Array => e.EnumerateArray().Select(p => new PersonArg(
            p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            p.TryGetProperty("watched", out var w) ? w.GetInt32() : 0,
            p.TryGetProperty("total", out var t) ? t.GetInt32() : 0,
            p.TryGetProperty("flags", out var f) ? f.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>())).ToList(),
        _ => null,
    };

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
