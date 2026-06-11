using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace OrderFlow.Infrastructure.Journal;

/// <summary>
/// Exports the SQLite journal to CSV and Parquet (CLAUDE.md journaling requirement).
/// Both walk the actual table schema (PRAGMA table_info), so they track the journal
/// contract automatically. Output is deterministic: rows ordered by primary key, numbers
/// formatted with the invariant culture / round-trip format. The CSV export doubles as
/// the determinism-diff artifact for replays.
/// </summary>
public static class JournalExporter
{
    public static void ExportCsv(string dbPath, string outDir)
    {
        using var conn = Open(dbPath);
        ExportTableCsv(conn, "candidates", "id", Path.Combine(outDir, "candidates.csv"));
        ExportTableCsv(conn, "fills", "candidate_id, seq", Path.Combine(outDir, "fills.csv"));
    }

    public static async Task ExportParquetAsync(string dbPath, string outDir)
    {
        using var conn = Open(dbPath);
        await ExportTableParquetAsync(conn, "candidates", "id", Path.Combine(outDir, "candidates.parquet"));
        await ExportTableParquetAsync(conn, "fills", "candidate_id, seq", Path.Combine(outDir, "fills.parquet"));
    }

    private static SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        conn.Open();
        return conn;
    }

    /// <summary>Column names + declared SQLite types (INTEGER/REAL/TEXT) in table order.</summary>
    private static List<(string Name, string Type)> Columns(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        var cols = new List<(string, string)>();
        while (reader.Read())
        {
            cols.Add((reader.GetString(1), reader.GetString(2).ToUpperInvariant()));
        }
        return cols;
    }

    private static void ExportTableCsv(SqliteConnection conn, string table, string orderBy, string path)
    {
        var cols = Columns(conn, table);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(", ", cols.Select(c => c.Name))} FROM {table} ORDER BY {orderBy}";
        using var reader = cmd.ExecuteReader();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", cols.Select(c => c.Name)));
        while (reader.Read())
        {
            for (int i = 0; i < cols.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                if (!reader.IsDBNull(i))
                {
                    sb.Append(cols[i].Type switch
                    {
                        "INTEGER" => reader.GetInt64(i).ToString(CultureInfo.InvariantCulture),
                        "REAL" => reader.GetDouble(i).ToString("R", CultureInfo.InvariantCulture),
                        _ => Escape(reader.GetString(i)),
                    });
                }
            }
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString());

        static string Escape(string s) =>
            s.Contains(',') || s.Contains('"') || s.Contains('\n')
                ? "\"" + s.Replace("\"", "\"\"") + "\""
                : s;
    }

    private static async Task ExportTableParquetAsync(SqliteConnection conn, string table, string orderBy, string path)
    {
        var cols = Columns(conn, table);
        var fields = cols.Select<(string Name, string Type), DataField>(c => c.Type switch
        {
            "INTEGER" => new DataField<long?>(c.Name),
            "REAL" => new DataField<double?>(c.Name),
            _ => new DataField<string?>(c.Name),
        }).ToArray();

        var data = new List<object?>[cols.Count];
        for (int i = 0; i < cols.Count; i++)
        {
            data[i] = new List<object?>();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT {string.Join(", ", cols.Select(c => c.Name))} FROM {table} ORDER BY {orderBy}";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                for (int i = 0; i < cols.Count; i++)
                {
                    data[i].Add(reader.IsDBNull(i)
                        ? null
                        : cols[i].Type switch
                        {
                            "INTEGER" => reader.GetInt64(i),
                            "REAL" => reader.GetDouble(i),
                            _ => reader.GetString(i),
                        });
                }
            }
        }

        var schema = new ParquetSchema(fields);
        await using var stream = File.Create(path);
        using var writer = await ParquetWriter.CreateAsync(schema, stream);
        using var rowGroup = writer.CreateRowGroup();
        for (int i = 0; i < cols.Count; i++)
        {
            Array typed = cols[i].Type switch
            {
                "INTEGER" => data[i].Select(v => (long?)v).ToArray(),
                "REAL" => data[i].Select(v => (double?)v).ToArray(),
                _ => data[i].Select(v => (string?)v).ToArray(),
            };
            await rowGroup.WriteColumnAsync(new DataColumn(fields[i], typed));
        }
    }
}
