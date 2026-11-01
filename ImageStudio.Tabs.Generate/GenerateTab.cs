using ImageStudio.ViewModels;

namespace ImageStudio.Tabs.Generate;

// Zakładka "Generowanie" lewego panelu okna głównego; Content to DataContext jej widoku
public sealed record GenerateTab(object Content) : IMainTab
{
    public string Title => "Generowanie";
    public int Order => 0;
    object IMainTab.Content => Content;
}
