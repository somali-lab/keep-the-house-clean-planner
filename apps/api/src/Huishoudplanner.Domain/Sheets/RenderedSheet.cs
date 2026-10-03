namespace Huishoudplanner.Domain.Sheets;

/// <summary>A rendered PDF with the file name and content type the download should carry.</summary>
public sealed record RenderedSheet(byte[] Content, string FileName, string ContentType)
{
    public const string PdfContentType = "application/pdf";
}
