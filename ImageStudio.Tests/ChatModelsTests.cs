using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;

namespace ImageStudio.Tests;

// Modele czatu: opis z katalogu (właściciel, obraz na wejściu, rozumowanie) i filtry wspólne z modelami obrazów
public sealed class ChatModelsTests
{
    private static readonly ChatModelInfo[] Catalog =
    [
        new("openai/gpt-vision", ["reasoning", "include_reasoning"], true, "openai", ["text", "image"], "Model z obrazem"),
        new("anthropic/claude-text", ["temperature"], false, "anthropic", ["text"]),
        new("venice/uncensored", ["reasoning_effort"], null),  // brak właściciela i modalności: bierzemy z id / z braku danych
    ];

    private static TestApp Loaded()
    {
        var app = TestApp.Create();
        app.Provider.ChatCatalog = Catalog;
        app.Vm.InitializeAsync().GetAwaiter().GetResult();
        return app;
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    [Fact]
    public void Catalog_becomes_options_with_owner_modalities_and_reasoning_flag()
    {
        Sta.Run(() =>
        {
            using var app = Loaded();
            var options = app.Vm.ChatModelOptions.ToDictionary(o => o.Id);

            Assert.Equal(3, options.Count);
            var vision = options["openai/gpt-vision"];
            Assert.Equal("openai", vision.OwnedBy);
            Assert.True(vision.AcceptsImage);
            Assert.True(vision.Reasoning);
            Assert.Equal("Model z obrazem", vision.Description);

            var text = options["anthropic/claude-text"];
            Assert.False(text.AcceptsImage);
            Assert.False(text.Reasoning);

            var venice = options["venice/uncensored"];
            Assert.Equal("venice", venice.OwnedBy);  // z id
            Assert.True(venice.Reasoning);
        });
    }

    [Fact]
    public void Chat_filters_reuse_the_search_provider_and_image_filters()
    {
        Sta.Run(() =>
        {
            using var app = Loaded();
            var vm = app.Vm;
            IEnumerable<string> Visible() => vm.ChatModelsView.Cast<ModelOption>().Select(m => m.Id).ToList();
            Assert.Equal(3, Visible().Count());

            vm.SearchText = "claude";
            Assert.Equal(["anthropic/claude-text"], Visible());

            vm.SearchText = "";
            vm.SelectedProvider = "openai";
            Assert.Equal(["openai/gpt-vision"], Visible());

            vm.SelectedProvider = MainViewModel.AllProviders;
            vm.InputImageOnly = true;
            Assert.Equal(["openai/gpt-vision"], Visible());
        });
    }

    [Fact]
    public void Preferred_only_filter_applies_to_chat_models_too()
    {
        Sta.Run(() =>
        {
            using var app = Loaded();
            var vm = app.Vm;
            app.Get<ImageStudio.Configuration.ModelPreferences>().For("fake").Preferred.Add("anthropic/claude-text");

            vm.PreferredOnly = true;

            Assert.Equal(["anthropic/claude-text"], vm.ChatModelsView.Cast<ModelOption>().Select(m => m.Id));
        });
    }

    [Fact]
    public void Owner_filter_lists_providers_of_chat_models_as_well()
    {
        Sta.Run(() =>
        {
            using var app = Loaded();

            Assert.Contains("anthropic", app.Vm.Providers);
            Assert.Contains("venice", app.Vm.Providers);
            Assert.Equal(MainViewModel.AllProviders, app.Vm.Providers[0]);
        });
    }

    [Fact]
    public void Selected_option_follows_the_chat_model_and_ignores_null_from_the_filter()
    {
        Sta.Run(() =>
        {
            using var app = Loaded();
            var chat = app.Vm.Chat;

            chat.SelectedModelOption = chat.ModelOptions.Single(o => o.Id == "anthropic/claude-text");
            Assert.Equal("anthropic/claude-text", chat.SelectedModel);

            chat.SelectedModelOption = null;  // selektor zgłasza null, gdy filtr ukrył pozycję
            Assert.Equal("anthropic/claude-text", chat.SelectedModel);
            Assert.Equal("anthropic/claude-text", chat.SelectedModelOption!.Id);

            chat.SelectedModel = "openai/gpt-vision";
            Assert.Equal("openai/gpt-vision", chat.SelectedModelOption!.Id);
        });
    }

    [Fact]
    public void Enhancer_option_follows_the_enhancer_model()
    {
        Sta.Run(() =>
        {
            using var app = Loaded();
            var vm = app.Vm;

            vm.SelectedEnhancerModelOption = vm.ChatModelOptions.Single(o => o.Id == "venice/uncensored");
            Assert.Equal("venice/uncensored", vm.SelectedEnhancerModel);

            vm.SelectedEnhancerModelOption = null;
            Assert.Equal("venice/uncensored", vm.SelectedEnhancerModel);
        });
    }

    [Fact]
    public void Model_browser_includes_chat_models()
    {
        Sta.Run(() =>
        {
            using var app = Loaded();
            ModelBrowserViewModel? shown = null;
            app.Dialogs.OnShowModelBrowser = vm => shown = vm;

            app.Vm.OpenModelBrowserCommand.Execute(null);

            Assert.Contains(shown!.Items, i => i.Option.Id == "openai/gpt-vision" && i.KindList.Contains("czat"));
        });
    }

    [Fact]
    public void Chat_tab_shows_a_filter_panel_bound_to_the_shared_filters_and_the_described_models()
    {
        WpfHost.Run(() =>
        {
            using var app = Loaded();
            var vm = app.Vm;
            vm.ActiveTab = 3;
            var window = app.Get<ImageStudio.MainWindow>();
            window.ShowInTaskbar = false;
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var chatPanel = FindAll<System.Windows.Controls.UserControl>(window).First(c => c.GetType().Name == "ChatPanel");
            var picker = FindAll<ModelPicker>(chatPanel).First();
            Assert.True(picker.HasFilters);
            Assert.Same(vm, picker.FiltersContext);

            FindAll<System.Windows.Controls.Expander>(picker).First().IsExpanded = true;  // treść akordeonu powstaje po rozwinięciu
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var bar = FindAll<ModelFilterBar>(picker).Single();
            Assert.Same(vm, bar.DataContext);  // filtry działają na MainViewModel, mimo że panel czatu ma DataContext = ChatViewModel
            Assert.Same(vm.Chat.ModelOptionsView, picker.ItemsSource);
            window.Close();
        });
    }
}

public sealed class ModelCostTooltipTests
{
    [Fact]
    public void Cost_text_lists_current_list_price_discount_and_market_data()
    {
        var option = new ModelOption("m", "o", false, ["text"],
                                     new ModelPricing(0.04m, 0.05m, 20m, "obraz", 12m, 340m));

        Assert.Equal("Cena teraz: $0,04 za obraz|Cena katalogowa: $0,05|Rabat: 20%|Oferty na rynku: 12|Zapytania (24 h): 340",
                     option.CostText.Replace(Environment.NewLine, "|").Replace('.', ','));
    }

