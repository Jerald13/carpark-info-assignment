# Carpark Information API

A backend for searching Singapore HDB carparks and saving favourites, built for the
[take-home assignment](docs/ASSIGNMENT.md).

**.NET 10 LTS · SQLite · EF Core 10 · 215 tests · OpenAPI 3.1**

---

## Run it

**Prerequisite:** the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
`global.json` pins the channel, so a wrong version fails with a clear message rather than a
cryptic restore error.

```bash
git clone https://github.com/Jerald13/carpark-info-assignment.git
cd carpark-info-assignment

# 1. Load the data (2,181 carparks, ~3 seconds)
dotnet run --project src/CarparkInfo.BatchJob -- \
    --file hdb-carpark-information-20220824010400.csv

# 2. Start the API
dotnet run --project src/CarparkInfo.Api
```

Then open **<https://localhost:7293/swagger>**.

The database is created and migrated automatically. Nothing else to install, no connection string
to edit.

### Try the user stories

```bash
# Carparks offering free parking            -> 1,605
curl "http://localhost:5106/api/v1/carparks?freeParking=true&includeTotal=true"

# Carparks offering night parking           -> 1,795
curl "http://localhost:5106/api/v1/carparks?nightParking=true&includeTotal=true"

# Carparks that fit a 2.0 m vehicle         -> 2,056   <- see below
curl "http://localhost:5106/api/v1/carparks?vehicleHeight=2.0&includeTotal=true"

# All three, near Albert Centre, nearest first
curl "http://localhost:5106/api/v1/carparks?freeParking=true&nightParking=true&vehicleHeight=2.0&lat=1.3009&lon=103.8546&radiusKm=1&sort=distance"
```

Favourites need a token: register → login → paste it into Swagger's **Authorize** button, or:

```bash
curl -X PUT -H "Authorization: Bearer $TOKEN" \
    "http://localhost:5106/api/v1/favourites/ACB"
```

---

## The one thing worth reading

**`gantry_height = 0.00` appears on 477 rows, and every single one is a `SURFACE CAR PARK`.**

It does not mean "zero clearance". It means *there is no gantry* — an open-air carpark with no
height barrier at all. A further 67 rows carry `9.99`, also exclusively surface carparks, which is
the source system's sentinel for "unlimited".

So the obvious implementation of the third user story is wrong:

```sql
WHERE gantry_height >= 2.0     -- returns 1,579
```

The correct answer is **2,056**. That query silently hides **477 carparks — 23% of the dataset** —
and specifically the ones that accommodate *any* vehicle. It does not throw, it returns a plausible
non-empty result, and it passes a naive unit test.

The fix is to normalise at ingestion rather than in a query, so no consumer can get it wrong:

| Source value | Stored as | Meaning |
|---|---|---|
| `0.00` (477 rows) | `gantry_height_m = NULL`, `has_height_restriction = false` | no gantry |
| `9.99` (67 rows) | same | unlimited |
| `1.70`–`5.40` | the measured value, `has_height_restriction = true` | a real limit |

```sql
WHERE has_height_restriction = 0 OR gantry_height_m >= @vehicleHeight
```

The API exposes this as an **object**, never a bare number, because `"gantryHeight": 0.0` would
invite every client to reimplement the same bug:

```json
"heightRestriction": { "isRestricted": false, "maxVehicleHeightMetres": null }
```

Two more findings in the same vein:

- **`free_parking` is a schedule, not a boolean.** There is no `YES` in the data — the values are
  `NO`, `SUN & PH FR 7AM-10.30PM` and `SUN & PH FR 1PM-10.30PM`. A filter written as
  `free_parking = 'YES'` matches nothing, silently.
- **Addresses contain commas.** Over 30 rows, and `C10` contains four. `line.Split(',')` corrupts
  every field after `address` without erroring.

---

## Requirements

