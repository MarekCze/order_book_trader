using Microsoft.Extensions.Configuration;

namespace OrderFlow.Infrastructure.Config;

/// <summary>
/// Typed configuration root. Per CLAUDE.md every rulebook threshold must live here
/// (appsettings.json), never as a literal in code — detector options arrive in M4+.
/// </summary>
public sealed class AppOptions
{
    public InstrumentOptions Instrument { get; set; } = new();
    public PipelineOptions Pipeline { get; set; } = new();

    public static AppOptions Load(string? basePath = null)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(basePath ?? AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();
        var options = new AppOptions();
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
