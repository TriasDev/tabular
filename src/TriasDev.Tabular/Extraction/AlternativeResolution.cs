namespace TriasDev.Tabular;

/// <summary>Which group of a set of alternatives a row is located by, and how far it reaches.</summary>
/// <remarks>
/// Information, never a verdict: it changes no value. A caller that hands every row to another
/// service may ignore it; one that decides itself reads the winning group from here instead of
/// repeating the rule the schema already states.
/// </remarks>
public readonly record struct AlternativeResolution
{
    internal AlternativeResolution(string? group, int groupIndex, int level, string? levelName)
    {
        Group = group;
        GroupIndex = groupIndex;
        Level = level;
        LevelName = levelName;
    }

    /// <summary>The winning group's name, or null when no group is usable.</summary>
    public string? Group { get; }

    /// <summary>The winning group's position in the set, or −1.</summary>
    public int GroupIndex { get; }

    /// <summary>How many levels of the winning group the row reaches; 0 when unresolved.</summary>
    public int Level { get; }

    /// <summary>The deepest level reached, or null when unresolved.</summary>
    public string? LevelName { get; }

    /// <summary>Whether some group locates the row.</summary>
    public bool IsResolved => Group is not null;

    /// <summary>A row no group of the set can locate.</summary>
    internal static AlternativeResolution Unresolved { get; } = new(null, -1, 0, null);
}
