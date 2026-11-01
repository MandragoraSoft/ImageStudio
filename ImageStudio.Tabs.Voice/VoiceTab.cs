using ImageStudio.ViewModels;

namespace ImageStudio.Tabs.Voice;

// Zakładka "Głos" lewego panelu okna głównego (mowa z tekstu, rozpoznawanie mowy); Content to DataContext jej widoku
public sealed record VoiceTab(object Content) : IMainTab
{
    public string Title => "Głos";
    public int Order => 40;
    object IMainTab.Content => Content;
}
