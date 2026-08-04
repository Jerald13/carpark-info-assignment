# Carpark Information API

A backend for searching Singapore HDB carparks and saving favourites, built for the
[take-home assignment](docs/ASSIGNMENT.md).

**.NET 10 LTS · SQLite · EF Core 10 · 232 tests · OpenAPI 3.1**

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

Then open **<http://localhost:5106/swagger>** — note **http**, not https.

The database is created and migrated automatically. Nothing else to install, no connection string
to edit.

> **Why HTTP?** .NET ships a self-signed development certificate that is **not trusted until you
> say so**. Until then, `https://localhost:7293` is refused by the browser at the TLS handshake and
> Swagger sits on “LOADING” for ever — which looks like a broken API but is only an untrusted
> certificate.
>
> HTTP works immediately: HTTPS redirection is applied in Production only, deliberately, so a
> reviewer never has to trust anything before clicking **Execute**.
>
> To use HTTPS instead, run this once and restart the browser:
>
> ```bash
> dotnet dev-certs https --trust      # then accept the Windows prompt
> ```

<details>
<summary><b>Swagger still spins on “LOADING”? Clear the HSTS pin.</b></summary>

If <code>curl http://localhost:5106/api/v1/carparks?pageSize=1</code> returns <code>200</code> but
the browser still hangs, the browser has cached an HSTS policy for <code>localhost</code> and is
silently upgrading every <code>http://localhost:*</code> URL to <code>https://</code> — where
nothing is listening.

Browsers cache HSTS **per host, ignoring the port**, so a single run of *any* ASP.NET Core app in
Production mode on localhost pins it for every project on the machine.

Confirm it: **F12 → Network → Execute**. If the request shows `https://` when you typed `http://`,
or `(failed)`, that is HSTS.

Fix it in Chrome or Edge:

1. Open `chrome://net-internals/#hsts` (or `edge://net-internals/#hsts`)
2. Under **Delete domain security policies**, enter `localhost` and click **Delete**
3. Close every browser window and reopen

This app never sends HSTS on loopback, precisely to avoid causing it — see the comment in
`Program.cs`.

</details>

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

### Favourites — getting a token

Favourites need a bearer token. Three steps, all doable in Swagger without any client tooling.

**1. Register** — `POST /api/v1/auth/register` → **Try it out** → **Execute**

```json
{
  "email": "reviewer@example.com",
  "password": "correct-horse-battery-staple",
  "displayName": "Reviewer"
}
```

> The password must be **at least 12 characters**, or you get a `400` listing the rule.
> Registering an address twice returns the same message as registering a new one — that is
> deliberate, so the endpoint cannot be used to discover which addresses have accounts.

**2. Log in** — `POST /api/v1/auth/login` with the same email and password:

```json
{
  "accessToken":  "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxIiwi…",
  "refreshToken": "8Kx2vQ…",
  "expiresInSeconds": 900,
  "tokenType": "Bearer"
}
```

**3. Authorize** — click the **Authorize** button, paste the **`accessToken`** value, click Authorize.

- Paste the token **only**. Do not type `Bearer ` in front — Swagger adds it.
- Use `accessToken`, not `refreshToken`. The refresh token is a short random string for renewal
  and will not authenticate a request.
- It expires after **15 minutes**. A bearer token cannot be revoked once issued, so a short life
  bounds the damage if one leaks; `POST /api/v1/auth/refresh` gets a fresh pair without
  re-entering the password.
- **Tokens do not survive an API restart.** When no signing key is configured the app generates a
  random one at startup — deliberately, because a hard-coded fallback that silently works in
  production is how signing keys end up committed.

Then try `PUT /api/v1/favourites/ACB`. It returns **`201`** the first time and **`200`** if you
repeat it — never `409`, because favouriting twice is favouriting once.

Or from a terminal:

```bash
EMAIL="reviewer@example.com"; PASS="correct-horse-battery-staple"

curl -s -X POST http://localhost:5106/api/v1/auth/register \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"$EMAIL\",\"password\":\"$PASS\",\"displayName\":\"Reviewer\"}"

TOKEN=$(curl -s -X POST http://localhost:5106/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"$EMAIL\",\"password\":\"$PASS\"}" | jq -r .accessToken)

curl -X PUT -H "Authorization: Bearer $TOKEN" \
    "http://localhost:5106/api/v1/favourites/ACB"
```

### The administrator account

Three endpoints need the `Admin` role — ingestion history, the defect report, and the manual
trigger. Registration always creates a `User`, so **Development seeds an administrator at startup**:

| | |
|---|---|
| **Email** | `admin@carpark.local` |
| **Password** | `Admin!ChangeMe123` |

Sign in with `POST /api/v1/auth/login` exactly as above, then **Authorize** with that token. You can
now call:

```bash
ADMIN=$(curl -s -X POST http://localhost:5106/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@carpark.local","password":"Admin!ChangeMe123"}' | jq -r .accessToken)

# What the last ingestion run did
curl -s -H "Authorization: Bearer $ADMIN" http://localhost:5106/api/v1/admin/job-runs | jq

# The defect report — three warnings, each with the exact source line number
curl -s -H "Authorization: Bearer $ADMIN" http://localhost:5106/api/v1/admin/job-runs/1/defects | jq
```

> **This account exists in Development only.** The seed is guarded by an ordinal comparison against
> the environment name and fails closed — configuration can switch it *off* inside Development, but
> can never switch it *on* anywhere else, and `DevelopmentSeederTests` asserts both directions.
> Publishing the password here is safe precisely because the account cannot exist in Production.
> To disable it locally, set `Seed:Admin:Enabled` to `false`.

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
| **ER diagram** *(named deliverable)* | [docs/er-diagram.md](docs/er-diagram.md) — 3 focused views + full model, as SVG |
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
| Supports unit testing | Ports and adapters, DI throughout — 232 tests |
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
pwsh ./check.ps1          # build, format, tests, vulnerability audit
pwsh ./smoke.ps1          # starts the API and walks this README end to end
```

```
1. restore                      PASS
2. format                       PASS
3. build (Release)              PASS
4. test                         PASS      232 tests
5. vulnerable packages          PASS
```

`smoke.ps1` exists because the test suites cannot answer "does a reviewer who clones this get a
working system?". They call the API in-process, which never touches a real socket, an HTTP
redirect, or the OpenAPI document Swagger UI has to consume. A defect in any of those leaves every
test green while the page spins for ever — which happened once, and is why the check exists.

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

- [docs/er-diagram.md](docs/er-diagram.md) — the ER diagram, as three focused views plus the
  complete model. Every image is zoomable SVG.
- [docs/performance.md](docs/performance.md) — measured 1M-row results
- [docs/ASSIGNMENT.md](docs/ASSIGNMENT.md) — the original brief
