using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Services;

namespace ImageStudio.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxSide = 1536;
    public const string AllProviders = "Wszyscy";

    private readonly ImageGenerationService _service;
    private readonly MarketService _market;
    private readonly IDialogService _dialogs;

    // Surowe bajty wyniku: zapis zachowuje oryginalny format (PNG/JPEG), bez ponownego kodowania
    private byte[]? _imageBytes;

    // Proporcja W/H używana przy blokadzie; _syncing chroni przed rekurencją przy wzajemnym przeliczaniu W i H
    private double _aspect = 1.0;
    private bool _syncing;

    // --- Generowanie ---
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private string _generatePrompt = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyPropertyChangedFor(nameof(ActiveModel))]
    private ModelOption? _selectedModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Size))]
    private int _width = 1024;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Size))]
    private int _height = 1024;

    [ObservableProperty]
    private bool _keepAspect = true;

    // --- Edycja ---
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    private string _editPrompt = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    [NotifyPropertyChangedFor(nameof(ActiveModel))]
    private ModelOption? _selectedEditModel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    private string? _sourceImagePath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplySourceAspectCommand))]
    private BitmapSource? _sourceImage;

    // Indeks zakładki w panelu wejściowym: 0 = generowanie, 1 = edycja
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveModel))]
    private int _activeTab;

    // --- Wynik i stan ---
    [ObservableProperty]
    private string _status = "Gotowy";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(EditCommand), nameof(PickSourceCommand),
                                nameof(CopyImageCommand), nameof(SaveImageAsCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyImageCommand), nameof(SaveImageAsCommand))]
    private BitmapSource? _image;

    // Skala podglądu wyniku (tylko wyświetlanie, nie zmienia pliku)
    [ObservableProperty]
    private double _zoom = 1.0;

    // --- Filtry modeli (wspólne dla obu zakładek) ---
    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _selectedProvider = AllProviders;

    [ObservableProperty]
    private bool _inputImageOnly;

    [ObservableProperty]
    private bool _hideNsfw;

    public MainViewModel(ImageGenerationService service, MarketService market, IDialogService dialogs)
    {
        _service = service;
        _market = market;
        _dialogs = dialogs;

        ModelsView = CollectionViewSource.GetDefaultView(Models);
        EditModelsView = CollectionViewSource.GetDefaultView(EditModels);
        ModelsView.Filter = FilterModel;
        EditModelsView.Filter = FilterModel;
    }

    // Listy z /v1/models wraz z cenami; widoki stosują filtry
    public ObservableCollection<ModelOption> Models { get; } = [];
    public ObservableCollection<ModelOption> EditModels { get; } = [];
    public ICollectionView ModelsView { get; }
    public ICollectionView EditModelsView { get; }
    public ObservableCollection<string> Providers { get; } = [];

    partial void OnSearchTextChanged(string value) => RefreshFilters();
    partial void OnSelectedProviderChanged(string value) => RefreshFilters();
    partial void OnInputImageOnlyChanged(bool value) => RefreshFilters();
    partial void OnHideNsfwChanged(bool value) => RefreshFilters();

    private void RefreshFilters()
    {
        ModelsView.Refresh();
        EditModelsView.Refresh();
    }

    private bool FilterModel(object item)
    {
        if (item is not ModelOption m) return false;
        if (HideNsfw && m.Nsfw) return false;
        if (InputImageOnly && !m.InputModalities.Contains("image")) return false;
        if (SelectedProvider != AllProviders && m.OwnedBy != SelectedProvider) return false;
        return string.IsNullOrWhiteSpace(SearchText)
               || m.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    // Model pokazywany w metryczce: zależy od aktywnej zakładki
    public ModelOption? ActiveModel => ActiveTab == 0 ? SelectedModel : SelectedEditModel;

    public string Size => $"{Width}x{Height}";

    private bool CanGenerate => !IsBusy && !string.IsNullOrWhiteSpace(GeneratePrompt) && SelectedModel is not null;
    private bool CanEdit => !IsBusy && !string.IsNullOrWhiteSpace(EditPrompt)
                            && SourceImagePath is not null && SelectedEditModel is not null;
    private bool HasImage => Image is not null;
    private bool HasSource => SourceImage is not null;

    public async Task InitializeAsync()
    {
        Status = "Pobieranie listy modeli i cen...";
        try
        {
            var models = await _service.GetImageModelsAsync();
            var pricing = await LoadPricingAsync();

            Replace(Models, models.Generation, pricing);
            Replace(EditModels, models.Edit, pricing);
            FillProviders(models.Generation.Concat(models.Edit));
            SelectedModel = Models.FirstOrDefault(m => m.Id == "venice/z-image-turbo") ?? Models.FirstOrDefault();
            SelectedEditModel = EditModels.FirstOrDefault(m => m.Id == "venice/grok-imagine-edit") ?? EditModels.FirstOrDefault();

            Status = $"Modele: {Models.Count} generowania, {EditModels.Count} edycji";
        }
        catch (Exception ex)
        {
            Status = $"Nie udało się pobrać modeli: {ex.Message}";
        }
    }

    // Brak cen nie blokuje listy modeli, tylko zostawia etykiety bez ceny
    private async Task<IReadOnlyDictionary<string, ModelPricing>> LoadPricingAsync()
    {
        try
        {
            return await _market.GetMediaPricingAsync();
        }
        catch (Exception ex)
        {
            Status = $"Bez cen z rynku: {ex.Message}";
            return new Dictionary<string, ModelPricing>();
        }
    }

    // --- Proporcje wyjścia ---

    partial void OnWidthChanged(int value) => OnDimensionChanged(widthChanged: true);
    partial void OnHeightChanged(int value) => OnDimensionChanged(widthChanged: false);

    private void OnDimensionChanged(bool widthChanged)
    {
        if (_syncing) return;

        _syncing = true;
        try
        {
            if (KeepAspect)
            {
                if (widthChanged) Height = Snap(Width / _aspect);
                else Width = Snap(Height * _aspect);
            }
            else if (Height > 0)
            {
                _aspect = (double)Width / Height;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    [RelayCommand]
    private void SetAspect(string? ratio)
    {
        var parts = ratio?.Split(':');
        if (parts is not { Length: 2 }
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
            || h <= 0)
            return;

        _aspect = w / h;
        _syncing = true;
        try
        {
            Height = Snap(Width / _aspect);
        }
        finally
        {
            _syncing = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSource))]
    private void ApplySourceAspect()
    {
        if (SourceImage is null) return;
        ApplyAspectFrom(SourceImage.PixelWidth, SourceImage.PixelHeight);
    }

    // Wymiary wyjścia biorą proporcje z wczytanego obrazu, z limitem długiego boku
    private void ApplyAspectFrom(int pixelWidth, int pixelHeight)
    {
        var scale = Math.Min(1.0, MaxSide / (double)Math.Max(pixelWidth, pixelHeight));
        _syncing = true;
        try
        {
            Width = Snap(pixelWidth * scale);
            Height = Snap(pixelHeight * scale);
        }
        finally
        {
            _syncing = false;
        }
        _aspect = (double)pixelWidth / pixelHeight;
    }

    // Wielokrotność 8, minimum 8
    private static int Snap(double value) => Math.Max(8, (int)Math.Round(value / 8.0) * 8);

    // --- Komendy ---

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private Task GenerateAsync() => RunAsync(() => _service.GenerateAsync(SelectedModel!.Id, GeneratePrompt, Size));

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task EditAsync() => RunAsync(() => _service.EditAsync(SelectedEditModel!.Id, EditPrompt, SourceImagePath!));

    [RelayCommand(CanExecute = nameof(CanPickOrCopy))]
    private void PickSource()
    {
        var path = _dialogs.PickImageFile();
        if (path is null) return;

        var bytes = File.ReadAllBytes(path);
        var source = LoadImage(bytes);
        SourceImagePath = path;
        SourceImage = source;
        ApplyAspectFrom(source.PixelWidth, source.PixelHeight);
        Status = $"Wybrano: {path}";
    }

    private bool CanPickOrCopy => !IsBusy;

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void CopyImage()
    {
        try
        {
            _dialogs.CopyImage(Image!);
            Status = "Skopiowano do schowka";
        }
        catch (Exception ex)
        {
            Status = $"Nie udało się skopiować: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void SaveImageAs()
    {
        var ext = _imageBytes is [0xFF, 0xD8, ..] ? ".jpg" : ".png";
        var path = _dialogs.ChooseSaveFile($"{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
        if (path is null) return;

        File.WriteAllBytes(path, _imageBytes!);
        Status = $"Zapisano: {path}";
    }

    [RelayCommand]
    private void ResetZoom() => Zoom = 1.0;

    private async Task RunAsync(Func<Task<byte[]>> produce)
    {
        IsBusy = true;
        Status = "Przetwarzanie...";
        try
        {
            var bytes = await produce();
            var path = SaveImage(bytes);
            _imageBytes = bytes;
            Image = LoadImage(bytes);
            Status = $"Zapisano: {path}";
        }
        catch (Exception ex)
        {
            Status = $"Błąd: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void Replace(ObservableCollection<ModelOption> target, IEnumerable<ImageModelInfo> infos,
                                IReadOnlyDictionary<string, ModelPricing> pricing)
    {
        target.Clear();
        foreach (var info in infos.OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase))
            target.Add(new ModelOption(info.Id, info.OwnedBy, info.Nsfw, info.InputModalities,
                                       pricing.GetValueOrDefault(info.Id)));
    }

    private void FillProviders(IEnumerable<ImageModelInfo> infos)
    {
        Providers.Clear();
        Providers.Add(AllProviders);
        foreach (var owner in infos.Select(i => i.OwnedBy).OfType<string>().Distinct().OrderBy(o => o, StringComparer.OrdinalIgnoreCase))
            Providers.Add(owner);
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
