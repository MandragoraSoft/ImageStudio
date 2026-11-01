using ImageStudio.ViewModels;
using ImageStudio.Localization;

namespace ImageStudio.Tabs.Chat;

// Zakładka "Czat" lewego panelu okna głównego; Content to DataContext jej widoku
public sealed record ChatTab(object Content) : IMainTab
{
    public string Title => Loc.T("tab.chat");
    public int Order => 30;
    object IMainTab.Content => Content;
}
