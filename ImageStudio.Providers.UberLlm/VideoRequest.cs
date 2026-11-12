using System.Globalization;
using System.Text.Json;

namespace ImageStudio.Services;

// Żądanie wideo UberLLM zbudowane wg schematu modelu (GET /v1/models/<id> -> parameters, JSON Schema).
// Każdy model oferuje inne pola: modele wan nie mają aspect_ratio, czasy to np. tylko 5/10/15 s, obraz idzie jako image_url
// (pierwsza klatka) albo input_images (referencje). Bez schematu (np. brak sieci) stosujemy nazwy z dokumentacji
public static class VideoRequest
{
    // schema = obiekt parameters z opisu modelu albo null; notes dostaje zmiany względem tego, o co poprosił użytkownik
    public static Dictionary<string, object> Build(JsonElement? schema, string model, string prompt, int duration, string aspectRatio,
                                                   IReadOnlyList<(byte[] Bytes, string Mime)> attachments, List<string> notes,
                                                   string? negativePrompt = null, Action<NegativePromptOutcome>? negativeNotice = null)
    {
        JsonElement? properties = schema is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("properties", out var p) ? p : null;
        bool Has(string name) => properties is null || properties.Value.TryGetProperty(name, out _);
        JsonElement? Property(string name) =>
            properties is { } all && all.TryGetProperty(name, out var value) ? value : null;

        var body = new Dictionary<string, object> { ["model"] = model, ["prompt"] = prompt };

        if (Has("duration_seconds"))
        {
            var allowed = Property("duration_seconds") is { } d ? DurationChoices(d) : null;
            var chosen = allowed is { Count: > 0 } ? Nearest(allowed, duration) : duration;
            body["duration_seconds"] = chosen;
            if (chosen != duration) notes.Add($"model oferuje czasy {string.Join(", ", allowed!)} s: ustawiono {chosen} s");
        }

        if (Has("aspect_ratio"))
        {
            var allowed = Property("aspect_ratio") is { } a ? Strings(a, "enum") : [];
            var chosen = allowed.Count == 0 || allowed.Contains(aspectRatio) ? aspectRatio : ClosestRatio(allowed, aspectRatio);
            body["aspect_ratio"] = chosen;
            if (chosen != aspectRatio) notes.Add($"model oferuje proporcje {string.Join(", ", allowed)}: ustawiono {chosen}");
        }
        else
        {
            notes.Add("model nie ma wyboru proporcji: wynikają z obrazu");
        }

        // negative_prompt tylko natywnie i tylko gdy schemat modelu go potwierdza; bez potwierdzenia nie idzie nigdzie (też do promptu)
        if (!string.IsNullOrWhiteSpace(negativePrompt))
        {
            if (NegativePromptBlocker(schema) is { } blocker) negativeNotice?.Invoke(blocker);
            else body["negative_prompt"] = negativePrompt.Trim();
        }

        var uris = attachments.Select(a => $"data:{a.Mime};base64,{Convert.ToBase64String(a.Bytes)}").ToList();
        if (uris.Count > 0) AddPictures(body, uris, Has, Property, notes);
        return body;
    }

    // null = schemat potwierdza parametr negative_prompt; inaczej powód, dla którego go nie wysyłamy
    public static NegativePromptOutcome? NegativePromptBlocker(JsonElement? schema) =>
        schema is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? properties.TryGetProperty("negative_prompt", out _) ? null : NegativePromptOutcome.Unsupported
            : NegativePromptOutcome.Unconfirmed;

    private static void AddPictures(Dictionary<string, object> body, List<string> uris, Func<string, bool> has,
                                    Func<string, JsonElement?> property, List<string> notes)
    {
        // Modele referencyjne przyjmują obrazy jako obiekty {url, role}, pozostałe jako same adresy
        var referenceObjects = property("input_images") is { } items && items.TryGetProperty("items", out var itemSchema)
                               && itemSchema.TryGetProperty("type", out var type) && type.GetString() == "object";
        object Reference(string uri) => referenceObjects ? new { url = uri, role = "reference" } : uri;

        if (uris.Count == 1 && has("image_url"))
        {
            body["image_url"] = uris[0];
        }
        else if (uris.Count == 2 && has("image_url") && has("end_image_url") && !has("input_images"))
        {
            body["image_url"] = uris[0];  // pierwsza i ostatnia klatka
            body["end_image_url"] = uris[1];
        }
        else if (has("input_images"))
        {
            body["input_images"] = uris.Select(Reference).ToList();
        }
        else if (has("image_url"))
        {
            body["image_url"] = uris[0];
            notes.Add("model przyjmuje jeden obraz: użyto pierwszego");
        }
        else
        {
            notes.Add("model nie przyjmuje obrazów: pominięto załączniki");
        }
    }

    // Dozwolone czasy: enum albo zakres minimum..maksimum (liczby całkowite)
    private static List<int>? DurationChoices(JsonElement schema)
    {
        if (schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
            return values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Select(v => (int)Math.Round(v.GetDouble())).Order().ToList();

        if (schema.TryGetProperty("minimum", out var min) && schema.TryGetProperty("maximum", out var max)
            && min.ValueKind == JsonValueKind.Number && max.ValueKind == JsonValueKind.Number)
            return Enumerable.Range((int)Math.Ceiling(min.GetDouble()), Math.Max(1, (int)Math.Floor(max.GetDouble()) - (int)Math.Ceiling(min.GetDouble()) + 1)).ToList();

        return null;
    }

    // Najbliższa dozwolona wartość; przy remisie krótsza (tańsza)
    private static int Nearest(List<int> allowed, int wanted) =>
        allowed.OrderBy(a => Math.Abs(a - wanted)).ThenBy(a => a).First();

    // Proporcje "16:9" porównujemy jako liczby (szerokość/wysokość)
    private static string ClosestRatio(List<string> allowed, string wanted)
    {
        var target = Ratio(wanted);
        return allowed.OrderBy(a => Math.Abs(Ratio(a) - target)).First();
    }

    private static double Ratio(string text)
    {
        var parts = text.Split(':');
        return parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var w)
                                 && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var h) && h > 0
            ? w / h
            : 1.0;
    }

    private static List<string> Strings(JsonElement schema, string name) =>
        schema.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(v => v.GetString()).OfType<string>().ToList()
            : [];
}
