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
