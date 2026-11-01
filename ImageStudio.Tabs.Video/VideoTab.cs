using ImageStudio.ViewModels;
using ImageStudio.Localization;

namespace ImageStudio.Tabs.Video;

// Zakładka "Wideo" lewego panelu okna głównego; Content to DataContext jej widoku
public sealed record VideoTab(object Content) : IMainTab
{
    public string Title => Loc.T("tab.video");
    public int Order => 20;
    object IMainTab.Content => Content;
}
