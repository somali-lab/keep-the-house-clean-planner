namespace Huishoudplanner.Domain.Sheets;

/// <summary>The language of the text a sheet prints itself (requirements 6.1). User-entered names are never translated.</summary>
public enum SheetLanguage
{
    Nl,
    En,
}

/// <summary>Page orientation of the A4 week sheet (requirements 6.2).</summary>
public enum SheetOrientation
{
    Portrait,
    Landscape,
}
