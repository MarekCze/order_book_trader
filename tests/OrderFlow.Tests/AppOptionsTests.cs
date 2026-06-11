using OrderFlow.Infrastructure.Config;

namespace OrderFlow.Tests;

public class AppOptionsTests
{
    [Fact]
    public void Load_FeatureArraysFromJson_ReplaceDefaultsInsteadOfConcatenating()
    {
        // The ConfigurationBinder appends JSON array items onto a property's non-empty
        // default array; explicit config must REPLACE the default, not double it.
        var dir = Directory.CreateTempSubdirectory("orderflow-options-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "appsettings.json"),
                """
                {
                  "Features": {
                    "FlowWindowsSeconds": [10, 30, 60, 300],
                    "DepthImbalanceLevels": [1, 3, 5, 10]
                  }
                }
                """);

            var options = AppOptions.Load(dir.FullName);

            Assert.Equal(new[] { 10, 30, 60, 300 }, options.Features.FlowWindowsSeconds);
            Assert.Equal(new[] { 1, 3, 5, 10 }, options.Features.DepthImbalanceLevels);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_Overrides_TakePrecedenceOverJsonAndDefaults()
    {
        var dir = Directory.CreateTempSubdirectory("orderflow-options-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "appsettings.json"),
                """
                { "Detectors": { "Setup1": { "StopOffsetTicks": 3 } } }
                """);

            var options = AppOptions.Load(dir.FullName, new[]
            {
                "Detectors:Setup1:StopOffsetTicks=7", // overrides the JSON value
                "Risk.TickValue=10.0",                // '.' separator also accepted, overrides default
            });

            Assert.Equal(7, options.Detectors.Setup1.StopOffsetTicks);
            Assert.Equal(10.0m, options.Risk.TickValue);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_Override_WithoutEquals_Throws()
    {
        Assert.Throws<ArgumentException>(() => AppOptions.Load(null, new[] { "Detectors:Setup1:StopOffsetTicks" }));
    }

    [Fact]
    public void Load_WithoutFeatureSection_KeepsDefaults()
    {
        var dir = Directory.CreateTempSubdirectory("orderflow-options-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "appsettings.json"), "{}");

            var options = AppOptions.Load(dir.FullName);

            Assert.Equal(new[] { 10, 30, 60, 300 }, options.Features.FlowWindowsSeconds);
            Assert.Equal(new[] { 1, 3, 5, 10 }, options.Features.DepthImbalanceLevels);
            Assert.Equal(200, options.Features.SessionMinSamples);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
