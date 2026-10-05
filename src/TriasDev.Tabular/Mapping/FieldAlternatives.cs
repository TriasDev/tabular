namespace TriasDev.Tabular;

/// <summary>
/// Groups of fields in priority order, of which a row needs one: coordinates, and where a row has
/// none, an address.
/// </summary>
/// <remarks>
/// <para>
/// The first group that is usable in a row wins it. A later group is judged only in rows where every
/// earlier one fell short — a row located by its coordinates does not care what its address says —
/// and its fields are validated strictly only there. Everywhere else their values are kept as the
/// file holds them, because a caller may still store them.
/// </para>
/// <para>
/// Rows are judged one by one: whether a particular row carries a usable group is something no
/// column profile can say, since two half-empty columns may cover each other's gaps.
/// </para>
/// </remarks>
public sealed class FieldAlternatives
{
    /// <summary>Declares a set of alternatives.</summary>
    /// <param name="name">The set's name, which reports and row errors carry.</param>
    /// <param name="groups">The groups, the most preferred first.</param>
    /// <param name="unresolvedRowFails">
    /// Whether a row with no usable group is a row error. False by default: the row is imported and
    /// reported, because the caller may hand it to something that decides better than a file can.
    /// </param>
    public FieldAlternatives(string name, IEnumerable<AlternativeGroup> groups, bool unresolvedRowFails = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(groups);

        AlternativeGroup[] list = [.. groups];

        if (list.Length == 0)
        {
            throw new ArgumentException("A set of alternatives needs at least one group.", nameof(groups));
        }

        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (AlternativeGroup group in list)
        {
            if (group is null)
            {
                throw new ArgumentException("A group cannot be null.", nameof(groups));
            }

            if (!names.Add(group.Name))
            {
                throw new ArgumentException($"The set '{name}' declares the group '{group.Name}' twice.", nameof(groups));
            }
        }

        Name = name;
        Groups = list;
        UnresolvedRowFails = unresolvedRowFails;
    }

    /// <summary>The set's name.</summary>
    public string Name { get; }

    /// <summary>The groups, the most preferred first.</summary>
    public IReadOnlyList<AlternativeGroup> Groups { get; }

    /// <summary>Whether a row with no usable group is a row error rather than a finding.</summary>
    public bool UnresolvedRowFails { get; }
}

/// <summary>
/// One way of satisfying a set of alternatives: an ordered list of levels, usable from
/// <see cref="RequiredLevels"/> on.
/// </summary>
/// <remarks>
/// A row reaches level <c>k</c> when levels 1 to <c>k</c> each carry a valid value — a gap ends the
/// ladder, because a street without its city locates nothing finer than the country. How far a row
/// reaches is its quality; whether it reaches <see cref="RequiredLevels"/> decides whether the group
/// is usable at all.
/// </remarks>
public sealed class AlternativeGroup
{
    private AlternativeGroup(string name, AlternativeLevel[] levels, int requiredLevels)
    {
        Name = name;
        Levels = levels;
        RequiredLevels = requiredLevels;
    }

    /// <summary>The group's name.</summary>
    public string Name { get; }

    /// <summary>The levels, the indispensable first.</summary>
    public IReadOnlyList<AlternativeLevel> Levels { get; }

    /// <summary>How many levels a row must reach for the group to be usable.</summary>
    public int RequiredLevels { get; }

    /// <summary>A group usable only when every one of its fields carries a value.</summary>
    /// <param name="name">The group's name.</param>
    /// <param name="fields">The fields, each its own level, named after it.</param>
    /// <example><code>AlternativeGroup.AllOf("coordinates", Latitude, Longitude)</code></example>
    public static AlternativeGroup AllOf(string name, params ImportField[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (fields.Any(f => f is null))
        {
            throw new ArgumentException("A field cannot be null.", nameof(fields));
        }

        AlternativeLevel[] levels = [.. fields.Select(f => new AlternativeLevel(f.Name, f))];

        if (levels.Length == 0)
        {
            throw new ArgumentException($"The group '{name}' needs at least one field.", nameof(fields));
        }

        return Ladder(name, levels.Length, levels);
    }

    /// <summary>A group whose levels refine one another, usable from <paramref name="requiredLevels"/> on.</summary>
    /// <param name="name">The group's name.</param>
    /// <param name="requiredLevels">How many levels a row must reach for the group to be usable.</param>
    /// <param name="levels">The levels, the indispensable first.</param>
    /// <example>
    /// <code>
    /// AlternativeGroup.Ladder("address", 1,
    ///     new AlternativeLevel("country", Country),
    ///     new AlternativeLevel("locality", PostalCode, City),
    ///     new AlternativeLevel("street", Street, StreetAndHouse),
    ///     new AlternativeLevel("house", HouseNumber, StreetAndHouse))
    /// </code>
    /// </example>
    public static AlternativeGroup Ladder(string name, int requiredLevels, params AlternativeLevel[] levels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(levels);

        if (levels.Length == 0)
        {
            throw new ArgumentException($"The group '{name}' needs at least one level.", nameof(levels));
        }

        if (levels.Any(l => l is null))
        {
            throw new ArgumentException("A level cannot be null.", nameof(levels));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(requiredLevels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requiredLevels, levels.Length);

        return new AlternativeGroup(name, [.. levels], requiredLevels);
    }
}

/// <summary>One rung of a group: satisfied when any of its fields carries a valid value.</summary>
/// <remarks>
/// Any, not all: a postal code and a city locate a row equally well, and a file need carry only one.
/// </remarks>
public sealed class AlternativeLevel
{
    /// <summary>Declares a level.</summary>
    /// <param name="name">The level's name, which reports carry.</param>
    /// <param name="anyOf">The fields any one of which satisfies it.</param>
    public AlternativeLevel(string name, params ImportField[] anyOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(anyOf);

        if (anyOf.Length == 0 || anyOf.Any(f => f is null))
        {
            throw new ArgumentException($"The level '{name}' needs at least one field, and none of them null.", nameof(anyOf));
        }

        Name = name;
        AnyOf = [.. anyOf];
    }

    /// <summary>The level's name.</summary>
    public string Name { get; }

    /// <summary>The fields any one of which satisfies the level.</summary>
    public IReadOnlyList<ImportField> AnyOf { get; }
}
