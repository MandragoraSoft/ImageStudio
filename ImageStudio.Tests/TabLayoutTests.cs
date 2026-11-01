using System.Windows.Controls;
using ImageStudio.Localization;
using ImageStudio.Views;

namespace ImageStudio.Tests;

// Ujednolicony układ zakładek lewego panelu: wybór modelu jest pierwszy (w zakładce albo w jej akordeonie) i ma tę samą nazwę
public sealed class TabLayoutTests
{
    private static StackPanel Stack(UserControl panel) => (StackPanel)((ScrollViewer)panel.Content).Content;

    private static void AssertModelFirst(Panel container)
    {
        var picker = Assert.IsType<ModelPicker>(container.Children[0]);
        Assert.Equal(Loc.T("common.model"), picker.Header);
        Assert.True(picker.HasFilters);
    }

    [Fact]
    public void Generate_edit_video_and_chat_start_with_the_model_picker_named_Model()
    {
        WpfHost.Run(() =>
        {
            AssertModelFirst(Stack(new ImageStudio.Tabs.Generate.GenerationPanel()));
            AssertModelFirst(Stack(new ImageStudio.Tabs.Edit.EditPanel()));
            AssertModelFirst(Stack(new ImageStudio.Tabs.Video.VideoPanel()));
            AssertModelFirst(Stack(new ImageStudio.Tabs.Chat.ChatPanel()));
        });
    }

    [Fact]
    public void Voice_sections_start_with_their_model_picker_named_Model()
    {
        WpfHost.Run(() =>
        {
            var sections = Stack(new ImageStudio.Tabs.Voice.VoicePanel()).Children.OfType<Expander>().ToList();

            Assert.Equal(2, sections.Count);
            Assert.All(sections, s => AssertModelFirst((Panel)s.Content));
        });
    }

    [Fact]
    public void Prompt_comes_right_after_the_model_and_the_action_button_is_last()
    {
        WpfHost.Run(() =>
        {
            foreach (var panel in new UserControl[] { new ImageStudio.Tabs.Generate.GenerationPanel(), new ImageStudio.Tabs.Edit.EditPanel(),
                                                      new ImageStudio.Tabs.Video.VideoPanel() })
            {
                var children = Stack(panel).Children;
                Assert.Equal(Loc.T("common.editPrompt"), ((Button)children[1]).Content);
            }

            Assert.Equal(Loc.T("gen.generate"), ((Button)Stack(new ImageStudio.Tabs.Generate.GenerationPanel()).Children.OfType<Button>().Last()).Content);
            Assert.Equal(Loc.T("edit.run"), ((Button)Stack(new ImageStudio.Tabs.Edit.EditPanel()).Children.OfType<Button>().Last()).Content);
        });
    }
}
