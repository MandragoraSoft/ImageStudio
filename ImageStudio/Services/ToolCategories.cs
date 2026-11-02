namespace ImageStudio.Services;

// Przeznaczenie narzędzi imagestudio__* (grupy w panelu Narzędzia): każdą grupę można włączyć albo wyłączyć osobno,
// np. wyłączyć cały głos, gdy nie jest potrzebny. Narzędzie spoza tabeli trafia do grupy "window"
public static class ToolCategories
{
    // Kolejność grup w panelu
    public static readonly IReadOnlyList<string> Order = ["image", "video", "voice", "prompts", "gallery", "models", "window"];

    private static readonly Dictionary<string, string> ByTool = new()
    {
        ["generate_image"] = "image", ["edit_image"] = "image", ["add_reference"] = "image", ["remove_reference"] = "image",
        ["clear_references"] = "image", ["set_size"] = "image", ["set_aspect"] = "image",

        ["generate_video"] = "video", ["set_video_options"] = "video", ["add_video_attachment"] = "video",
        ["clear_video_attachments"] = "video",

        ["list_audio_models"] = "voice", ["list_voices"] = "voice", ["speak"] = "voice", ["transcribe"] = "voice",

        ["set_prompt"] = "prompts", ["rebuild_prompt"] = "prompts", ["describe_images"] = "prompts",
        ["list_presets"] = "prompts", ["save_preset"] = "prompts",

        ["list_gallery"] = "gallery", ["repeat_gallery_entry"] = "gallery", ["remove_gallery_entry"] = "gallery",
        ["delete_gallery_entry"] = "gallery", ["clear_gallery"] = "gallery", ["export_gallery_entry"] = "gallery",

        ["list_models"] = "models", ["select_model"] = "models", ["get_defaults"] = "models",
        ["set_default_model"] = "models", ["reload_models"] = "models",

        ["get_state"] = "window", ["switch_tab"] = "window", ["generate"] = "window",
    };

    // Nazwa bez przedrostka (generate_image, nie imagestudio__generate_image)
    public static string For(string toolName) => ByTool.GetValueOrDefault(toolName, "window");
}
