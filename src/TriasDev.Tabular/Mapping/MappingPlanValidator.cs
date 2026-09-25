
namespace TriasDev.Tabular;

/// <summary>
/// Checks a plan against its schema without opening the file.
/// </summary>
/// <remarks>
/// Cheap, immediate, and separate on purpose: faults in a mapping are a different kind of problem
/// from faults in data, they are found at a different moment, and a user fixes them in a different
/// place. Reporting them together would make a screen that cannot say which is which.
/// </remarks>
public static class MappingPlanValidator
{
    /// <summary>
    /// Reports everything wrong with a plan at once.
    /// </summary>
    /// <remarks>
    /// All of it, not the first: a user who fixes one fault and is told about the next has to upload
    /// again to learn about the third.
    /// </remarks>
    public static IReadOnlyList<MappingFault> Validate(MappingPlan plan, TargetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(schema);

        List<MappingFault> faults = [];

        HashSet<string> bound = CheckBindings(plan, SchemaFields.ByName(schema), faults);

        CheckRequiredFields(schema, bound, faults);
        CheckRequiredGroups(schema, bound, faults);
        CheckPlanShape(plan, faults);

        return faults;
    }

    /// <summary>
    /// Faults in the bindings themselves, and the names of the fields they validly bind.
    /// </summary>
    private static HashSet<string> CheckBindings(
        MappingPlan plan,
        Dictionary<string, TargetField> fields,
        List<MappingFault> faults)
    {
        HashSet<string> bound = new(StringComparer.Ordinal);

        foreach (ColumnBinding binding in plan.Bindings)
        {
            if (!fields.ContainsKey(binding.TargetFieldName))
            {
                faults.Add(BindingFault("mapping.unknown-field", binding));

                continue;
            }

            if (!bound.Add(binding.TargetFieldName))
            {
                faults.Add(BindingFault("mapping.duplicate-binding", binding));
            }

            if (binding.SourceColumnIndex < 0)
            {
                faults.Add(BindingFault("mapping.invalid-column", binding));
            }
        }

        return bound;
    }

    private static MappingFault BindingFault(string code, ColumnBinding binding) => new()
    {
        Code = code,
        TargetFieldName = binding.TargetFieldName,
        SourceColumnIndex = binding.SourceColumnIndex,
    };

    private static void CheckRequiredFields(TargetSchema schema, HashSet<string> bound, List<MappingFault> faults)
    {
        foreach (TargetField field in schema.Fields
            .Where(f => f.Required && f.Group is null && !bound.Contains(f.Name)))
        {
            faults.Add(new MappingFault { Code = "mapping.required-field-unmapped", TargetFieldName = field.Name });
        }
    }

    /// <remarks>
    /// A required group asks for one of its members, not for each: a file translated into German
    /// alone is a complete file, and demanding an English column would refuse it for saying nothing
    /// wrong.
    /// </remarks>
    private static void CheckRequiredGroups(TargetSchema schema, HashSet<string> bound, List<MappingFault> faults)
    {
        foreach (IGrouping<string, TargetField> group in schema.Fields
            .Where(f => f.Required && f.Group is not null)
            .GroupBy(f => f.Group!, StringComparer.Ordinal)
            .Where(group => !group.Any(f => bound.Contains(f.Name))))
        {
            faults.Add(new MappingFault { Code = "mapping.required-group-unmapped", TargetFieldName = group.Key });
        }
    }

    /// <summary>Faults in the plan's own settings, independent of any field.</summary>
    private static void CheckPlanShape(MappingPlan plan, List<MappingFault> faults)
    {
        if (plan.HeaderRowIndex < 0)
        {
            faults.Add(new MappingFault { Code = "mapping.invalid-header-row" });
        }

        if (plan.SheetIndex < 0)
        {
            faults.Add(new MappingFault { Code = "mapping.invalid-sheet" });
        }

        if (plan.Culture is { Length: > 0 } culture && !IsKnownCulture(culture))
        {
            faults.Add(new MappingFault { Code = "mapping.unknown-culture" });
        }
    }

    /// <summary>Every culture this runtime actually knows, by name.</summary>
    /// <remarks>
    /// Built once and compared against, rather than asking
    /// <see cref="System.Globalization.CultureInfo.GetCultureInfo(string)"/> whether a name is valid.
    /// Under ICU that question has no useful answer: almost any well-formed name is accepted and a
    /// synthetic culture invented for it, so a typo in a mapping would be honoured as a real culture
    /// and would then read numbers by rules nobody chose.
    /// </remarks>
    private static readonly HashSet<string> KnownCultures = BuildKnownCultures();

    private static HashSet<string> BuildKnownCultures()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (System.Globalization.CultureInfo culture
            in System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.AllCultures))
        {
            names.Add(culture.Name);
        }

        return names;
    }

    private static bool IsKnownCulture(string name) => KnownCultures.Contains(name);
}
