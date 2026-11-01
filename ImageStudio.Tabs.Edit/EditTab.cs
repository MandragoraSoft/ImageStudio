using ImageStudio.ViewModels;
using ImageStudio.Localization;

namespace ImageStudio.Tabs.Edit;

// Zakładka "Edycja" lewego panelu okna głównego; Content to DataContext jej widoku
public sealed record EditTab(object Content) : IMainTab
{
    public string Title => Loc.T("tab.edit");
    public int Order => 10;
    object IMainTab.Content => Content;
}
