using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Configuration;
using ImageStudio.Services;
using Microsoft.Extensions.Logging;

namespace ImageStudio.ViewModels;

public sealed partial class MainViewModel
{
    private static void Replace(ObservableCollection<ModelOption> target, IEnumerable<ImageModelInfo> infos,
                                IReadOnlyDictionary<string, ModelPricing> pricing)
    {
        target.Clear();
        foreach (var info in infos.OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase))
            target.Add(new ModelOption(info.Id, info.OwnedBy, info.Nsfw, info.InputModalities,
                                       pricing.GetValueOrDefault(info.Id), info.Description,
                                       info.OutputModalities));
    }

    // Lista dostawców modeli do filtra: właściciele ze wszystkich list (obrazy, edycja, wideo, czat)
    private void RefreshOwners()
    {
        var owners = Models.Concat(EditModels).Concat(VideoModels).Concat(ChatModelOptions).Concat(Voice.SpeechModels).Concat(Voice.TranscriptionModels)
            .Select(m => m.OwnedBy).OfType<string>().Distinct().OrderBy(o => o, StringComparer.OrdinalIgnoreCase).ToList();
        Providers.Clear();
        Providers.Add(AllProviders);
        foreach (var owner in owners) Providers.Add(owner);
    }

    // Modele czatu jako pozycje z opisem: możliwości z katalogu (obraz na wejściu, rozumowanie), właściciel z katalogu lub z id
    private void ReplaceChatOptions(IEnumerable<ChatModelInfo> chat, IReadOnlyDictionary<string, ModelPricing> pricing)
    {
        ChatModelOptions.Clear();
        foreach (var info in chat.OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase))
        {
            var input = info.InputModalities ?? (info.AcceptsImage == true ? ["text", "image"] : ["text"]);
            ChatModelOptions.Add(new ModelOption(info.Id, info.OwnedBy ?? OwnerFromId(info.Id), false, input, pricing.GetValueOrDefault(info.Id), info.Description,
                                                 ["text"], Reasoning: ReasoningSupport.ParameterFor(info.SupportedParameters) is not null));
        }
    }

    private static string? OwnerFromId(string id) => id.Contains('/') ? id.Split('/')[0] : null;

    private static void ReplaceStrings(ObservableCollection<string> target, IEnumerable<string> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    // Automatyczny zapis do Obrazy\ImageStudio (kopia bezpieczeństwa; "Zapisz jako" zapisuje w wybranym miejscu)
    private static string SaveImage(byte[] bytes)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ImageStudio");
        Directory.CreateDirectory(dir);

        var ext = bytes is [0xFF, 0xD8, ..] ? ".jpg" : ".png";
        var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static BitmapImage LoadImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // zwalnia strumień po załadowaniu
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
