using ImageStudio.ViewModels;
using ImageStudio.Localization;

namespace ImageStudio.Tabs.Generate;

// Zakładka "Generowanie" lewego panelu okna głównego; Content to DataContext jej widoku
public sealed record GenerateTab(object Content) : IMainTab
{
    public string Title => Loc.T("tab.generate");
    public int Order => 0;
    object IMainTab.Content => Content;
}
