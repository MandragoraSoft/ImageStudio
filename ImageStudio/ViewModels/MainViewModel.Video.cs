using ImageStudio.Localization;
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
    // --- Wideo ---

    public ObservableCollection<ModelOption> VideoModels { get; } = [];

    // Lista modeli wideo z tymi samymi filtrami co modele obrazów
    public ICollectionView VideoModelsView { get; }
    public IReadOnlyList<string> VideoAspects { get; } = ["16:9", "9:16", "1:1", "4:3"];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateVideoCommand))]
    [NotifyPropertyChangedFor(nameof(ActiveModel), nameof(VideoEstimateLabel), nameof(VideoHint))]
    private ModelOption? _selectedVideoModel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateVideoCommand))]
    private string _videoPrompt = "";

    // Sekundy wideo (parametr duration)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VideoEstimateLabel))]
    private int _videoDuration = 5;

    [ObservableProperty]
    private string _videoAspect = "16:9";

    // Załączniki do promptu wideo (obrazy wejściowe); wiele = pole input_images
    public ObservableCollection<ReferenceItem> VideoAttachments { get; } = [];

    public string VideoHint
    {
        get
        {
            if (VideoAttachments.Count == 0) return "";
            return SelectedVideoModel is { AcceptsImage: false }
                ? Loc.T("video.msg.noImages")
                : Loc.T("video.msg.attachmentsHint", VideoAttachments.Count);
        }
    }

    [RelayCommand(CanExecute = nameof(CanPickOrCopy))]
    private void AddVideoAttachments()
    {
        var paths = _dialogs.PickImageFiles();
        if (paths is null || paths.Length == 0) return;

        try
        {
            foreach (var path in paths)
                VideoAttachments.Add(new ReferenceItem(ImageOrientation.EnsureUpright(path), Path.GetFileName(path), path));
            Status = Loc.T("video.msg.attachments", VideoAttachments.Count);
        }
        catch (Exception ex)
        {
            Status = Loc.T("video.msg.attachmentFailed", ex.Message);
            Log.LogError("{Message}", Status);
        }
    }

    [RelayCommand]
    private void RemoveVideoAttachment(ReferenceItem? item)
    {
        if (item is not null) VideoAttachments.Remove(item);
    }

    [RelayCommand]
    private void ClearVideoAttachments() => VideoAttachments.Clear();

    [RelayCommand(CanExecute = nameof(CanPickOrCopy))]
    private void PasteVideoAttachment()
    {
        var path = PasteClipboardImage();
        if (path is null) return;

        VideoAttachments.Add(new ReferenceItem(path, $"Schowek {DateTime.Now:HH:mm:ss}", "schowek"));
        Status = Loc.T("video.msg.attachments", VideoAttachments.Count);
    }

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "image/png",
    };

    // Wynik: wideo zamiast obrazu w podglądzie
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowImage))]
    [NotifyCanExecuteChangedFor(nameof(SaveImageAsCommand), nameof(ClearResultCommand))]
    private bool _showVideo;

    [ObservableProperty]
    private Uri? _videoSource;

    public bool ShowImage => !ShowVideo;

    public string VideoEstimateLabel => SelectedVideoModel?.Pricing is { } p
        ? Loc.T("video.estimate", p.PriceNow * VideoDuration, VideoDuration, p.PriceNow)
        : "Brak ceny dla tego modelu";

    // Załączniki wymagają modelu, który przyjmuje obrazy
    private bool CanGenerateVideo => !IsBusy && !string.IsNullOrWhiteSpace(VideoPrompt)
                                     && SelectedVideoModel is not null
                                     && (VideoAttachments.Count == 0 || SelectedVideoModel.AcceptsImage);

    // Każde wywołanie tworzy płatne zadanie; postęp idzie do paska stanu
    [RelayCommand(CanExecute = nameof(CanGenerateVideo))]
    private async Task GenerateVideoAsync()
    {
        var model = SelectedVideoModel!;
        var estimate = model.Pricing?.PriceNow * VideoDuration;
        IsBusy = true;
        Status = Loc.T("video.msg.sending");
        try
        {
            var attachments = VideoAttachments
                .Select(a => (Bytes: File.ReadAllBytes(a.Path), Mime: MimeOf(a.Path)))
                .ToList();

            var result = await _provider.GenerateVideoAsync(model.Id, VideoPrompt, VideoDuration, VideoAspect,
                                                           attachments, s => Status = Loc.T("video.msg.progress", s));

            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ImageStudio");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
            await File.WriteAllBytesAsync(path, result.Bytes);

            ShowVideo = true;
            VideoSource = new Uri(path);
            AddToGallery(new GalleryEntry
            {
                Prompt = VideoPrompt,
                Model = model.Id,
                Kind = "video",
                Size = $"{VideoAspect} · {VideoDuration} s" + (attachments.Count > 0 ? Loc.T("video.sizeAttachments", attachments.Count) : ""),
                FilePath = path,
                Thumbnail = GalleryStore.Thumbnail(path),
                CostUsd = result.CostUsd ?? estimate,
                CostEstimated = result.CostUsd is null && estimate is not null,
            });
            Status = Loc.T("video.msg.saved", path);
        }
        catch (Exception ex)
        {
            Status = Loc.T("video.msg.error", ex.Message);
            Log.LogError("{Message}", Status);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
