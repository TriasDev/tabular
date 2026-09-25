namespace TriasDev.Tabular;

/// <summary>
/// A schema's fields by name, built once and the same way everywhere.
/// </summary>
/// <remarks>
/// <para>
/// Four places built this index and three of them let a duplicate name win silently, while the fourth
/// threw an exception from <c>ToDictionary</c> that named neither the schema nor the field. So a
/// schema declaring one name twice validated against one of the two, extracted into the other, and
/// read back through whichever the row accessor happened to resolve — a caller's mistake producing a
/// wrong value rather than a complaint.
/// </para>
/// <para>
/// A duplicate is a programming error, not a data fault: it is in the schema the programmer wrote,
/// long before any file is opened. So it throws, the way asking for a field the schema does not
/// declare throws, and it says which name is doubled.
/// </para>
/// </remarks>
internal static class SchemaFields
{
    /// <summary>Indexes a schema's fields by name.</summary>
    /// <exception cref="ArgumentException">Two fields share a name.</exception>
    public static Dictionary<string, TargetField> ByName(TargetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        Dictionary<string, TargetField> fields = new(schema.Fields.Count, StringComparer.Ordinal);

        // TryAdd is the loop's work, not a filter over it, so the loop stays a loop.
#pragma warning disable S3267
        foreach (TargetField field in schema.Fields)
#pragma warning restore S3267
        {
            if (!fields.TryAdd(field.Name, field))
            {
                throw new ArgumentException(
                    $"The schema declares more than one field named '{field.Name}'. A name identifies "
                    + "a field, so two of them cannot answer to it.",
                    nameof(schema));
            }
        }

        return fields;
    }
}
