namespace TriasDev.Tabular;

/// <summary>One group resolved to schema positions.</summary>
/// <param name="Name">The group's name.</param>
/// <param name="LevelNames">The levels' names, in order.</param>
/// <param name="Levels">Per level, the positions of the fields any of which satisfies it.</param>
/// <param name="Members">Every position the group names, once.</param>
/// <param name="RequiredLevels">How many levels make the group usable.</param>
internal sealed record ResolvedGroup(string Name, string[] LevelNames, int[][] Levels, int[] Members, int RequiredLevels);

/// <summary>One set of alternatives resolved to schema positions.</summary>
/// <param name="Declared">The set as the schema declares it.</param>
/// <param name="Groups">Its groups, in priority order.</param>
internal sealed record ResolvedAlternatives(FieldAlternatives Declared, ResolvedGroup[] Groups);

/// <summary>
/// A schema's alternatives resolved to positions, refused where they cannot mean anything.
/// </summary>
/// <remarks>
/// The mistakes here are in code the programmer wrote, long before any file is opened, so they throw
/// like a duplicate field name does — and from every entry point, so none of them meets them later.
/// </remarks>
internal static class SchemaAlternatives
{
    /// <summary>Resolves a schema's alternatives to positions.</summary>
    /// <exception cref="ArgumentException">The alternatives do not fit the schema.</exception>
    public static ResolvedAlternatives[] Resolve(ImportSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (schema.Alternatives.Count == 0)
        {
            return [];
        }

        Dictionary<string, int> positions = new(StringComparer.Ordinal);

        for (int i = 0; i < schema.Fields.Count; i++)
        {
            positions[schema.Fields[i].Name] = i;
        }

        HashSet<string> sets = new(StringComparer.Ordinal);
        Dictionary<string, string> owners = new(StringComparer.Ordinal);
        List<ResolvedAlternatives> resolved = [];

        foreach (FieldAlternatives set in schema.Alternatives)
        {
            if (set is null)
            {
                throw new ArgumentException("A set of alternatives cannot be null.", nameof(schema));
            }

            if (!sets.Add(set.Name))
            {
                throw new ArgumentException($"The schema declares more than one set of alternatives named '{set.Name}'.", nameof(schema));
            }

            ResolvedGroup[] groups = [.. set.Groups.Select(g => Group(schema, set, g, positions, owners))];
            resolved.Add(new ResolvedAlternatives(set, groups));
        }

        return [.. resolved];
    }

    private static ResolvedGroup Group(
        ImportSchema schema,
        FieldAlternatives set,
        AlternativeGroup group,
        Dictionary<string, int> positions,
        Dictionary<string, string> owners)
    {
        string owner = $"{set.Name}/{group.Name}";
        int[][] levels = new int[group.Levels.Count][];
        SortedSet<int> members = [];

        for (int l = 0; l < levels.Length; l++)
        {
            levels[l] = [.. group.Levels[l].AnyOf.Select(field => Position(schema, field, owner, positions, owners))];
            members.UnionWith(levels[l]);
        }

        return new ResolvedGroup(group.Name, [.. group.Levels.Select(l => l.Name)], levels, [.. members], group.RequiredLevels);
    }

    private static int Position(
        ImportSchema schema,
        ImportField field,
        string owner,
        Dictionary<string, int> positions,
        Dictionary<string, string> owners)
    {
        if (!positions.TryGetValue(field.Name, out int position))
        {
            throw new ArgumentException($"The alternatives '{owner}' name the field '{field.Name}', which the schema does not declare.", nameof(schema));
        }

        if (schema.Fields[position].Required)
        {
            // Required says every row carries it; an alternative says a row may do without it.
            throw new ArgumentException($"The field '{field.Name}' is required and part of the alternatives '{owner}'; it can be one or the other.", nameof(schema));
        }

        if (owners.TryGetValue(field.Name, out string? other) && !string.Equals(other, owner, StringComparison.Ordinal))
        {
            // Which group a value counts towards must be one answer, or the winner of a row would
            // depend on the order the groups were checked in.
            throw new ArgumentException($"The field '{field.Name}' belongs to both '{other}' and '{owner}'.", nameof(schema));
        }

        owners[field.Name] = owner;
        return position;
    }
}
