using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>What a test can see in a PDF: its pages, their text and their size. Uses PdfPig (test only, Apache-2.0).</summary>
internal sealed record PdfProbe(int Pages, string Text, IReadOnlyList<string> PageTexts, double Width, double Height)
{
    public static PdfProbe Read(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        var pages = document.GetPages().ToList();
        var texts = pages.Select(p => ContentOrderTextExtractor.GetText(p)).ToList();
        return new PdfProbe(pages.Count, string.Join('\n', texts), texts, pages[0].Width, pages[0].Height);
    }

    /// <summary>Text between two markers (exclusive): which day row a task sits in.</summary>
    public string Between(string start, string end)
    {
        var from = Text.IndexOf(start, StringComparison.Ordinal);
        if (from < 0)
        {
            return string.Empty;
        }

        var to = Text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        return to < 0 ? Text[(from + start.Length)..] : Text[(from + start.Length)..to];
    }

    /// <summary>The text with whitespace runs collapsed, so a wrapped line still contains its words in order.</summary>
    public string Flat => System.Text.RegularExpressions.Regex.Replace(Text, @"\s+", " ");
}
