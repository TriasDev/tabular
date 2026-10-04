using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Declaring an export: a column's type from its lambda, its header from a field, the writer's rules at Build.</summary>
public sealed class TabularExportBuilderTests
{
    private sealed record Portfolio(long Id, int Count, string Name, decimal Amount, double Rate, DateTime Start, DateOnly Settled, bool Active, long? Parent);

    private static readonly IntegerImportField IdField = ImportField.Integer("Id");
    private static readonly TextImportField NameField = ImportField.Text("Name");
    private static readonly DecimalImportField AmountField = ImportField.Decimal("Amount");
    private static readonly DecimalImportField RateField = ImportField.Decimal("Rate");
    private static readonly DateImportField StartField = ImportField.Date("Start");
    private static readonly DateImportField SettledField = ImportField.Date("Settled");
    private static readonly BooleanImportField ActiveField = ImportField.Boolean("Active");

    [Fact]
    public void DeclaresColumnsInOrderWithTheirHeaders()
    {
        TabularExport<Portfolio> export = TabularExport.For<Portfolio>()
            .Column("Id", p => p.Id)
            .Column("Count", p => p.Count)
            .Column("Name", p => p.Name)
            .Column("Amount", p => p.Amount)
            .Column("Rate", p => p.Rate)
            .Column("Start", p => p.Start)
            .Column("Settled", p => p.Settled)
            .Column("Active", p => p.Active)
            .Column("Parent", p => p.Parent)
            .Build();

        Assert.Equal(["Id", "Count", "Name", "Amount", "Rate", "Start", "Settled", "Active", "Parent"], export.Columns.Select(c => c.Header));
    }

    [Fact]
    public void TakesItsHeadersFromTheImportsFields()
    {
        TabularExport<Portfolio> export = TabularExport.For<Portfolio>()
            .Column(IdField, p => p.Id)
            .Column(NameField, p => p.Name)
            .Column(AmountField, p => p.Amount)
            .Column(RateField, p => p.Rate)
            .Column(StartField, p => p.Start)
            .Column(SettledField, p => p.Settled)
            .Column(ActiveField, p => p.Active)
            .Build();

        Assert.Equal(["Id", "Name", "Amount", "Rate", "Start", "Settled", "Active"], export.Columns.Select(c => c.Header));
    }

    [Fact]
    public void WidensDateColumnsUnlessToldOtherwise()
    {
        TabularExport<Portfolio> export = TabularExport.For<Portfolio>()
            .Column("Start", p => p.Start)
            .Column("Settled", p => p.Settled)
            .Column("Name", p => p.Name)
            .Column("Narrow", p => p.Start, width: 12)
            .Build();

        Assert.Equal(new double?[] { 19, 10, null, 12 }, export.Columns.Select(c => c.Width));
    }

    [Fact]
    public void RefusesATranslatedFieldsVariant()
    {
        ImportField variant = Assert.Single(ImportField.Translated("Title", ["en"]));

        Assert.Throws<ArgumentException>(() => TabularExport.For<Portfolio>().Column((TextImportField)variant, p => p.Name));
    }

    [Fact]
    public void RefusesAnExportWithoutColumns()
    {
        Assert.Throws<InvalidOperationException>(() => TabularExport.For<Portfolio>().Build());
    }

    [Fact]
    public void RefusesColumnsTheWriterWouldRefuseWhereTheyAreDeclared()
    {
        Assert.ThrowsAny<ArgumentException>(() => TabularExport.For<Portfolio>().Column("Name", p => p.Name).Column(" name", p => p.Name).Build());
        Assert.ThrowsAny<ArgumentException>(() => TabularExport.For<Portfolio>().Column("Name", p => p.Name).Column("NAME", p => p.Name).Build());
        Assert.ThrowsAny<ArgumentException>(() => TabularExport.For<Portfolio>().Column("", p => p.Name).Build());
        Assert.ThrowsAny<ArgumentException>(() => TabularExport.For<Portfolio>().Column("Name", p => p.Name, width: 0).Build());
    }

    [Fact]
    public void RefusesANullHeaderOrLambda()
    {
        Assert.Throws<ArgumentNullException>(() => TabularExport.For<Portfolio>().Column((string)null!, p => p.Name));
        Assert.Throws<ArgumentNullException>(() => TabularExport.For<Portfolio>().Column("Name", (Func<Portfolio, string?>)null!));
        Assert.Throws<ArgumentNullException>(() => TabularExport.For<Portfolio>().Column((TextImportField)null!, p => p.Name));
    }

    [Fact]
    public void BuildsAnExportTheBuilderNoLongerChanges()
    {
        TabularExportBuilder<Portfolio> builder = TabularExport.For<Portfolio>().Column("Id", p => p.Id);
        TabularExport<Portfolio> export = builder.Build();

        builder.Column("Name", p => p.Name);

        Assert.Single(export.Columns);
    }
}
