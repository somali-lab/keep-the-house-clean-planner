using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Huishoudplanner.Adapters.Pdf;

/// <summary>
/// The measurements and text styles of the sheets, taken from the stylesheet of the HTML sheets
/// (<c>apps/server/src/domain/pdf/html.ts</c>): 10 mm page margin, 9 pt body text, a 5 mm checkbox with a 0.4 mm
/// border, 0.3 mm table borders. Black and white only; nothing is carried by colour.
/// </summary>
internal static class SheetStyle
{
    /// <summary>
    /// Font strategy. The Docker image installs <c>fonts-dejavu-core</c> (plan section 3.1), so DejaVu Sans is the first
    /// choice, as in the HTML sheets. Liberation Sans and Arial cover other Linux and Windows machines, and Lato is the
    /// font QuestPDF ships inside its own package (SIL Open Font Licence), so a missing system font never leaves a
    /// sheet without glyphs for the Latin letters Dutch needs. No font file is embedded in this repository.
    /// </summary>
    public static readonly string[] FontFamilies = ["DejaVu Sans", "Liberation Sans", "Arial", "Lato"];

    public const float PageMarginMm = 10;
    public const float BodyPt = 9;
    public const float SmallPt = 8;
    public const float TitlePt = 14;
    public const float BoxMm = 5;
    public const float BoxBorderMm = 0.4f;
    public const float TableBorderMm = 0.3f;

    public static TextStyle Body(float size = BodyPt) =>
        TextStyle.Default.FontFamily(FontFamilies).FontSize(size).FontColor(Colors.Black);

    /// <summary>The hand-tickable checkbox: 5 mm by 5 mm, 0.4 mm black border.</summary>
    public static void Checkbox(IContainer container) =>
        container.Width(BoxMm, Unit.Millimetre).Height(BoxMm, Unit.Millimetre)
            .Border(BoxBorderMm, Unit.Millimetre).BorderColor(Colors.Black);

    /// <summary>A table cell with the 0.3 mm black border and 1 mm padding.</summary>
    public static IContainer Cell(IContainer container) =>
        container.Border(TableBorderMm, Unit.Millimetre).BorderColor(Colors.Black).Padding(1, Unit.Millimetre);
}
