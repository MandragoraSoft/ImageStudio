using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ImageStudio.Configuration;

namespace ImageStudio.Views;

// Zapamiętywanie rozmiarów jako zachowanie podpinane do elementów (attached property), bez kodu w oknach:
//  - WindowMemory.Remember na oknie (globalnie przez styl WindowBase w App.xaml): rozmiar po zmianie, a dla okna
//    głównego też położenie i stan zmaksymalizowany; zapis przy zamknięciu okna;
//  - WindowMemory.ColumnKey na ColumnDefinition: szerokość kolumny (np. lewego panelu) po przeciągnięciu GridSplittera.
public static class WindowMemory
{
    // Magazyn rozmiarów; zachowanie jest tworzone przez XAML (bez konstruktora z DI), więc composition root podmienia tę
    // właściwość przy starcie (testy: magazyn w pamięci)
    public static IWindowStateStore Store { get; set; } = new FileWindowStateStore();

    private const double MinVisibleTitle = 40;

    public static readonly DependencyProperty RememberProperty =
        DependencyProperty.RegisterAttached("Remember", typeof(bool), typeof(WindowMemory),
            new PropertyMetadata(false, OnRememberChanged));

    public static readonly DependencyProperty ColumnKeyProperty =
        DependencyProperty.RegisterAttached("ColumnKey", typeof(string), typeof(WindowMemory),
            new PropertyMetadata(null, OnColumnKeyChanged));

    private static readonly DependencyProperty ColumnTimerProperty =
        DependencyProperty.RegisterAttached("ColumnTimer", typeof(DispatcherTimer), typeof(WindowMemory));

    public static bool GetRemember(DependencyObject d) => (bool)d.GetValue(RememberProperty);
    public static void SetRemember(DependencyObject d, bool value) => d.SetValue(RememberProperty, value);
    public static string? GetColumnKey(DependencyObject d) => (string?)d.GetValue(ColumnKeyProperty);
    public static void SetColumnKey(DependencyObject d, string? value) => d.SetValue(ColumnKeyProperty, value);

    private static void OnRememberChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true) return;

        window.SourceInitialized += (_, _) => Restore(window);
        window.Closing += (_, _) => Save(window);
    }

    // Rozmiar wraca tylko oknom o zmiennej wielkości, bez SizeToContent; położenie tylko oknom bez centrowania
    // (główne). Dialogi dalej centrują się względem właściciela
    private static bool Resizable(Window w) =>
        w.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip && w.SizeToContent == SizeToContent.Manual;

    private static string KeyOf(Window w) => w.GetType().FullName!;

    private static void Restore(Window window)
    {
        if (Store.GetWindow(KeyOf(window)) is not { } record) return;

        var work = SystemParameters.WorkArea;
        if (Resizable(window))
        {
            window.Width = Math.Clamp(record.Width, Math.Max(window.MinWidth, 100), work.Width);
            window.Height = Math.Clamp(record.Height, Math.Max(window.MinHeight, 100), work.Height);
        }

        // Okno nie może wylądować poza ekranami (np. po odłączeniu monitora): wtedy zostaje domyślne położenie
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (window.WindowStartupLocation == WindowStartupLocation.Manual
            && screen.Contains(new Point(record.Left + MinVisibleTitle, record.Top + MinVisibleTitle / 2)))
        {
            window.Left = record.Left;
            window.Top = record.Top;
        }

        if (record.Maximized && Resizable(window)) window.WindowState = WindowState.Maximized;
    }

    private static void Save(Window window)
    {
        if (!Resizable(window)) return;

        // Zmaksymalizowane lub zminimalizowane okno zachowuje rozmiar sprzed zmiany (RestoreBounds)
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top)
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)) return;

        Store.SetWindow(KeyOf(window),
            new WindowRecord(bounds.Left, bounds.Top, bounds.Width, bounds.Height, window.WindowState == WindowState.Maximized));
    }

    private static void OnColumnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ColumnDefinition column || e.NewValue is not string key) return;

        if (Store.GetColumn(key) is { } saved)
            column.Width = new GridLength(Math.Clamp(saved, Math.Max(column.MinWidth, 150), SystemParameters.WorkArea.Width - 300));

        // GridSplitter zmienia Width kolumny; zapis z opóźnieniem, żeby nie pisać pliku przy każdym pikselu przeciągania
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        column.SetValue(ColumnTimerProperty, timer);
        timer.Tick += (_, _) => SaveColumn(column);
        DependencyPropertyDescriptor.FromProperty(ColumnDefinition.WidthProperty, typeof(ColumnDefinition))
            .AddValueChanged(column, (_, _) =>
            {
                timer.Stop();
                timer.Start();
            });
    }

    public static void SaveColumn(ColumnDefinition column)
    {
        (column.GetValue(ColumnTimerProperty) as DispatcherTimer)?.Stop();
        if (GetColumnKey(column) is { } key && column.ActualWidth > 0)
            Store.SetColumn(key, column.ActualWidth);
    }
}
