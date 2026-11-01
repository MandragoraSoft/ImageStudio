using System.Text.Json;
using ImageStudio.Services;

namespace ImageStudio.Tests;

// Budowa żądania wideo wg schematu modelu (JSON Schema z GET /v1/models/<id>)
public sealed class VideoRequestTests
{
    private static JsonElement Schema(string properties) =>
        JsonDocument.Parse("{\"type\":\"object\",\"properties\":" + properties + ",\"required\":[\"model\",\"prompt\"]}").RootElement.Clone();

    private static List<(byte[] Bytes, string Mime)> Pictures(int count) =>
        Enumerable.Range(0, count).Select(_ => (TestImages.Png(2, 2), "image/png")).ToList();

    private static Dictionary<string, object> Build(JsonElement? schema, int duration = 5, string ratio = "16:9", int pictures = 0)
    {
        var notes = new List<string>();
        var body = VideoRequest.Build(schema, "m/x", "prompt", duration, ratio, Pictures(pictures), notes);
        _notes = notes;
        return body;
    }

    private static List<string> _notes = [];

    [Fact]
    public void Without_a_schema_the_documented_field_names_are_sent()
    {
        var body = Build(null, 8, "9:16", pictures: 1);

        Assert.Equal(["model", "prompt", "duration_seconds", "aspect_ratio", "image_url"], body.Keys);
        Assert.Equal(8, body["duration_seconds"]);
        Assert.Equal("9:16", body["aspect_ratio"]);
        Assert.Empty(_notes);
    }

    [Fact]
    public void Fields_the_model_does_not_offer_are_left_out()
    {
        var body = Build(Schema("{\"model\":{},\"prompt\":{}}"), pictures: 1);

        Assert.Equal(["model", "prompt"], body.Keys);
        Assert.Contains(_notes, n => n.Contains("proporcji"));
        Assert.Contains(_notes, n => n.Contains("obrazów"));
    }

    [Theory]
    [InlineData(2, 5)]    // krótszy niż wszystkie: najkrótszy
    [InlineData(7, 5)]    // bliżej 5 niż 10
    [InlineData(8, 10)]
    [InlineData(10, 10)]
    [InlineData(12, 10)]  // remis 10/15 -> krótszy (tańszy)
    [InlineData(30, 15)]
    public void Duration_snaps_to_the_nearest_allowed_value(int wanted, int expected)
    {
        var body = Build(Schema("{\"duration_seconds\":{\"enum\":[5,10,15],\"type\":\"number\"}}"), wanted);

        Assert.Equal(expected, body["duration_seconds"]);
        Assert.Equal(wanted != expected, _notes.Any(n => n.Contains("5, 10, 15")));
    }

    [Fact]
    public void Duration_range_is_clamped()
    {
        var schema = Schema("{\"duration_seconds\":{\"minimum\":3,\"maximum\":12,\"type\":\"number\"}}");

        Assert.Equal(3, Build(schema, 1)["duration_seconds"]);
        Assert.Equal(7, Build(schema, 7)["duration_seconds"]);
        Assert.Equal(12, Build(schema, 40)["duration_seconds"]);
    }

    [Fact]
    public void Aspect_ratio_outside_the_offered_set_becomes_the_closest_offered_one()
    {
        var schema = Schema("{\"aspect_ratio\":{\"enum\":[\"1:1\",\"16:9\",\"9:16\"]}}");

        Assert.Equal("16:9", Build(schema, ratio: "16:9")["aspect_ratio"]);
        Assert.Equal("16:9", Build(schema, ratio: "3:2")["aspect_ratio"]);   // 1,5 bliżej 1,78 niż 1
        Assert.Equal("1:1", Build(schema, ratio: "4:3")["aspect_ratio"]);    // 1,33 bliżej 1 niż 1,78
        Assert.Equal("1:1", Build(schema, ratio: "5:4")["aspect_ratio"]);    // 1,25 bliżej 1
        Assert.Equal("9:16", Build(schema, ratio: "2:3")["aspect_ratio"]);
        Assert.Contains(_notes, n => n.Contains("proporcje"));
    }

    [Fact]
    public void Single_picture_is_the_first_frame_and_two_pictures_can_be_first_and_last()
    {
        var withEnd = Schema("{\"image_url\":{},\"end_image_url\":{}}");

        Assert.True(Build(withEnd, pictures: 1).ContainsKey("image_url"));
        var two = Build(withEnd, pictures: 2);
        Assert.True(two.ContainsKey("image_url"));
        Assert.True(two.ContainsKey("end_image_url"));
        Assert.False(two.ContainsKey("input_images"));
    }

    [Fact]
    public void Several_pictures_go_as_input_images_and_reference_models_get_objects_with_a_role()
    {
        var plain = Build(Schema("{\"image_url\":{},\"input_images\":{\"items\":{\"type\":\"string\"},\"type\":\"array\"}}"), pictures: 3);
        var uris = Assert.IsType<List<object>>(plain["input_images"]);
        Assert.Equal(3, uris.Count);
        Assert.All(uris, u => Assert.StartsWith("data:image/png;base64,", (string)u));

        var reference = Build(Schema("{\"input_images\":{\"items\":{\"type\":\"object\",\"properties\":{\"url\":{},\"role\":{}}},\"type\":\"array\"}}"), pictures: 2);
        var objects = Assert.IsType<List<object>>(reference["input_images"]);
        Assert.Equal(2, objects.Count);
        Assert.Equal("reference", objects[0].GetType().GetProperty("role")!.GetValue(objects[0]));
    }

    [Fact]
    public void Model_with_a_single_picture_slot_gets_only_the_first_of_several()
    {
        var body = Build(Schema("{\"image_url\":{}}"), pictures: 3);

        Assert.True(body.ContainsKey("image_url"));
        Assert.False(body.ContainsKey("input_images"));
        Assert.Contains(_notes, n => n.Contains("pierwszego"));
    }

    [Fact]
    public void Text_to_video_model_without_picture_slots_sends_no_picture_fields()
    {
        var body = Build(Schema("{\"duration_seconds\":{\"enum\":[5,10]},\"model\":{},\"prompt\":{}}"));

        Assert.Equal(["model", "prompt", "duration_seconds"], body.Keys);
    }
}
