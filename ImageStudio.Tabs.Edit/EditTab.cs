using ImageStudio.ViewModels;

namespace ImageStudio.Tabs.Edit;

// Zakładka "Edycja" lewego panelu okna głównego; Content to DataContext jej widoku
public sealed record EditTab(object Content) : IMainTab
{
    public string Title => "Edycja";
    public int Order => 10;
    object IMainTab.Content => Content;
}
