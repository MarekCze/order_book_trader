#!/usr/bin/env python3
"""Merge multiple per-day candidate journals into one combined journal so the M6
report can be run over a whole week. IDs are offset per source file to avoid
collisions (each run's candidate ids restart from 1). Deterministic: sources are
merged in the order given.

Usage: merge_journals.py <out.db> <in1.db> <in2.db> ...
"""
import sqlite3
import sys


def columns(conn, table):
    return [r[1] for r in conn.execute(f"PRAGMA table_info({table})")]


def main():
    out_path = sys.argv[1]
    sources = sys.argv[2:]
    if not sources:
        sys.exit("need at least one source journal")

    # Seed the combined schema from the first source (identical across runs).
    first = sqlite3.connect(sources[0])
    out = sqlite3.connect(out_path)
    for tbl in ("candidates", "fills"):
        ddl = first.execute(
            "SELECT sql FROM sqlite_master WHERE type='table' AND name=?", (tbl,)
        ).fetchone()[0]
        out.execute(f"DROP TABLE IF EXISTS {tbl}")
        out.execute(ddl)
    first.close()

    cand_cols = columns(out, "candidates")
    fill_cols = columns(out, "fills")
    cand_list = ", ".join(cand_cols)
    fill_list = ", ".join(fill_cols)

    offset = 0
    OFF_STEP = 100_000_000
    for src in sources:
        s = sqlite3.connect(src)
        for row in s.execute(f"SELECT {cand_list} FROM candidates"):
            row = list(row)
            row[cand_cols.index("id")] += offset
            out.execute(
                f"INSERT INTO candidates ({cand_list}) VALUES ({', '.join('?' * len(cand_cols))})",
                row,
            )
        for row in s.execute(f"SELECT {fill_list} FROM fills"):
            row = list(row)
            row[fill_cols.index("candidate_id")] += offset
            out.execute(
                f"INSERT INTO fills ({fill_list}) VALUES ({', '.join('?' * len(fill_cols))})",
                row,
            )
        s.close()
        offset += OFF_STEP

    out.commit()
    n = out.execute("SELECT COUNT(*) FROM candidates").fetchone()[0]
    out.close()
    print(f"merged {len(sources)} journals -> {out_path} ({n} candidates)")


if __name__ == "__main__":
    main()
