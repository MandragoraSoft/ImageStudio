using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;

namespace ImageStudio.Tests;

// Zakładka ustawień "Dźwięk": domyślny mikrofon i wyjście aplikacji
public sealed class AudioSettingsTabTests
{
    private static readonly AudioDevice[] Inputs = [new(-1, "Domyślne"), new(0, "Mikrofon USB"), new(1, "Headset")];
    private static readonly AudioDevice[] Outputs = [new(-1, "Domyślne"), new(0, "Głośniki"), new(1, "Słuchawki")];

    private static SettingsResult Current(string? mic = null, string? speaker = null) =>
        new("u", "m", 1536, "k", "dark", Microphone: mic, Speaker: speaker);

    [Fact]
    public void Tab_starts_on_the_saved_devices_and_falls_back_to_the_system_default_when_one_is_gone()
    {
        var saved = new AudioSettingsTab(Current("Headset", "Słuchawki"), Inputs, Outputs);
        Assert.Equal("Headset", saved.SelectedInput!.Name);
        Assert.Equal("Słuchawki", saved.SelectedOutput!.Name);

        var gone = new AudioSettingsTab(Current("Odłączony mikrofon", null), Inputs, Outputs);
        Assert.Equal("Domyślne", gone.SelectedInput!.Name);
        Assert.Equal("Domyślne", gone.SelectedOutput!.Name);
    }

    [Fact]
    public void Apply_puts_the_chosen_device_names_into_the_result_and_keeps_the_rest()
    {
        var tab = new AudioSettingsTab(Current(), Inputs, Outputs) { SelectedInput = Inputs[1], SelectedOutput = Outputs[2] };

        var result = tab.Apply(Current() with { Theme = "light" });

        Assert.Equal("Mikrofon USB", result.Microphone);
        Assert.Equal("Słuchawki", result.Speaker);
        Assert.Equal("light", result.Theme);
        Assert.Equal(("Dźwięk", 5), (tab.Title, tab.Order));
    }

    [Fact]
    public void Tab_without_devices_leaves_the_previous_choice_in_the_result()
    {
        var tab = new AudioSettingsTab(Current("Headset", "Słuchawki"), [], []);

        var result = tab.Apply(Current("Headset", "Słuchawki"));

        Assert.Equal(("Headset", "Słuchawki"), (result.Microphone, result.Speaker));
    }

    [Fact]
    public async Task Settings_dialog_offers_the_tab_and_the_choice_reaches_the_voice_tab_and_the_player()
    {
        using var app = TestApp.Create();
        var dialogs = app.Get<FakeDialogs>();
        dialogs.SettingsAnswer = r => r with { Microphone = "Mikrofon testowy", Speaker = "Słuchawki" };

        await app.Vm.OpenSettingsCommand.ExecuteAsync(null);

        Assert.Contains(dialogs.LastSettingsTabs!, t => t is AudioSettingsTab);
        Assert.Equal(["Aplikacja", "Dźwięk", "Zapis", "Dostawca AI"], dialogs.LastSettingsTabs!.OrderBy(t => t.Order).Select(t => t.Title));
        Assert.Equal("Słuchawki", app.Vm.Voice.SelectedSpeaker!.Name);
        Assert.Equal(1, app.Get<FakeAudioPlayer>().OutputDevice);
        Assert.Equal("Mikrofon testowy", app.Vm.Voice.SelectedMicrophone!.Name);
    }

    [Fact]
    public async Task Cancelled_settings_do_not_change_the_devices()
    {
        using var app = TestApp.Create();
        app.Get<FakeDialogs>().SettingsAnswer = _ => null;

        await app.Vm.OpenSettingsCommand.ExecuteAsync(null);

        Assert.Equal("Domyślne", app.Vm.Voice.SelectedSpeaker!.Name);
    }

    [Fact]
    public async Task Opening_settings_picks_up_hardware_plugged_in_meanwhile()
    {
        using var app = TestApp.Create();
        var recorder = app.Get<FakeAudioRecorder>();
        Assert.Single(app.Vm.Voice.Microphones);
        recorder.Mics.Add(new AudioDevice(1, "Nowy mikrofon"));
        AudioSettingsTab? shown = null;
        app.Get<FakeDialogs>().SettingsAnswer = r => r;

        await app.Vm.OpenSettingsCommand.ExecuteAsync(null);
        shown = app.Get<FakeDialogs>().LastSettingsTabs!.OfType<AudioSettingsTab>().Single();

        Assert.Equal(["Mikrofon testowy", "Nowy mikrofon"], shown.Inputs.Select(d => d.Name));
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Find<T>(child) is { } nested) return nested;
        }
        return null;
    }

    [Fact]
    public void Settings_window_shows_the_audio_view_with_both_device_lists()
    {
        WpfHost.Run(() =>
        {
            var tab = new AudioSettingsTab(Current(), Inputs, Outputs);
            var vm = new SettingsViewModel(Current(), [tab]);
            var window = new SettingsWindow { DataContext = vm, ShowInTaskbar = false };
            window.EnsureTabViews();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var view = Find<AudioSettingsTabView>(window);
            Assert.NotNull(view);
            var combos = new List<System.Windows.Controls.ComboBox>();
            void Collect(DependencyObject d)
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                {
                    var c = VisualTreeHelper.GetChild(d, i);
                    if (c is System.Windows.Controls.ComboBox cb) combos.Add(cb);
                    Collect(c);
                }
            }
            Collect(view!);
            Assert.Equal(2, combos.Count);
            Assert.Equal("Domyślne", ((AudioDevice)combos[0].SelectedItem).Name);
            foreach (var combo in combos)
            {
                var shown = new List<string>();
                void Texts(DependencyObject d)
                {
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                    {
                        var c = VisualTreeHelper.GetChild(d, i);
                        if (c is System.Windows.Controls.TextBlock tb) shown.Add(tb.Text);
                        Texts(c);
                    }
                }
                Texts(combo);
                Assert.Contains("Domyślne", shown);               // nazwa urządzenia w polu wyboru
                Assert.DoesNotContain(shown, t => t.Contains("AudioDevice {"));  // nie zapis rekordu
            }
            window.Close();
        });
    }
}
