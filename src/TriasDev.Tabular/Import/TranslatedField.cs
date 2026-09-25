using System.Collections;

namespace TriasDev.Tabular;

/// <summary>
/// One thing a file says in several languages, declared once.
/// </summary>
/// <remarks>
/// <para>
/// A file carrying <c>Title#en</c> beside <c>Title#de</c> is two columns saying one thing. Declaring
/// two unrelated fields would work and would lose what they have in common: that a row needs one of
/// them rather than both, that a screen should draw them together, and that the mapper wants them
/// back as a single value.
/// </para>
/// <para>
/// Underneath they are ordinary fields with ordinary names — <c>title.en</c>, <c>title.de</c> — each
/// fed by one column. That is the whole point of the shape: a binding, a plan and a row are exactly
/// what they were, so nothing downstream learns about languages.
/// </para>
/// <para>
/// Spread into a schema with <c>Fields = [.. Title, .. Text]</c>.
/// </para>
/// </remarks>
public sealed class TranslatedField : IEnumerable<TargetField>
{
    private readonly TextField[] _fields;

    internal TranslatedField(string name, IEnumerable<string> variants, bool required = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(variants);

        Name = name;

        List<TextField> fields = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string variant in variants)
        {
            if (string.IsNullOrWhiteSpace(variant))
            {
                throw new ArgumentException("A variant cannot be empty.", nameof(variants));
            }

            if (!seen.Add(variant))
            {
                throw new ArgumentException($"The variant '{variant}' is declared twice.", nameof(variants));
            }

            fields.Add(new TextField
            {
                Name = $"{name}.{variant}",
                Type = ColumnType.Text,
                Group = name,
                Variant = variant,
                Required = required,
            });
        }

        if (fields.Count == 0)
        {
            throw new ArgumentException("A translated field needs at least one variant.", nameof(variants));
        }

        _fields = [.. fields];
    }

    /// <summary>The group's name, which is also the prefix of every member's name.</summary>
    public string Name { get; }

    /// <summary>The members, one per variant, in the order they were declared.</summary>
    public IReadOnlyList<TextField> Fields => _fields;

    /// <summary>The member for one variant, for a mapper that wants a single language.</summary>
    /// <exception cref="ArgumentException">The variant was not declared.</exception>
    public TextField this[string variant] =>
        _fields.FirstOrDefault(f => string.Equals(f.Variant, variant, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException(
            $"'{Name}' has no variant '{variant}'. It has: "
            + string.Join(", ", _fields.Select(f => f.Variant)) + ".",
            nameof(variant));

    /// <summary>
    /// Declares that a row must carry at least one of these.
    /// </summary>
    /// <remarks>
    /// At least one, never a particular one: a catalogue translated into German alone is a complete
    /// catalogue, and a rule naming English would refuse it for saying nothing wrong.
    /// </remarks>
    public TranslatedField Require() => new(Name, _fields.Select(f => f.Variant!), required: true);

    /// <inheritdoc />
    public IEnumerator<TargetField> GetEnumerator() => _fields.AsEnumerable<TargetField>().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