    [Fact]
    public void Cost_text_handles_token_prices_and_missing_prices()
    {
        var tokens = new ModelOption("m", "o", false, ["text"], new ModelPricing(2m, null, null, ModelPricing.TokenUnit, null, null));
        Assert.Contains("za 1M tokenów", tokens.CostText);
        Assert.DoesNotContain("Rabat", tokens.CostText);

        Assert.Contains("Brak ceny", new ModelOption("m", "o", false, ["text"], null).CostText);
    }

    [Fact]
    public void Model_list_item_tooltip_is_bound_to_name_description_and_costs()
    {
        WpfHost.Run(() =>
        {
            var template = (System.Windows.DataTemplate)System.Windows.Application.Current.Resources["ModelItemTemplate"];
            var option = new ModelOption("openai/gpt", "openai", false, ["text"],
                                         new ModelPricing(1m, null, null, "obraz", null, null), "Opis modelu");

            var item = (System.Windows.Controls.Grid)template.LoadContent();
            item.DataContext = option;
            var tip = (System.Windows.Controls.StackPanel)item.ToolTip;
            // Dymek nie jest w drzewie logicznym (WPF nadaje mu DataContext dopiero przy pokazaniu), więc sprawdzamy powiązania
            var paths = tip.Children.OfType<System.Windows.Controls.TextBlock>()
                .Select(t => System.Windows.Data.BindingOperations.GetBinding(t, System.Windows.Controls.TextBlock.TextProperty)?.Path.Path)
                .ToList();

            Assert.Equal(["Id", "Description", "CostText"], paths);
        });
    }
}

public sealed class ModelPickerHeaderTests
{
    private static IEnumerable<T> FindAll<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    [Fact]
    public void Price_in_the_collapsed_header_sits_at_the_right_edge()
    {
        WpfHost.Run(() =>
        {
            var option = new ModelOption("venice/z-image-turbo", "venice", false, ["text"],
                                         new ModelPricing(0.004m, null, null, "image", null, null));
            var picker = new ImageStudio.Views.ModelPicker { Header = "Model", ItemsSource = new[] { option }, SelectedItem = option };
            var window = new System.Windows.Window { Width = 420, Height = 200, ShowInTaskbar = false, Content = picker };
            window.Show();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            var price = FindAll<System.Windows.Controls.TextBlock>(picker).First(t => t.Text == option.PriceText);
            var accordion = FindAll<System.Windows.Controls.Expander>(picker).First();
            var right = price.TranslatePoint(new System.Windows.Point(price.ActualWidth, 0), accordion).X;

            // do prawej krawędzi akordeonu zostaje tylko wcięcie ramki
            Assert.True(accordion.ActualWidth - right < 24, $"cena kończy się {accordion.ActualWidth - right:0} px od prawej krawędzi");
            window.Close();
        });
    }
}
