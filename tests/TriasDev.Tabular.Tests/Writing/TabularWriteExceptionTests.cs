using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>A value the format cannot hold is reported by code, with where it is.</summary>
public sealed class TabularWriteExceptionTests
{
    [Fact]
    public void CarriesTheCodeAndWhereTheValueIs()
    {
        TabularWriteException error = new(ErrorCodes.Write.PrecisionLoss, "data", 7, 2, "Amount", "message");

        Assert.IsAssignableFrom<TabularException>(error);
        Assert.Equal("write.precision-loss", error.Code);
        Assert.Equal("data", error.SheetName);
        Assert.Equal(7, error.RowNumber);
        Assert.Equal(2, error.ColumnIndex);
        Assert.Equal("Amount", error.Header);
    }

    [Fact]
    public void ReservesTheWritePrefix()
    {
        Assert.True(ErrorCodes.IsReserved("write.anything"));
    }
}
