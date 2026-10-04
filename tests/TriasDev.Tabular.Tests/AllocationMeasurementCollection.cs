using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// Tests that count allocated bytes run alone: Gen2 collections caused by tests running beside them
/// trim the shared array pool, and the buffers re-rented afterwards are counted as if they were per-row cost.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public static class AllocationMeasurementCollection
{
    public const string Name = "Allocation measurement";
}
