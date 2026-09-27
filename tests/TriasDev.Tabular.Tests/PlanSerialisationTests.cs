using System.Text.Json;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>A plan survives storage: serialised and read back, it equals the one written.</summary>
public sealed class PlanSerialisationTests
{
    [Fact]
    public void RoundTripsAPlanThroughJsonAsAnEqualOne()
    {
        MappingPlan plan = new()
        {
            SheetIndex = 1,
            Culture = "de-DE",
            SheetName = "Orders",
            Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "a", Header = "A", TreatAsEmpty = ["k.A.", "-"] }],
        };

        MappingPlan? back = JsonSerializer.Deserialize<MappingPlan>(JsonSerializer.Serialize(plan));

        Assert.Equal(plan, back);
        Assert.Equal(plan.GetHashCode(), back!.GetHashCode());
    }
}