| Requirement | Where |
|---|---|
| **ER diagram** *(named deliverable)* | [docs/er-diagram.md](docs/er-diagram.md) |
| **Swagger** *(named deliverable)* | `/swagger`, document at `/openapi/v1.json` |
| Database design, 3NF | 10 tables, 4 lookups extracted |
| Query performance | One covering index; column order explained |
| Batch job | `src/CarparkInfo.BatchJob` |
| Daily delta semantics | `Delta` default, `Snapshot` behind a guard |
| **Whole-file rollback** | `AtomicMergeService` |
| Filter: free parking | `free_parking_type.is_offered` |
| Filter: night parking | `has_night_parking` |
| Filter: vehicle height | `HeightRestriction` |
| Add a favourite | Idempotent `PUT` |
| Supports unit testing | Ports and adapters, DI throughout |
| **Swap data-access tech** | `ICarparkRepository`, enforced by `ArchitectureTests` |
| **Swap CSV → JSON** | `IRecordSource` — the JSON reader ships |
| Large dataset | [docs/performance.md](docs/performance.md) |
| Job recovery | `IngestionRunner` |
| Secure coding | `ApiSecurity` |
| API auth | `AuthenticationService` |

---

## Design decisions worth defending

**Whole-file rollback without a giant transaction.** Wrapping every row in one transaction
satisfies the requirement at 2,181 rows and fails at scale — a multi-million-row transaction holds
a write lock for minutes and blocks every read. Instead the volume is absorbed into a staging table
no reader touches, and a single set-based `INSERT … ON CONFLICT DO UPDATE` applies it atomically in
milliseconds. `Rollback_leaves_the_database_byte_for_byte_untouched` checksums every field, injects
a mid-file failure, and asserts nothing moved.

**The failure audit uses a separate connection.** Writing it on the transaction being rolled back
would discard the explanation along with the data, leaving a clean database and nobody able to say
what happened at 03:00.

**BOLA is designed out, not guarded against.** OWASP API1:2023 has been the top API risk since 2019.
There is **no endpoint that accepts a user identifier** — it always comes from the token's `sub`
claim. The usual mitigation is an ownership check in every handler, which holds only for as long as
every future handler remembers to write one.

**Validation collects every defect before aborting.** Stopping at the first means an operator fixes
one problem, re-runs, waits, and finds the next. Severity is deliberate: structural problems reject
the row, semantic oddities warn and still ingest. The real file contains three of the latter, and
rejecting reference data for being internally inconsistent is how a nightly feed takes down
production at 02:00.

**Delta vs snapshot is a genuine ambiguity, handled explicitly.** The brief says "daily delta file";
the supplied file is a complete 2,181-row inventory. The two readings disagree on what *absence*
means. `Delta` is the default — guessing snapshot on a real three-row delta would deactivate 2,178
carparks — and `Snapshot` is one flag away, guarded by a 5% deactivation ratio so a truncated
transfer cannot wipe the catalogue. *This is the first question I would ask the data provider.*

**Three popular libraries deliberately absent.** `MediatR`, `AutoMapper` and `FluentAssertions` all
moved to commercial licensing during 2025. A financial institution exceeds any small-company free
tier, so all three are replaced — at no cost, since none was load-bearing.

**.NET 10 rather than the brief's .NET 6.x**, which reached end of support in November 2024.
.NET 8 and 9 both leave support on 2026-11-10. .NET 10 is the only channel that is simultaneously
the latest stable release and an LTS. Reverting to `net8.0` is a one-line change; `net6.0` would
take about an hour, and I would do it on request.

---

## Verify it yourself

```bash
pwsh ./check.ps1          # runs exactly what CI runs
```

```
1. restore                      PASS
2. format                       PASS
3. build (Release)              PASS
4. test                         PASS      215 tests
5. vulnerable packages          PASS
```

The tests run against **all 2,181 real carparks** loaded through the real ingestion pipeline, not
fixtures — a five-row fixture could not catch the height regression they exist to prevent. Counts
are pinned to values measured by profiling the CSV *before* any code was written.

The vulnerability gate is not decorative: it has already caught two high-severity transitive
advisories, `Microsoft.OpenApi` 2.0.0 and `SQLitePCLRaw.lib.e_sqlite3` 2.1.11. Neither appeared in
any package reference written by hand.

### At a million rows

```bash
dotnet run --project src/CarparkInfo.BatchJob -c Release -- \
    --generate-load-test-file 1000000 --file load-1m.csv
```

Measured results — including one that does **not** flatter the design — are in
[docs/performance.md](docs/performance.md).

---

## Layout

