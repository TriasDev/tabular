namespace TriasDev.Tabular;

/// <summary>The file shapes this library reads.</summary>
/// <remarks>
/// Open to new members — an archive of files, an OpenDocument spreadsheet — as the library learns to
/// read them. A switch over it needs a default arm, or the day a member is added breaks the build of
/// whoever wrote it.
/// </remarks>
public enum TabularFormat
{
    /// <summary>An OOXML workbook.</summary>
    Xlsx,

    /// <summary>A delimiter-separated text file.</summary>
    Csv,

    /// <summary>An OpenDocument spreadsheet, as LibreOffice writes it.</summary>
    Ods,
}
