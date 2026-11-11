using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace ImageStudio.Views;

// Tylko do odczytu pole z tekstem w Markdownie (odpowiedzi modelu): da się zaznaczać i kopiować, linki otwierają przeglądarkę.
// Dokument jest budowany od nowa przy zmianie tekstu, w oknie najwyżej raz na 120 ms (odpowiedź strumieniowa dopisuje kawałki)
public sealed class MarkdownView : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownView),
            new PropertyMetadata("", (d, e) => ((MarkdownView)d).OnMarkdownChanged()));

    public string? Markdown { get => (string?)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }

    // Odpowiedź strumieniowa zmienia tekst kilkadziesiąt razy na sekundę, a przebudowa dokumentu z layoutem kosztuje ~50 ms:
    // widok w oknie przebudowuje się najwyżej raz na Interval, ostatnia zmiana zawsze zostaje narysowana
    private readonly DispatcherTimer _throttle = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _dirty;

    private void OnMarkdownChanged()
    {
        // Poza oknem (testy, widok jeszcze niezaładowany) i przy pierwszej zmianie po przerwie: od razu
        if (!IsLoaded || !_throttle.IsEnabled)
        {
            Rebuild();
            if (IsLoaded) _throttle.Start();
        }
        else
        {
            _dirty = true;
        }
    }

    private void Rebuild()
    {
        _dirty = false;
        Document = MarkdownRenderer.Build(Markdown);
    }

    public MarkdownView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;  // bez tego linki w dokumencie nie reagują
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Background = System.Windows.Media.Brushes.Transparent;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        Document = MarkdownRenderer.Build("");
        AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(OnNavigate));
        _throttle.Tick += (_, _) =>
        {
            if (_dirty) Rebuild();
            else _throttle.Stop();
        };
        Unloaded += (_, _) =>
        {
            _throttle.Stop();
            if (_dirty) Rebuild();
        };
    }

    // Tylko http(s): odpowiedź modelu nie może uruchamiać dowolnych plików ani protokołów
    private static void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri is { Scheme: "http" or "https" })
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    // Pole nie przewija się samo, więc kółko myszy ma przewijać rozmowę (ScrollViewer wyżej)
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (e.Handled) return;

        e.Handled = true;
        (Parent as UIElement)?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = this,
        });
    }
}
