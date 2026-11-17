using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>A plan that cannot work is refused before the file is read.</summary>
public sealed class PlanRefusalTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>A stream that counts how often anything asked it for bytes.</summary>
    private sealed class CountingStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public int Reads { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            Reads++;
            return base.Read(buffer);
        }
    }

    [Fact]
    public void OpensNoStreamForAMappingThatCannotWork()
    {
        // Opening a file is not free — a csv's head is read to detect the dialect, a package's whole
        // central directory to open it — and a mapping that does not fit its schema is answerable
        // without any of that.
        ImportSchema schema = new() { Fields = [ImportField.Text("iso").Require()] };

        MappingPlan plan = new()
        {
            Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "x", FieldName = "other" }],
        };

        CountingStream stream = new(Utf8NoBom.GetBytes("iso\nDE\n"));

        Assert.Throws<MappingPlanException>(
            () => TabularImporter.Import(stream, "t.csv", plan, schema, row => row.RowNumber, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Reads);
    }

    [Fact]
    public void LeavesABindingToAnUnknownFieldToTheValidatorInThePrecheck()
    {
        // The validator reports the field; the precheck has nothing of its own to say about it, and
        // judges the rest of the plan as it would without it.
        ImportSchema schema = new() { Fields = [ImportField.Text("iso").Require()] };
        MappingPlan plan = new()
        {
            Bindings =
            [
                new ColumnBinding { ColumnIndex = 0, Header = "iso", FieldName = "iso" },
                new ColumnBinding { ColumnIndex = 0, Header = "iso", FieldName = "nope" },
            ],
        };

        FileProfile profile;

        using (ITabularCursor cursor = TabularFile.Open(new MemoryStream(Utf8NoBom.GetBytes("iso\nDE\n"), writable: false), "t.csv", cancellationToken: TestContext.Current.CancellationToken))
        {
            profile = TabularAnalyzer.Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken);
        }

        PrecheckResult result = MappingPrecheck.Check(plan, schema, profile);

        Assert.Empty(result.Findings);
        Assert.Equal(ErrorCodes.Mapping.UnknownField, Assert.Single(MappingPlanValidator.Validate(plan, schema)).Code);
    }
}
