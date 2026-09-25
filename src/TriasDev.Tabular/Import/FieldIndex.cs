
namespace TriasDev.Tabular;

/// <summary>
/// Where each of a schema's fields sits in a row.
/// </summary>
/// <remarks>
/// Built once for a run. A field the schema does not declare is a mistake at the moment it is asked
/// for, not an empty value discovered later — asking for a field belonging to another target, or one
/// removed from the schema, is a programming error and should read as one.
/// </remarks>
internal sealed class FieldIndex
{
    private readonly Dictionary<string, int> _byName;
    private readonly Dictionary<string, (string Variant, int Position)[]> _byGroup;

    public FieldIndex(TargetSchema schema)
    {
        // Through the shared index, so a duplicate name is refused here as it is everywhere else
        // rather than quietly resolving to whichever field was declared last.
        SchemaFields.ByName(schema);

        _byName = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < schema.Fields.Count; i++)
        {
            _byName[schema.Fields[i].Name] = i;
        }

        // Resolved once, so reading a row's translations is a lookup rather than a scan of every
        // field the target declares.
        _byGroup = schema.Fields
            .Select((field, position) => (field, position))
            .Where(f => f.field.Group is not null && f.field.Variant is not null)
            .GroupBy(f => f.field.Group!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(f => (f.field.Variant!, f.position)).ToArray(),
                StringComparer.Ordinal);
    }

    public (string Variant, int Position)[] PositionsOf(TranslatedField group)
    {
        if (_byGroup.TryGetValue(group.Name, out (string Variant, int Position)[]? positions))
        {
            return positions;
        }

        throw new ArgumentException(
            $"The schema does not declare a field group named '{group.Name}'. It declares: "
            + (_byGroup.Count == 0 ? "none" : string.Join(", ", _byGroup.Keys.Order(StringComparer.Ordinal))) + ".",
            nameof(group));
    }

    public int PositionOf(TargetField field)
    {
        if (_byName.TryGetValue(field.Name, out int position))
        {
            return position;
        }

        throw new ArgumentException(
            $"The schema does not declare a field named '{field.Name}'. It declares: "
            + string.Join(", ", _byName.Keys.Order(StringComparer.Ordinal)) + ".",
            nameof(field));
    }
}
