using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ImageStudio.ViewModels;

namespace ImageStudio.Views;

public partial class ChatConversation : UserControl
{
    public ChatConversation()
    {
        InitializeComponent();

        // Nowa wiadomość przewija rozmowę na dół
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not ChatViewModel vm) return;
            vm.Messages.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(ScrollToEnd, DispatcherPriority.Background);
            // Odpowiedź strumieniowa rośnie w miejscu: też przewija na dół
            vm.MessageUpdated += () => Dispatcher.BeginInvoke(ScrollToEnd, DispatcherPriority.Background);
        };
    }

    // ScrollViewer siedzi w szablonie listy wiadomości (wirtualizacja), więc nie ma pola o tej nazwie
    private void ScrollToEnd() => (MessageList.Template?.FindName("Scroll", MessageList) as ScrollViewer)?.ScrollToEnd();

    // Treść urosła, gdy rozmowa stała na samym dole (Markdown przebudowuje się z opóźnieniem, wirtualizacja poprawia szacunki
    // wysokości): zostajemy na dole. Kto przewinął w górę, nie jest ściągany; pola tekstowe w dymkach też zgłaszają to zdarzenie
    private void Messages_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer { Name: "Scroll" } scroll || e.ExtentHeightChange <= 0) return;

        var bottomBefore = e.ExtentHeight - e.ExtentHeightChange - (e.ViewportHeight - e.ViewportHeightChange);
        if (e.VerticalOffset - e.VerticalChange >= bottomBefore - 2) scroll.ScrollToEnd();
    }

    // Przeciąganie plików na panel; przeciąganie tekstu do pola wiadomości działa jak dotąd
    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || DataContext is not ChatViewModel vm) return;
        vm.AddFiles(files);
        e.Handled = true;
    }

    // Ctrl+V z obrazem w schowku dodaje załącznik; sam tekst wkleja się normalnie
    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || DataContext is not ChatViewModel vm) return;
        if (!ClipboardHasImage()) return;

        vm.PasteImageCommand.Execute(null);
        e.Handled = true;
    }

    private static bool ClipboardHasImage()
    {
        try
        {
            return Clipboard.ContainsImage();
        }
        catch (COMException)
        {
            // Schowek bywa chwilowo zajęty przez inny program
            return false;
        }
    }
}
