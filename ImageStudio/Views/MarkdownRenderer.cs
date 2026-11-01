using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfBlock = System.Windows.Documents.Block;

namespace ImageStudio.Views;

// Markdown -> FlowDocument (czat): akapity, nagłówki, kod, listy, cytaty, tabele, linie, pogrubienia, linki.
// Kolory biorą się z zasobów motywu przez SetResourceReference, więc zmiana motywu przestawia już narysowane odpowiedzi
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras().UsePipeTables().UseTaskLists().UseAutoLinks().Build();

    private static readonly double[] HeadingSizes = [26, 22, 18, 16, 14, 14];

    public static FlowDocument Build(string? markdown)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left };
        foreach (var block in Markdig.Markdown.Parse(markdown ?? "", Pipeline)) AddBlock(document.Blocks, block);
        return document;
    }

    private static void AddBlock(BlockCollection target, MdBlock block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var title = new Paragraph { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4),
                                            FontSize = HeadingSizes[Math.Clamp(heading.Level, 1, 6) - 1] };
                AddInlines(title.Inlines, heading.Inline);
                target.Add(title);
                break;
            case ParagraphBlock paragraph:
                var text = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                AddInlines(text.Inlines, paragraph.Inline);
                target.Add(text);
                break;
            case CodeBlock code:  // także FencedCodeBlock
                target.Add(CodeParagraph(code.Lines.ToString().TrimEnd('\r', '\n')));
                break;
            case ListBlock list:
                target.Add(ToList(list));
                break;
            case QuoteBlock quote:
                var section = new Section { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0), Margin = new Thickness(0, 0, 0, 6) };
                section.SetResourceReference(WpfBlock.BorderBrushProperty, "BorderSoftBrush");
                section.SetResourceReference(TextElement.ForegroundProperty, "TextMutedBrush");
                foreach (var inner in quote) AddBlock(section.Blocks, inner);
                target.Add(section);
                break;
            case ThematicBreakBlock:
                var rule = new Paragraph { Margin = new Thickness(0, 4, 0, 8), FontSize = 2, BorderThickness = new Thickness(0, 0, 0, 1) };
                rule.SetResourceReference(WpfBlock.BorderBrushProperty, "BorderSoftBrush");
                target.Add(rule);
                break;
            case MdTable table:
                target.Add(ToTable(table));
                break;
            case LeafBlock leaf:  // np. HTML: pokazujemy jako zwykły tekst
                target.Add(new Paragraph(new Run(leaf.Lines.ToString())) { Margin = new Thickness(0, 0, 0, 6) });
                break;
            case ContainerBlock container:
                foreach (var inner in container) AddBlock(target, inner);
                break;
        }
    }

    private static Paragraph CodeParagraph(string code)
    {
        var paragraph = new Paragraph
        {
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 2, 0, 8),
            LineHeight = double.NaN,
        };
        paragraph.SetResourceReference(WpfBlock.BackgroundProperty, "SurfaceAltBrush");
        var lines = code.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run(lines[i]));
        }

        return paragraph;
    }

    private static List ToList(ListBlock list)
    {
        var result = new List
        {
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(24, 0, 0, 0),
        };
        if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start)) result.StartIndex = start;

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var listItem = new ListItem();
            foreach (var inner in item) AddBlock(listItem.Blocks, inner);
            result.ListItems.Add(listItem);
        }

        return result;
    }

    private static System.Windows.Documents.Table ToTable(MdTable table)
    {
        var result = new System.Windows.Documents.Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 8) };
        var columns = table.Max(row => ((MdTableRow)row).Count);
        for (var i = 0; i < columns; i++) result.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        foreach (var row in table.OfType<MdTableRow>())
        {
            var tableRow = new System.Windows.Documents.TableRow();
            foreach (var cell in row.OfType<MdTableCell>())
            {
                var tableCell = new System.Windows.Documents.TableCell { BorderThickness = new Thickness(1), Padding = new Thickness(6, 3, 6, 3) };
                tableCell.SetResourceReference(WpfBlock.BorderBrushProperty, "BorderSoftBrush");
                if (row.IsHeader)
                {
                    tableCell.FontWeight = FontWeights.SemiBold;
                    tableCell.SetResourceReference(WpfBlock.BackgroundProperty, "SurfaceAltBrush");
                }

                foreach (var inner in cell) AddBlock(tableCell.Blocks, inner);
                tableRow.Cells.Add(tableCell);
            }

            group.Rows.Add(tableRow);
        }

        result.RowGroups.Add(group);
        return result;
    }

    private static void AddInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null) return;
        foreach (var inline in container) AddInline(target, inline);
    }

    private static void AddInline(InlineCollection target, MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;
            case HtmlEntityInline entity:
                target.Add(new Run(entity.Transcoded.ToString()));
                break;
            case LineBreakInline lineBreak:
                if (lineBreak.IsHard) target.Add(new LineBreak());
                else target.Add(new Run(" "));
                break;
            case CodeInline code:
                var run = new Run(code.Content) { FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New") };
                run.SetResourceReference(TextElement.BackgroundProperty, "SurfaceAltBrush");
                target.Add(run);
                break;
            case EmphasisInline emphasis:
                Span span = emphasis.DelimiterChar switch
                {
                    '~' => new Span { TextDecorations = TextDecorations.Strikethrough },
                    _ when emphasis.DelimiterCount >= 2 => new Bold(),
                    _ => new Italic(),
                };
                AddInlines(span.Inlines, emphasis);
                target.Add(span);
                break;
            case LinkInline { IsImage: true } image:
                // Obrazów z sieci nie pobieramy: pokazujemy opis
                var alt = new Span();
                AddInlines(alt.Inlines, image);
                target.Add(alt);
                break;
            case LinkInline link:
                var hyperlink = new Hyperlink();
                if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") hyperlink.NavigateUri = uri;
                AddInlines(hyperlink.Inlines, link);
                if (hyperlink.Inlines.Count == 0) hyperlink.Inlines.Add(new Run(link.Url));
                target.Add(hyperlink);
                break;
            case AutolinkInline auto:
                var autoLink = new Hyperlink(new Run(auto.Url));
                if (Uri.TryCreate(auto.Url, UriKind.Absolute, out var autoUri) && autoUri.Scheme is "http" or "https") autoLink.NavigateUri = autoUri;
                target.Add(autoLink);
                break;
            case TaskList task:
                target.Add(new Run(task.Checked ? "☑ " : "☐ "));
                break;
            case HtmlInline html:
                target.Add(new Run(html.Tag));
                break;
            case ContainerInline container:
                AddInlines(target, container);
                break;
        }
    }
}
