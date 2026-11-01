using System.IO;
using System.Text.Json;

namespace ImageStudio.Configuration;

// Import starej historii promptów (prompts.json) jako zestawów w nowym formacie.
// Każdy prompt staje się zestawem; istniejące zestawy o tym samym typie i treści są pomijane.
public sealed class LegacyPromptImporter(IPresetStore presets)
{
    private sealed class LegacyHistory
    {
        public List<string>? Generate { get; set; }
        public List<string>? Edit { get; set; }
    }

    public readonly record struct Result(bool FileFound, int Imported, int Skipped);

    public Result Import()
    {
        var path = Path.Combine(AppPaths.UserDataDir, "prompts.json");
        if (!File.Exists(path)) return new Result(false, 0, 0);

        LegacyHistory? legacy;
        try
        {
            legacy = JsonSerializer.Deserialize<LegacyHistory>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return new Result(true, 0, 0);
        }

        var existing = presets.LoadAll().ToList();
        var imported = 0;
        var skipped = 0;

        // Najstarsze na końcu listy historii, więc importujemy od końca, żeby najnowsze były najwyżej
        Import(legacy?.Edit, "edit", existing, ref imported, ref skipped);
        Import(legacy?.Generate, "generate", existing, ref imported, ref skipped);

        return new Result(true, imported, skipped);
    }

    private void Import(List<string>? prompts, string kind, List<Preset> existing, ref int imported, ref int skipped)
    {
        if (prompts is null) return;

        foreach (var raw in prompts.AsEnumerable().Reverse())
        {
            var prompt = raw?.Trim();
            if (string.IsNullOrWhiteSpace(prompt)) continue;

            if (existing.Any(p => p.Kind == kind && string.Equals(p.Prompt, prompt, StringComparison.Ordinal)))
            {
                skipped++;
                continue;
            }

            var preset = new Preset
            {
                Name = prompt.Length > 40 ? prompt[..40] + "..." : prompt,
                Description = "Zaimportowano ze starej historii promptów.",
                Kind = kind,
                Prompt = prompt,
            };
            presets.Create(preset);
            existing.Add(preset);
            imported++;
        }
    }
}
