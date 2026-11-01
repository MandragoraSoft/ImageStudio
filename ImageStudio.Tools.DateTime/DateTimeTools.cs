using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using static ImageStudio.Services.ToolArgs;

namespace ImageStudio.Services;

// Grupa "datetime": aktualna data i godzina (model nie zna dzisiejszej daty)
public sealed class DateTimeTools(TimeProvider? time = null) : IChatToolGroup
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Id => "datetime";

    public IReadOnlyList<ChatToolInfo> Tools { get; } =
    [
        new("get_datetime",
            "Zwraca aktualną datę, godzinę i dzień tygodnia (czas lokalny użytkownika i UTC). Używaj zawsze, gdy odpowiedź zależy od dzisiejszej daty.",
            Schema(("timezone", """{"type":"string","description":"Opcjonalna strefa czasowa (np. Europe/Warsaw albo Central European Standard Time); domyślnie lokalna"}""", false))),
    ];

    public string Call(string name, JsonElement arguments) =>
        name == "get_datetime" ? GetDateTime(arguments) : throw new ArgumentException($"Nieznane narzędzie: {name}");

    private string GetDateTime(JsonElement args)
    {
        TimeZoneInfo zone;
        try
        {
            zone = OptString(args, "timezone") is { Length: > 0 } id ? TimeZoneInfo.FindSystemTimeZoneById(id) : TimeZoneInfo.Local;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException("Nieznana strefa czasowa.");
        }

        var utc = _time.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        return JsonSerializer.Serialize(new
        {
            local = local.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            date = local.ToString("yyyy-MM-dd"),
            time = local.ToString("HH:mm:ss"),
            dayOfWeek = local.DayOfWeek.ToString(),
            timezone = zone.Id,
            utc = utc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        }, Json);
    }
}
