using Microsoft.Extensions.Configuration;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Trading;

namespace OrderFlow.Infrastructure.Config;

/// <summary>
/// Typed configuration root. Per CLAUDE.md every rulebook threshold must live here
/// (appsettings.json), never as a literal in code.
/// </summary>
public sealed class AppOptions
{
    public InstrumentOptions Instrument { get; set; } = new();
    public PipelineOptions Pipeline { get; set; } = new();
    public FeatureEngineOptions Features { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public RiskOptions Risk { get; set; } = new();
    public ExecutionOptions Execution { get; set; } = new();
    public TradingOptions Detectors { get; set; } = new();

    /// <summary>
    /// Loads typed config from appsettings.json, optionally layering CLI overrides on top
    /// (for parameter sweeps without editing the file). Each override is
    /// <c>Key=Value</c> where Key is a configuration path using ':' or '.' separators —
    /// e.g. <c>Detectors:Setup1:StopOffsetTicks=4</c> or <c>Risk.TickValue=12.5</c>.
    /// Overrides win over the JSON file and the compiled defaults.
    /// </summary>
    public static AppOptions Load(string? basePath = null, IReadOnlyList<string>? overrides = null)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(basePath ?? AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        if (overrides is { Count: > 0 })
        {
            builder.AddInMemoryCollection(ParseOverrides(overrides));
        }
        var config = builder.Build();
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

    /// <summary>Parses <c>Key=Value</c> override strings into a configuration collection.
    /// Keys accept '.' as a friendlier alias for the ':' path separator.</summary>
    private static IEnumerable<KeyValuePair<string, string?>> ParseOverrides(IReadOnlyList<string> overrides)
    {
        foreach (var entry in overrides)
        {
            int eq = entry.IndexOf('=');
            if (eq <= 0)
            {
                throw new ArgumentException(
                    $"Invalid config override '{entry}'. Expected Key=Value, e.g. Detectors:Setup1:StopOffsetTicks=4.");
            }
            string key = entry[..eq].Replace('.', ':');
            string value = entry[(eq + 1)..];
            yield return new KeyValuePair<string, string?>(key, value);
        }
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
