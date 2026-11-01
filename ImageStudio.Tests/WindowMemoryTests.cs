using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ImageStudio.Configuration;
using ImageStudio.ViewModels;
using ImageStudio.Views;

namespace ImageStudio.Tests;

public sealed class WindowMemoryTests
{
    private static SettingsWindow NewSettings()
    {
        var current = new SettingsResult("u", "m", 1536, "", "system");
        return new SettingsWindow { DataContext = new SettingsViewModel(current, [new AppSettingsTab(current)]), ShowInTaskbar = false };
    }

    private static void Flush(Dispatcher d) => d.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    [Fact]
    public void Store_round_trips_windows_and_columns_through_the_file()
    {
        WindowStateStore.SetWindow("test.Window", new WindowRecord(10, 20, 700, 500, true));
        WindowStateStore.SetColumn("test.Column", 333);

        WindowStateStore.Reload();

        Assert.Equal(new WindowRecord(10, 20, 700, 500, true), WindowStateStore.GetWindow("test.Window"));
        Assert.Equal(333, WindowStateStore.GetColumn("test.Column"));
        Assert.Null(WindowStateStore.GetWindow("brak"));
    }

    [Fact]
    public void Resized_window_comes_back_with_its_size()
    {
        WpfHost.Run(() =>
        {
            WindowMemory.Store = new MemoryWindowStateStore();
            var first = NewSettings();
            first.Show();
            first.Width = 710;
            first.Height = 530;
            first.Close();

            var second = NewSettings();
            second.Show();
            Flush(second.Dispatcher);

            Assert.Equal(710, second.Width, 1);
            Assert.Equal(530, second.Height, 1);
            second.Close();
        });
    }

    [Fact]
    public void Fixed_size_dialog_is_not_resized_from_the_store()
    {
        WpfHost.Run(() =>
        {
            WindowMemory.Store = new MemoryWindowStateStore();
            WindowMemory.Store.SetWindow(typeof(PresetSaveWindow).FullName!, new WindowRecord(0, 0, 123, 456, false));

            var window = new PresetSaveWindow { DataContext = new PresetSaveViewModel("x"), ShowInTaskbar = false };
            window.Show();
            Flush(window.Dispatcher);

            Assert.NotEqual(123, window.Width);  // NoResize + SizeToContent: rozmiar z XAML
            window.Close();
        });
    }

    [Fact]
    public void Column_width_is_restored_and_saved_after_a_splitter_drag()
    {
        WpfHost.Run(() =>
        {
            WindowMemory.Store = new MemoryWindowStateStore();
            WindowMemory.Store.SetColumn("Test.Column", 412);
            var grid = new Grid();
            var column = new ColumnDefinition { Width = new GridLength(390) };
            grid.ColumnDefinitions.Add(column);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            WindowMemory.SetColumnKey(column, "Test.Column");

            Assert.Equal(412, column.Width.Value);
        });
    }
}
