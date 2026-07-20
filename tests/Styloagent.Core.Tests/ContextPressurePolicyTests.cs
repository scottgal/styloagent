using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.Core.Tests;

public sealed class ContextPressurePolicyTests
{
    [Theory]
    [InlineData(0, ContextPressure.Unknown)]
    [InlineData(0.50, ContextPressure.Normal)]
    [InlineData(0.65, ContextPressure.Elevated)]
    [InlineData(0.80, ContextPressure.High)]
    [InlineData(0.95, ContextPressure.Critical)]
    public void Pressure_levels_are_shared_by_both_runtimes(double used, ContextPressure expected)
        => Assert.Equal(expected, ContextPressurePolicy.For(used));

    [Fact]
    public void Guidance_becomes_more_conservative_under_pressure()
    {
        Assert.Contains("concise", ContextPressurePolicy.Guidance(ContextPressure.Elevated));
        Assert.Contains("checkpoint", ContextPressurePolicy.Guidance(ContextPressure.Critical));
    }
}
