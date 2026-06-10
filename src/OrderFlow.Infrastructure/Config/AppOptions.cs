using Microsoft.Extensions.Configuration;
using OrderFlow.Domain.Features;

namespace OrderFlow.Infrastructure.Config;

/// <summary>
/// Typed configuration root. Per CLAUDE.md every rulebook threshold must live here
/// (appsettings.json), never as a literal in code — detector options arrive in M4+.
/// </summary>
public sealed class AppOptions
{
    public InstrumentOptions Instrument { get; set; } = new();
    public PipelineOptions Pipeline { get; set; } = new();
    public FeatureEngineOptions Features { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();

    public static AppOptions Load(string? basePath = null)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(basePath ?? AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();
        var options = new AppOptions();
        // The binder APPENDS JSON array items to a property's non-empty default array;
        // explicitly configured arrays must replace the default, so blank them first.
        if (config.GetSection("Features:FlowWindowsSeconds").Exists())
        {
            options.Features.FlowWindowsSeconds = Array.Empty<int>();
        }
        if (config.GetSection("Features:DepthImbalanceLevels").Exists())
        {
            options.Features.DepthImbalanceLevels = Array.Empty<int>();
        }
        config.Bind(options);
        return options;
    }
}

public sealed class InstrumentOptions
{
    public string Symbol { get; set; } = "ES";
    public decimal TickSize { get; set; } = 0.25m;
}

public sealed class PipelineOptions
{
    public int ChannelCapacity { get; set; } = 65536;
}

public sealed class StorageOptions
{
    /// <summary>
    /// SQLite file for cross-session feature state (naked POCs, ATR history). Empty =
    /// ephemeral in-memory stores, which keeps repeated replays of the same file
    /// byte-identical (a persistent db mutates between runs). Set a path when replaying
    /// consecutive sessions in sequence.
    /// </summary>
    public string SqlitePath { get; set; } = string.Empty;
}
