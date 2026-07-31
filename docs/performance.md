# Performance — measured, not projected

> *"The dataset has the potential to be large in size."* — README, Challenge Yourself
>
> The supplied file has 2,181 rows, which proves nothing about scale. These numbers come from
> actually generating and ingesting a **1,000,000-row** file and querying the result.

**Measured 2026-07-31** · .NET 10.0.302 Release · SQLite (WAL) · Windows 10, local NVMe SSD ·
`dotnet run -c Release`

---

## The load-test file

```bash
dotnet run --project src/CarparkInfo.BatchJob -c Release -- \
    --generate-load-test-file 1000000 --file load-1m.csv
```

| | |
|---|---|
| Rows | 1,000,000 |
| Size | **157.3 MB** |
| Generation time | 1.5 s |

The generator samples the **real dataset's distribution** rather than picking values uniformly.
That matters more than the row count: it preserves the ~25% share of rows carrying an unrestricted
gantry height (`0.00` or `9.99`) and the ~8% share of addresses containing commas, so the load test
exercises the two code paths most likely to break. A uniform synthetic file would never hit either.

---

## Ingestion

### First run — 1,000,000 new carparks

```
status      : Succeeded          wall clock  : 234 s
read        : 1,000,000          throughput  : ~4,270 rows/sec
inserted    : 1,000,000          database    : 585 MB
rejected    : 0                  peak memory : flat (streaming)
warnings    : 22,110
errors      : 0
```

The 22,110 warnings are synthetic rows where the sampled carpark type and deck count disagree — the
same `INCONSISTENT_DECK_COUNT` rule that flags three rows in the real file. They were all ingested,
which is the intended behaviour.

**Memory stays flat regardless of file size.** Records stream through `IAsyncEnumerable` and are
staged in batches of 1,000; the 157 MB file is never held in memory. This is the property that
makes the difference between working and an `OutOfMemoryException` at ten million rows.

### Second run — the same file, `--force`

```
status      : Succeeded          wall clock  : 238 s
read        : 1,000,000
inserted    : 0
updated     : 0
unchanged   : 1,000,000
```

### An honest result

**The unchanged run took 238 s — marginally *slower* than the 234 s insert.** That is not what the
design notes imply, and it is worth being precise about what `source_row_hash` actually buys.

The hash eliminates the **write** amplification: no `UPDATE` statements, no WAL pages, no index
maintenance, no replication traffic for unchanged rows. What it does **not** eliminate is the cost
of getting to the comparison — parsing, validating, converting coordinates, hashing and staging all
1,000,000 rows still happens, and at this size that parse-and-stage work dominates the total.

So the accurate claim is narrower than "ingestion cost tracks change rather than catalogue size":

- **Write cost** tracks change. ✔ Confirmed — zero rows updated.
- **Read and validate cost** tracks *file* size, not change. ✘ Unavoidable while the provider sends
  a full file.

The row hash is still worth having — write amplification is what hurts a replicated production
database, and it is what makes `--force` safe to run. But if a genuinely large daily feed became a
bottleneck, the fix is not a better hash: it is asking the provider for a real delta, or comparing
file hashes to skip unchanged files wholesale, which this design already does by default.

Recorded here rather than quietly omitted, because a performance claim that the measurement does not
support is worse than no claim.

---

## Query performance at 1,000,000 carparks

| Query | Rows returned | Time |
|---|---:|---:|
| Total active | 1,000,000 | 65.9 ms |
| Free parking (R10) | 735,359 | 130.8 ms |
| Night parking (R11) | 823,396 | 70.9 ms |
| **Fits a 2.0 m vehicle (R12)** | **945,796** | 411.8 ms |
| All three combined | 573,161 | 367.2 ms |

These are `COUNT(*)` over the whole matching set — the worst case, and precisely why
`includeTotal` is **opt-in** on the API. A normal page returns 20 rows and never computes a total.

### Keyset vs offset pagination

This is the measurement behind [ADR-008](../PLAN.md#adr-008):

| Fetching 20 rows at depth 500,000 | Time |
|---|---:|
| **Keyset** — `WHERE car_park_no > @cursor` | **0.2 ms** |
| `OFFSET 500000` | 31.2 ms |

**156× faster**, and the gap widens linearly with depth because `OFFSET` reads and discards every
skipped row. Speed is not even the main argument: a cursor is *stable* while the nightly job
writes, whereas an offset shifts under a user who is scrolling, showing them duplicates and hiding
other rows.

### The covering index is used

```
EXPLAIN QUERY PLAN
SELECT id FROM carpark
WHERE is_active = 1 AND has_night_parking = 1 AND free_parking_type_id IN (2,3)
  AND (has_height_restriction = 0 OR gantry_height_m >= 2.0)
LIMIT 20;
```

```
SEARCH carpark USING COVERING INDEX ix_carpark_search
    (is_active=? AND has_night_parking=? AND free_parking_type_id=?)
```

`SEARCH ... USING COVERING INDEX`, not `SCAN` — SQLite seeks on the three equality columns and
answers from the index without touching the table, at a million rows exactly as at two thousand.
This is asserted by `QueryPlanTests`, so a regression fails the build rather than quietly costing
latency.

---

## What these numbers do and do not show

**Do:**
- Memory is O(1) in file size — 157 MB streams through without accumulating.
- Whole-file atomicity survives at scale; the merge window stays short because staging absorbs the
  volume.
- Keyset pagination is constant-time at any depth.
- The covering index holds at 1M rows.

**Do not:**
- Concurrent API read latency *during* ingestion was not measured. WAL should keep readers
  unblocked, and the design is built around that claim, but it is untested here and should not be
  asserted without a number.
- This is one machine with a local SSD. A network filesystem would change the ingestion figures
  substantially, and SQLite over a network filesystem is unsafe regardless.
- 585 MB for a million carparks is comfortable for SQLite. At 100M the answer is PostgreSQL, and
  the port-and-adapter design ([ARCHITECTURE.md §15](../ARCHITECTURE.md)) is what makes that a new
  Infrastructure project rather than a rewrite.

---

## Reproducing

```bash
# 1. Generate
dotnet run --project src/CarparkInfo.BatchJob -c Release -- \
    --generate-load-test-file 1000000 --file load-1m.csv

# 2. Ingest into a throwaway database
ConnectionStrings__CarparkDatabase="Data Source=loadtest.db" \
dotnet run --project src/CarparkInfo.BatchJob -c Release -- --file load-1m.csv

# 3. Re-ingest to measure the unchanged path
ConnectionStrings__CarparkDatabase="Data Source=loadtest.db" \
dotnet run --project src/CarparkInfo.BatchJob -c Release -- --file load-1m.csv --force
```
