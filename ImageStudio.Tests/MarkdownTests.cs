using System.Windows;
using System.Windows.Documents;
using ImageStudio.Services;
using ImageStudio.Views;

namespace ImageStudio.Tests;

// Odpowiedzi czatu jako Markdown: blok kodu, pogrubienie, lista, nagłówek, link i tekst użytkownika bez formatowania
public sealed class MarkdownTests
{
    private static string TextOf(Block block) => new TextRange(block.ContentStart, block.ContentEnd).Text.Trim();

    [Fact]
    public void Fenced_code_becomes_a_monospace_block_without_the_fence_markers()
    {
        Sta.Run(() =>
        {
            var document = MarkdownRenderer.Build("Oto kod:\n\n```python\nn = 1\nfor _ in range(n):\n    print(n)\n```\n");

            var code = Assert.IsType<Paragraph>(document.Blocks.Last());
            Assert.Contains("Consolas", code.FontFamily.Source);
            var text = TextOf(code).Replace("\r", "");
            Assert.Equal("n = 1\nfor _ in range(n):\n    print(n)", text);
            Assert.DoesNotContain("```", TextOf(document.Blocks.First()));
        });
    }

    [Fact]
    public void Emphasis_headings_lists_and_links_are_formatted()
    {
        Sta.Run(() =>
        {
            var document = MarkdownRenderer.Build("# Tytuł\n\nTo jest **ważne** i *kursywa* oraz `kod`.\n\n1. pierwszy\n2. drugi\n\n- punkt\n\n[strona](https://example.com)");

            var blocks = document.Blocks.ToList();
            var heading = Assert.IsType<Paragraph>(blocks[0]);
            Assert.Equal("Tytuł", TextOf(heading));
            Assert.True(heading.FontSize > 20);

            var paragraph = Assert.IsType<Paragraph>(blocks[1]);
            Assert.Equal("To jest ważne i kursywa oraz kod.", TextOf(paragraph));
            Assert.Contains(paragraph.Inlines.OfType<Bold>(), b => TextOf(b.ContentStart.Paragraph!).Contains("ważne"));
            Assert.Single(paragraph.Inlines.OfType<Italic>());

            var ordered = Assert.IsType<List>(blocks[2]);
            Assert.Equal(TextMarkerStyle.Decimal, ordered.MarkerStyle);
            Assert.Equal(2, ordered.ListItems.Count);
            Assert.Equal(TextMarkerStyle.Disc, Assert.IsType<List>(blocks[3]).MarkerStyle);

            var link = Assert.IsType<Paragraph>(blocks[4]).Inlines.OfType<Hyperlink>().Single();
            Assert.Equal("https://example.com/", link.NavigateUri.ToString());
        });
    }

    [Fact]
    public void Only_http_links_are_clickable_and_raw_html_stays_text()
    {
        Sta.Run(() =>
        {
            var document = MarkdownRenderer.Build("[x](file:///C:/Windows/System32/calc.exe) [y](javascript:alert(1)) <b>tag</b>");

            var paragraph = Assert.IsType<Paragraph>(document.Blocks.Single());
            Assert.All(paragraph.Inlines.OfType<Hyperlink>(), h => Assert.Null(h.NavigateUri));
            Assert.Contains("<b>", TextOf(paragraph));
        });
    }

    [Fact]
    public void Tables_and_quotes_render_without_throwing()
    {
        Sta.Run(() =>
        {
            var document = MarkdownRenderer.Build("| a | b |\n|---|---|\n| 1 | 2 |\n\n> cytat\n\n---\n\n- [x] gotowe\n- [ ] nie");

            Assert.Contains(document.Blocks, b => b is Table);
            Assert.Contains(document.Blocks, b => b is Section);
            Assert.Contains("☑", new TextRange(document.ContentStart, document.ContentEnd).Text);
        });
    }

    [Fact]
    public void View_rebuilds_the_document_when_the_text_grows_like_in_a_stream()
    {
        Sta.Run(() =>
        {
            var view = new MarkdownView { Markdown = "**Ho" };
            view.Markdown = "**Hola** mundo";

            Assert.Equal("Hola mundo", new TextRange(view.Document.ContentStart, view.Document.ContentEnd).Text.Trim());
        });
    }

    [Fact]
    public void Only_assistant_messages_are_markdown()
    {
        Assert.True(new ChatMessage("assistant", "x").IsAssistant);
        Assert.False(new ChatMessage("user", "x").IsAssistant);
        Assert.False(new ChatMessage("tool", "x").IsAssistant);
    }

    [Fact]
    public void Chat_view_shows_assistant_answers_as_markdown_and_user_text_as_plain()
    {
        WpfHost.Run(() =>
        {
            using var app = TestApp.Create();
            var chat = app.Vm.Chat;
            chat.Messages.Add(new ChatMessage("user", "**nie formatuj**"));
            chat.Messages.Add(new ChatMessage("assistant", "To jest **ważne**"));
            var window = new System.Windows.Window { Content = new ImageStudio.Views.ChatConversation { DataContext = chat }, ShowInTaskbar = false };
            window.Show();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            var views = new List<MarkdownView>();
            void Walk(DependencyObject d)
            {
                for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
                    if (child is MarkdownView v) views.Add(v);
                    Walk(child);
                }
            }
            Walk(window);

            Assert.Equal(2, views.Count);
            Assert.Equal([Visibility.Collapsed, Visibility.Visible], views.Select(v => v.Visibility));
            window.Close();
        });
    }
}
