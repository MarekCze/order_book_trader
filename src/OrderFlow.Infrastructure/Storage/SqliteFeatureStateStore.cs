using Microsoft.Data.Sqlite;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Infrastructure.Storage;

/// <summary>
/// SQLite persistence for cross-session feature state (CLAUDE.md: naked-POC registry and
/// other multi-day state live in SQLite). One file holds both the naked-POC registry and
/// the ATR sample history. Active naked POCs are cached in memory at open — reads are
/// allocation-free and deterministic; writes go through to the file immediately.
/// Determinism note: replaying the same file twice against the same db MUTATES the db;
/// byte-identical replays require starting from the same db state (the CLI defaults to
/// ephemeral in-memory stores for this reason).
/// </summary>
public sealed class SqliteFeatureStateStore : INakedPocStore, IAtrHistoryStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly List<NakedPoc> _activeCache = new();

    public SqliteFeatureStateStore(string path)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _connection.Open();
        using var create = _connection.CreateCommand();
        create.CommandText =
            """
            CREATE TABLE IF NOT EXISTS naked_pocs (
                session_date TEXT NOT NULL,
                price_raw INTEGER NOT NULL,
                filled_on TEXT NULL,
                PRIMARY KEY (session_date, price_raw)
            );
            CREATE TABLE IF NOT EXISTS atr_samples (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_date TEXT NOT NULL,
                atr_points REAL NOT NULL,
                series TEXT NOT NULL DEFAULT 'atr5'
            );
            """;
        create.ExecuteNonQuery();
        // Migration for stores created before the regime-ATR series column existed.
        using (var migrate = _connection.CreateCommand())
        {
            migrate.CommandText = "ALTER TABLE atr_samples ADD COLUMN series TEXT NOT NULL DEFAULT 'atr5'";
            try
            {
                migrate.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Column already present — nothing to do.
            }
        }
        LoadActiveCache();
    }

    // ----- INakedPocStore -----

    public void Add(DateOnly sessionDate, Price poc)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "INSERT OR IGNORE INTO naked_pocs (session_date, price_raw) VALUES ($date, $price)";
        cmd.Parameters.AddWithValue("$date", Iso(sessionDate));
        cmd.Parameters.AddWithValue("$price", poc.RawNano);
        cmd.ExecuteNonQuery();
        _activeCache.Add(new NakedPoc(sessionDate, poc));
        SortCache();
    }

    public void MarkFilled(Price price, DateOnly filledOn)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "UPDATE naked_pocs SET filled_on = $filled WHERE price_raw = $price AND filled_on IS NULL";
        cmd.Parameters.AddWithValue("$filled", Iso(filledOn));
        cmd.Parameters.AddWithValue("$price", price.RawNano);
        cmd.ExecuteNonQuery();
        _activeCache.RemoveAll(p => p.Price == price);
    }

    public IReadOnlyList<NakedPoc> Active() => _activeCache;

    // ----- IAtrHistoryStore -----

    public void AddSample(DateOnly sessionDate, double atrPoints, string series = AtrSeries.FiveMin)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO atr_samples (session_date, atr_points, series) VALUES ($date, $atr, $series)";
        cmd.Parameters.AddWithValue("$date", Iso(sessionDate));
        cmd.Parameters.AddWithValue("$atr", atrPoints);
        cmd.Parameters.AddWithValue("$series", series);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<double> SamplesSince(DateOnly fromInclusive, string series = AtrSeries.FiveMin)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT atr_points FROM atr_samples WHERE session_date >= $from AND series = $series ORDER BY id";
        cmd.Parameters.AddWithValue("$from", Iso(fromInclusive));
        cmd.Parameters.AddWithValue("$series", series);
        using var reader = cmd.ExecuteReader();
        var samples = new List<double>();
        while (reader.Read())
        {
            samples.Add(reader.GetDouble(0));
        }
        return samples;
    }

    public int DistinctDaysSince(DateOnly fromInclusive, string series = AtrSeries.FiveMin)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(DISTINCT session_date) FROM atr_samples WHERE session_date >= $from AND series = $series";
        cmd.Parameters.AddWithValue("$from", Iso(fromInclusive));
        cmd.Parameters.AddWithValue("$series", series);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Dispose() => _connection.Dispose();

    private void LoadActiveCache()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT session_date, price_raw FROM naked_pocs WHERE filled_on IS NULL ORDER BY session_date, price_raw";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            _activeCache.Add(new NakedPoc(
                DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd"),
                new Price(reader.GetInt64(1))));
        }
    }

    private void SortCache() =>
        _activeCache.Sort((a, b) => a.SessionDate != b.SessionDate
            ? a.SessionDate.CompareTo(b.SessionDate)
            : a.Price.CompareTo(b.Price));

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd");
}
