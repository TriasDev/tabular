using System.Globalization;

namespace TriasDev.Tabular;

/// <summary>
/// A style registered with one writer by <see cref="TabularWriter.RegisterStyle"/>, to pass with each cell written in it.
/// Valid only with the writer that returned it. The default value is the unstyled cell.
/// </summary>
public readonly record struct StyleId
{
    internal StyleId(int writer, int value)
    {
        Writer = writer;
        Value = value;
    }

    /// <summary>The stamp of the writer that handed this id out; 0 for the default, which every writer accepts.</summary>
    internal int Writer { get; }

    /// <summary>The style's index in its writer's table; 0 is no style.</summary>
    internal int Value { get; }

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"StyleId({Value}, writer {Writer})");
}
