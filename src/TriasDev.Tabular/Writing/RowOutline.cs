namespace TriasDev.Tabular;

/// <summary>How a sheet's rows are grouped. Csv ignores it.</summary>
public sealed record RowOutline
{
    /// <summary>The deepest level a row of the sheet is written at: 1 to 7.</summary>
    public required int Levels { get; init; }

    /// <summary>Whether a group's summary row sits above or below it. Ods has no such setting; LibreOffice draws its button on the group's first row.</summary>
    public SummaryRows SummaryRows { get; init; }

    /// <summary>How many levels show when the file opens, as Excel's level buttons count them: 1 shows the level-0 rows only. Null shows every row.</summary>
    public int? ShownLevels { get; init; }
}

/// <summary>Where the summary row of a group of rows sits.</summary>
public enum SummaryRows
{
    /// <summary>Above the group: a parent row written before its children.</summary>
    Above = 0,

    /// <summary>Below the group: a subtotal row written after its rows.</summary>
    Below = 1,
}