```
src/
  CarparkInfo.Domain/           entities, value objects.        references NOTHING
  CarparkInfo.Application/      use cases, ports, DTOs.         references Domain only
  CarparkInfo.Infrastructure/   EF Core, CSV/JSON, JWT.         implements Application's ports
  CarparkInfo.Api/              controllers, OpenAPI, security
  CarparkInfo.BatchJob/         CLI and scheduled worker
tests/
  *.UnitTests, *.IntegrationTests, *.FunctionalTests
docs/
  er-diagram.md                 required deliverable
  performance.md                measured 1M-row results
  ASSIGNMENT.md                 the original brief
```

The dependency rule is enforced by `ArchitectureTests` rather than by convention: a pull request
that has `Application` name an EF Core type, or leaks an `IQueryable` across a repository port,
breaks the build. That is the difference between an architecture and a diagram.

---

## Endpoints

| Method | Route | Auth | |
|---|---|---|---|
| `GET` | `/api/v1/carparks` | — | **The three user-story filters** |
| `GET` | `/api/v1/carparks/{carParkNo}` | — | One carpark |
| `GET` | `/api/v1/carparks/lookups` | — | Filter values with counts |
| `POST` | `/api/v1/auth/register` | — | Create an account |
| `POST` | `/api/v1/auth/login` | — | Access + refresh tokens |
| `POST` | `/api/v1/auth/refresh` | — | Rotate; reuse revokes the chain |
| `POST` | `/api/v1/auth/logout` | — | Revoke a refresh token |
| `GET` | `/api/v1/favourites` | Bearer | List mine |
| `PUT` | `/api/v1/favourites/{carParkNo}` | Bearer | **Add — idempotent** |
| `DELETE` | `/api/v1/favourites/{carParkNo}` | Bearer | Remove |
| `GET` | `/api/v1/admin/job-runs` | Admin | Ingestion history |
| `GET` | `/api/v1/admin/job-runs/{id}/defects` | Admin | Defect report with line numbers |
| `POST` | `/api/v1/admin/job-runs` | Admin | Trigger ingestion |
| `GET` | `/api/v1/health/live` · `/ready` | — | Readiness degrades on a stale feed |

---

## Batch job

```bash
# One file
dotnet run --project src/CarparkInfo.BatchJob -- --file <path>

# Drain the inbox, treating the file as a full snapshot
dotnet run --project src/CarparkInfo.BatchJob -- --mode Snapshot

# Run continuously on a timer
dotnet run --project src/CarparkInfo.BatchJob -- --scheduled

dotnet run --project src/CarparkInfo.BatchJob -- --help
```

Re-running an already-ingested file is a no-op. Idempotency is by SHA-256 of the file's bytes,
which is the precondition that makes automated retry safe — a retry that might double-apply is not
a retry.

---

## How a front-end would use this

*The brief asks the candidate to be ready to articulate this.*

`GET /carparks/lookups` returns every filter's values **with counts**, so the filter UI is built
from data rather than hard-coded enums — HDB adding an eighth carpark type needs no client release.
The counts let the UI grey out empty facets, and `vehicleHeight.commonPresets` comes from the real
distribution (2.15 m alone covers 807 carparks), so the height picker offers plausible choices
instead of an arbitrary slider.

Results carry `latitude`/`longitude` ready for a map SDK. The feed supplies SVY21 projected metres,
which no map understands, so the conversion happens once at ingestion and a front-end developer
never has to learn what SVY21 is.

`isFavourite` is returned **inline** on every result for authenticated callers, computed with a
join. Without it the client fetches `/favourites` and intersects two lists on every render — an N+1
by another name. The favourite toggle is optimistic: flip the icon, fire `PUT` or `DELETE`, and
because both are idempotent a failed retry needs no reconciliation.

Paging follows `pagination.nextCursor`. Unlike an offset, a cursor stays stable while the nightly
job writes — an offset shifts under a scrolling user, showing duplicates and hiding rows.

---

## Further reading

- [docs/er-diagram.md](docs/er-diagram.md) — the ER diagram and schema rationale
- [docs/performance.md](docs/performance.md) — measured 1M-row results
- [docs/ASSIGNMENT.md](docs/ASSIGNMENT.md) — the original brief
