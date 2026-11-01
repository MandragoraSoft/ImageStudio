using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace ImageStudio.Views;

// Tylko do odczytu pole z tekstem w Markdownie (odpowiedzi modelu): da się zaznaczać i kopiować, linki otwierają przeglądarkę.
// Dokument jest budowany od nowa przy każdej zmianie tekstu (odpowiedź strumieniowa dopisuje kawałki)
public sealed class MarkdownView : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownView),
            new PropertyMetadata("", (d, e) => ((MarkdownView)d).Document = MarkdownRenderer.Build((string?)e.NewValue)));

    public string? Markdown { get => (string?)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }

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
