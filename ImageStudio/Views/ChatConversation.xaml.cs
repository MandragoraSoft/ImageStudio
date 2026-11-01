using System.Windows.Controls;
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
            if (e.NewValue is ChatViewModel vm)
                vm.Messages.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(Scroll.ScrollToEnd, DispatcherPriority.Background);
        };
    }
}
