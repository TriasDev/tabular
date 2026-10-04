using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>What the Open XML SDK's schema validator finds wrong with a workbook — nothing, for a good one.</summary>
public static class OoxmlValidation
{
    public static IReadOnlyList<string> Errors(byte[] xlsx)
    {
        using MemoryStream stream = new(xlsx, writable: false);
        using SpreadsheetDocument document = SpreadsheetDocument.Open(stream, isEditable: false);

        return [.. new OpenXmlValidator(FileFormatVersions.Office2019)
            .Validate(document)
            .Select(error => $"{error.Part?.Uri} {error.Path?.XPath}: {error.Description}")];
    }
}
