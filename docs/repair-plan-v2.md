# Pharmco POS — Repair Plan v2 (evidence-based)

**Branch:** `repair/audit-v2` (cut from `main` @ `bc86604`)
**Preserved work:** commit *"preserve in-progress server-side Java-to-C# repair (13 files)"*
**Document status:** written from direct repository inspection; every claim below cites file/line evidence.

> **Verification status: NOT BUILD-VERIFIED.** The machine used to write this plan has
> `dotnet.exe` present but **no .NET SDK installed** (`C:\Program Files\dotnet\sdk` → empty),
> **no Docker**, and **no winget**. Therefore `dotnet restore/build/test` have **not** been run
> and no stage below may be marked complete until real command output exists. Python is
> available, so `cli/` tests are runnable locally.

---

## 1. Why this plan exists

An earlier audit (27 items) was written against the **committed HEAD** state, and it is
largely correct **for that state**. Before this branch, the working tree already contained an
uncommitted 13-file server-side repair that fixed most of the Java contamination in
`server/Pharmco.Api` and `server/Pharmco.Core`. That work is now committed verbatim on this
branch (nothing was discarded), so:

* Items already fixed by that work must **not** be "re-fixed".
* The remaining work is **smaller and different** from what the audit implies.

### 1.1 Audit items already resolved by the preserved commit

| Audit topic | Evidence at `main` (fixed) | Evidence in working tree (fix) |
|---|---|---|
| `RateLimiter` Java APIs (`java.time`, `java.util.concurrent`, `ConcurrentHashMap`, `java.util.ArrayDeque`, `.ComputeIfAbsent`, `.toEpochSecond()`, `.IsEmpty()`) | `RateLimiter.cs:3,4,22,24,42,51,59,68,72,96,99` | rewritten onto `ConcurrentDictionary` + `Queue` + per-key locks |
| Invalid endpoint syntax `if (var denied = …; denied is not null)` | `DarajaConfigEndpoints.cs:192` | all handlers now use the 2-line form |
| `.IsEmpty()`, `http.User.Roles.Contains(...)`, `http.Request.RemoteAddress` | `Authz.cs:145,154`, `AuthEndpoints.cs:120` | `string.IsNullOrEmpty`, `http.User.IsInRole`, `http.Connection.RemoteIpAddress?.ToString()` |
| Java time in auth/JWT | `AuthEndpoints.cs:5` `using java.time;`, `DateTimeOffset.now()`, `ofEpochSecond(...)` | `DateTimeOffset.UtcNow`, `now.AddSeconds(...)` |
| Case-sensitive tenant status compare | `tenant.Status.ToLower().Equals("active")` | `Equals("active", StringComparison.OrdinalIgnoreCase)` |
| `PasswordService` Java string API | `PasswordService.cs` | `string.IsNullOrEmpty` (bcrypt cost 12 untouched) |

### 1.2 Audit items that do **not** apply to this repository (do not "fix")

* `JwtService` `_secret` / `HmacSha256` static-vs-instance bug — not present; `HmacSha256` is a
  correct instance method over the readonly `_secret` field.
* Reflection hack on a private `_key` field — **no reflection anywhere** in the repo
  (`GetField|BindingFlags|NonPublic|Reflection.` → 0 hits).
* `DeviceId = Guid.NewGuid()` on the server — the actual (worse) code is a client-supplied
  `MachineName + "-" + UserName.GetHashCode()` string parsed to `Guid.Empty`. See BUG-006.
* Removing a `tenant_id` filter from the M-Pesa sale update — **do not do this**.
  `sales.tenant_id` is `uuid NOT NULL` with `UNIQUE (tenant_id, invoice_no)`
  (`db/tenant/002_tenant_template.sql:48,62`). Hardening by *adding* a tenant filter is allowed.
* Admin-only authorization on sync — already allows any authenticated role
  (`SyncEndpoints.cs:37-40,110-113`).
* `SyncEndpoints._logger` "never injected" — the logger **is** DI-injected as a handler
  parameter; static endpoint classes are the existing convention.

---

## 2. Toolchain preflight (required before any C# stage)

```powershell
dotnet --list-sdks          # must print 8.0.x  (currently: empty → blocked)
docker --version            # currently: not installed
python --version            # available
```

Install the .NET 8 SDK (download: <https://dotnet.microsoft.com/download/dotnet/8.0>), then:

```powershell
cd c:\Users\OPENDESK\Desktop\Pharmco
dotnet new sln -n Pharmco                                  # repo currently has NO .sln
dotnet sln add server/Pharmco.Core/Pharmco.Core.csproj server/Pharmco.Api/Pharmco.Api.csproj `
               server/Pharmco.Cli/Pharmco.Cli.csproj server/Pharmco.Tests/Pharmco.Tests.csproj `
               client/Pharmco.Client/Pharmco.Client.csproj client/Pharmco.Client.Tests/Pharmco.Client.Tests.csproj
dotnet restore Pharmco.sln
dotnet build Pharmco.sln -c Debug 2>&1 | Tee-Object baseline-build.txt
```

Integration tests self-skip without a database — **a skip is not a pass**:

```powershell
docker run --rm -d --name pharmco-test-db -e POSTGRES_PASSWORD=test -e POSTGRES_USER=pharmco `
  -e POSTGRES_DB=pharmco_test -p 5433:5432 postgres:16-alpine
$env:PHARMCO_TEST_CONNECTION = "Host=localhost;Port=5433;Database=pharmco_test;Username=pharmco;Password=test"
dotnet test server/Pharmco.Tests/Pharmco.Tests.csproj
dotnet test client/Pharmco.Client.Tests/Pharmco.Client.Tests.csproj   # Windows-only TFM
```

---

## 3. Defect register (remaining work)

Severity: **B**locker, **H**igh, **M**edium, **L**ow. Evidence is cited inline for every item.

### A. Compile blockers — nothing can be verified until these compile

**BUG-016 — B — `Authz.cs:37` invalid null-coalescing.**
`http.Request.Headers["authorization"] ?? ""` → **CS0019** (`StringValues` is a struct).
Fix: `.FirstOrDefault() ?? ""` (matches the fix already applied in `AuthEndpoints.cs`). Also
refresh the stale XML doc at `Authz.cs:23`, which still shows the invalid
`if (var denied = …; …)` form.

**BUG-017 — B — `DarajaService.cs`: three compile errors plus a broken retry.**
* `:7` `using Polly;` — **no `.csproj` references Polly** (checked every project file).
* `:25` `ConcurrentDictionary<…>` — missing `using System.Collections.Concurrent;`.
* `:352` `file sealed class BasicAuthenticationCredential : AuthenticationHeaderValue` — the base
  type is **sealed** (**CS0509**).
* The `HttpRequestMessage` is built outside the retry delegate and re-sent on retry; a sent
  request cannot be re-sent, so retries throw at runtime.
Fix: `new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{key}:{secret}")))`,
and replace Polly with an explicit 3-attempt loop (1s/2s/4s backoff, cancellation-aware, logged)
that builds the request **inside** the delegate — or reference Polly, never both.

**BUG-018 — B — client is still Java (5 files).**
* `AuthService.cs` — `java.net.URI/java.net.http/java.time`, `HttpClient.newBuilder()`,
  `throws AuthError`, `catch (Throwable)`, `.IsEmpty()`, `response.StatusCode()`.
* `CredentialCache.cs` — `using java.sql;`, JDBC `PreparedStatement`/`ResultSet`/`Types.BLOB`,
  `st.setNull(8, Types.BLOB)`, `st.setLong`, `rs.getLong`,
  `DateTimeOffset.now().toEpochSecond()`, `DateTimeOffset.ofEpochSecond(…, ZoneOffset.UTC)`,
  `java.nio.file.Files`, `java.lang.System.getenv`.
* `Json.cs` — `using java.util;`, `LinkedHashMap`, `new ArrayList<…>()`, `private final`,
  `.charAt(…)`, Java `switch { … -> … }`, `raw.Equals(…)`.
* `SecretsBox.cs` — `javax.crypto.Cipher`, `javax.crypto.spec.SecretKeySpec`,
  `SecureRandom.getInstanceStrong()`, `MessageDigest`, `doFinalWithTag()`, `System.arraycopy`,
  `Arrays.copyOfRange`, `implements SecretCipher`, `Integer.toHexString(b, 2)`, and
  `Windows.Data.ProtectedData` (wrong type; the real one is
  `System.Security.Cryptography.ProtectedData`).
* `SessionManager.cs` — `java.util.function`, `java.util.Timer`, `DateTimeOffset.now()`, `.IsEmpty()`.
Fix: `System.Net.Http`, `RandomNumberGenerator.GetBytes`, `AesGcm` (**preserve the existing blob
layout: 12-byte nonce | ciphertext | 16-byte tag**), `SHA256.HashData`, `Encoding.UTF8`,
`System.IO.File/Directory`, `Environment.GetEnvironmentVariable`, `Microsoft.Data.Sqlite`
(already referenced), `System.Threading.Timer`/`PeriodicTimer`. Keep `Json.*` and `SecretsBox.*`
public signatures so `Pharmco.Client.Tests` continues to compile.

**BUG-019 — B — server tests are still Java (5 files).**
`AuthFlowIntegrationTest.cs` (`java.net.http`, `java.nio.charset`, `java.time`,
`java.util.UUID.randomUUID()`, `java.util.Base64.getDecoder()`, `java.net.http.HttpClient`), plus
`JwtServiceTest.cs`, `OfflineGateTest.cs`, `RateLimiterTest.cs`, `SessionLockTest.cs`
(`java.time`, `toEpochSecond`, `ofEpochSecond`, `ZoneOffset.UTC`).
Fix: `Guid.NewGuid()`, `Convert.FromBase64String`, `Encoding.UTF8`,
`DateTimeOffset.FromUnixTimeSeconds`, `System.Net.Http.HttpClient`.
**Assertions must keep their exact current meaning** — they are the regression guard for later stages.

### B. Runtime / data blockers

**BUG-001 — B — migrations `003`–`007` are never applied by any code path.**
Only `db/master/001_master.sql` and `db/tenant/002_tenant_template.sql` are embedded
(`Pharmco.Core.csproj:19-25`, `SchemaInstaller.cs:12`, `TenantProvisioner.cs:38`), and the API
never migrates at startup (`Program.cs` has no migration call). On a fresh database:
`AuthRepository.cs:124` (`master.refresh_tokens`) and `:177-181`
(`audit_logs.username/tenant_code/ip/user_agent/outcome`) fail with **42703** → login and refresh
are broken; `SyncEndpoints.cs:176-217` writes `{schema}.sync_queue`, which only **007** creates →
**42P01**; **005** adds `sales.client_sale_uuid` + `sale_sequences` and widens
`payment_mode`/`status`; **004** adds `products.created_at`; **006** creates
`daraja_callback_log` + `daraja_stk_push_tracking`.
Fix: one ordered, idempotent runner covering `001/003/006` (master) and `002/004/005/007` (tenant,
`{tenant_schema}` substituted), invoked from the CLI bootstrap, the API startup (flag-guarded) and
the test fixture.
Gate: an `[IntegrationFact]` that bootstraps a clean DB, logs in, and pushes a sale.

**BUG-002 — B — sync inserts omit `tenant_id`, which is `NOT NULL`.**
`sales`, `sale_items` and `stock_moves` all declare `tenant_id uuid NOT NULL`
(`db/tenant/002_tenant_template.sql:48,74,89`); the inserts omit it
(`SyncEndpoints.cs:312-317`, `:335-338`, `:353-356`) → **23502** on every synced sale.
Fix: supply `tenant_id` from the JWT claim — never from the payload — in all three inserts.

**BUG-003 — B — `payment_mode` / `status` violate the schema.**
`SyncEndpoints.cs:326` uses `sale.PaymentMode.ToString().ToLowerInvariant()` → `mpesaonline` /
`mpesamanual`, but the CHECK allows only `cash|mpesa_online|mpesa_manual`
(`db/tenant/005_tenant_sales.sql:49-50`) → **23514** for any non-cash sale. Line `:317` hard-codes
`status = 'completed'`, so `pending_verification` is never written even though
`PendingVerificationJob.cs:73,106` scans for exactly that.
Fix: explicit wire mapping (mirror `UserRoles.Parse`/`ToWireString`); `mpesa_manual` without a
confirmed receipt → `pending_verification`.

**BUG-004 — H — every real Safaricom callback is dropped.**
`MpesaCallbackEndpoint.cs:61-66` with `ExtractShortCode` (`:142-151`) requires a `ShortCode` value
inside `CallbackMetadata`. STK callbacks carry Amount, MpesaReceiptNumber, TransactionDate and
PhoneNumber — **not** ShortCode — so real callbacks return early and no sale is ever marked paid.
Fix: resolve the tenant from `master.daraja_stk_push_tracking` by `checkout_request_id`; an unknown
id → audit + accepted envelope, and never touch a sale. Keep the
`daraja_callback_log.checkout_request_id` UNIQUE idempotency guard (`006_daraja.sql:31`).

**BUG-005 — H — STK Push never records the tracking row.**
`DarajaService.PushStkAsync` (`:96-170`) returns the CheckoutRequestID but performs no INSERT into
`master.daraja_stk_push_tracking` (`006_daraja.sql:43-56`), so the authoritative
CheckoutRequestID → (tenant, invoice) mapping is never persisted anywhere.
Fix: insert the tracking row on initiation and update its status on callback/query.

**BUG-006 — H — device identity is unusable and spoofable.**
The client sends `Environment.MachineName + "-" + Environment.UserName.GetHashCode()`
(`LocalDatabase.cs:417`, `SyncEngine.cs:53`) — not a GUID, and .NET Core randomizes string hashing
per process, so it changes on every run. The server's `Guid.TryParse(op.DeviceId, …)`
(`SyncEndpoints.cs:309`) therefore yields `Guid.Empty`, and the client-supplied value is trusted.
Fix: generate and persist a stable GUID on the client; register/bind it to the tenant server-side
(migration); put `device_id` in the JWT; sync uses the **claim**; a claim/payload mismatch → 403 +
audit row.

**BUG-007 — H — pending verification uses the wrong id and can complete an unpaid sale.**
`PendingVerificationJob.cs:157-175` passes `sale.ClientSaleUuid` as the CheckoutRequestID and treats
`ResponseCode == "0"` as payment success; `TransactionStatusResponse` (`DarajaDtos.cs:97-103`) has
no `ResultCode` field at all.
Fix: resolve the real checkout id from the tracking table; require `ResultCode == 0` **and** a
non-empty `MpesaReceiptNumber` before completing; keep the 24h → `unverified` rule.

### C. Security, tenancy, scalability, operations

**BUG-008 — H — unvalidated identifier interpolation.**
`NpgsqlConnectionFactory.SetSearchPathAsync` (`:52`) quotes whatever it is handed and never
validates it; `PendingVerificationJob.cs:69-77` builds SQL with `'{t.TenantId}'::uuid` and
`"{t.SchemaName}"`; `SyncEndpoints.QuoteIdentifier` (`:475-481`) is a private duplicate of the
idea; `TenantProvisioner.cs:137` interpolates a schema name into `DROP SCHEMA`.
Fix: one shared quoter in `Pharmco.Core` that validates `^[a-z_][a-z0-9_]*$`, rejects `"` outright,
and fails closed; use it everywhere. `master.tenants.schema_name` already has a CHECK
(`db/master/001_master.sql:34-35`), but the helper must not depend on callers being trustworthy.

**BUG-009 — H — pending verification does not scale.**
The job builds a `UNION ALL` of `EXISTS` probes for **every** active tenant on every 15-minute poll
(`PendingVerificationJob.cs:58-77`), with no batch cap and no claim/lease, so multiple API replicas
would double-process.
Fix: query only bounded pending rows from the master-level tracking table (add any missing index via
migration), claim with `FOR UPDATE SKIP LOCKED`, cap the batch, add jitter, and keep it
multi-replica safe.

**BUG-010 — M — duplicate and divergent schema definitions.**
`db/master/001_master.sql` creates the `master.*` objects, while `db/master/001_master_schema.sql`
creates **unqualified** `tenants`, `users`, `refresh_tokens` and `tenant_events` (which land in
`public`). `deploy/docker-compose.yml:48` mounts that whole directory into
`/docker-entrypoint-initdb.d`, so initdb executes both on first boot. Tenant schemas diverge too: the
C# provisioner applies `db/tenant/002_tenant_template.sql`, whereas
`db/scripts/apply_tenant_schema.sh:19` applies `db/tenant-template/001_tenant_tables.sql` (the only
place that defines a `devices` table).
Fix: settle on one master source and one tenant template; fold any genuinely required objects into
numbered migrations; stop mounting the whole directory for initdb.

**BUG-011 — H — unsafe default secrets and no production guard.**
`appsettings.json:6,7,10` ship `dev-only-secret-change-me`, `dev-only-admin-key-change-me` and
`dev-only-encryption-key-change-me`; `Program.cs:17-20` requires only `ConnectionStrings:Master` and
`Jwt:Secret` and never rejects defaults; `appsettings.json:13` uses the invalid level `"INFO"`
(the valid value is `Information`).
Fix: strongly typed options validated at startup; in Production reject missing, default or short
(< 32 bytes) secrets with a clear, secret-free startup error; allow explicit test values in
Development/Test; never log secret material.

**BUG-012 — H — deployment environment contract is inconsistent.**
`deploy/docker-compose.yml:24` requires `${ADMIN_KEY}` while `deploy/.env.example:14` defines
`JWT_ADMIN_KEY`, so `docker compose up` fails for anyone following the docs. No C# code reads
`Jwt:AdminKey` and no `/v1/admin/*` surface exists, yet `README.md:43`, `docs/api-contract.md:27`
and `cli/pharmco_cli/client.py:40` all reference the `X-Admin-Key` header. The auth tuning variables
exist only as comments in `.env.example` and are never passed into the container.
Fix: make compose, `.env.example`, docs and the Python CLI agree with the real JWT-admin-role model
(or actually implement the admin-key surface); pass the tuning variables through.

**BUG-013 — H — rate limiting is per-process while the deployment runs Redis.**
`FailureRateLimiter` is in-memory (one table per process); `deploy/docker-compose.yml:55-65` starts
`redis:7` with AOF and a healthcheck, but the API receives no Redis connection string and no Redis
client package is referenced by any project. Restarts therefore reset the counters and replicas do
not share them.
Fix: Redis-backed sliding window/counter with atomic operations and TTLs, keyed
`login:<pharmacyCode>:<lowercased username>` (matching today's `KeyFor`), plus the existing
in-memory implementation as a dev/test fallback behind the same interface with a logged warning.

**BUG-014 — M — `/health` verifies nothing.**
`Health.cs:6-12` always returns `ok`, yet `docker-compose.yml:33-37` and the Uptime Kuma probe depend
on it.
Fix: cheap database probe (plus Redis when configured), 503 on dependency failure, keeping the
existing response shape.

**BUG-015 — M — hard-coded licensing limits.**
`SyncEndpoints.cs:463-467` carries a TODO with `max_users = 10` and `max_terminals = 5`, directly
beneath a comment claiming limits are provisioned per tenant via the CLI.
Fix: make the authoritative source real (a per-tenant column/table added by migration, or the signed
license claims); remove the magic numbers unless they are genuinely configured defaults.

### D. Smaller / hygiene items

**BUG-020 — L — unreachable code in `AuthEndpoints.Session`.**
`AuthEndpoints.cs:221` still ends with `await Task.CompletedTask; // satisfy async signature` after a
`return` (CS0162), and the method does not need to be `async` at all.

**BUG-021 — M — no solution file.** There is no `.sln` and no `global.json`, so `dotnet build` /
`dotnet test` from the repository root cannot work. Fix: add `Pharmco.sln` (see §2) and document the
per-project commands.

**BUG-022 — L — line-ending churn.** `.editorconfig:5` sets `end_of_line = lf`, but git's effective
`core.autocrlf=true` rewrites to CRLF on checkout, so every `git add` prints warnings. Fix: add a
`.gitattributes` containing `* text=auto eol=lf` (or set `core.autocrlf=false` in this repo), then
renormalize once.

**BUG-023 — M — compose depends on a path that may not exist.** `docker-compose.yml:79` mounts
`../client-publish` (gitignored) into Caddy, so `docker compose up` fails unless the client has been
published first. Fix: document the publish step and make the mount optional.

**BUG-024 — M — sync payloads are trusted without validation.**
`ProcessSaleOperationAsync` / `ProcessProductOperationAsync` / `ProcessStockMoveOperationAsync`
insert client-supplied product ids, quantities, prices, `reason` values and timestamps without
validation (`SyncEndpoints.cs:222-395`); the standalone stock-move path even takes `created_at`
straight from the payload.
Fix: validate product existence and active state, `qty > 0`, non-negative money, `reason` ∈ the
`stock_moves` CHECK set, invoice format and no future timestamps; return a per-operation error result
instead of throwing.

**BUG-025 — L — CLI test discovery is broken as documented.**
`python -m unittest discover -s tests -t .` fails with
`ImportError: Start directory is not importable: '…\cli\tests'` because `cli/tests/` has no
`__init__.py`. Verified working forms:
`python -m unittest tests.test_client tests.test_license -v`, or
`$env:PYTHONPATH=<cli path>; python -m unittest discover -s tests -p 'test_*.py' -t tests -v`.
Fix: add `cli/tests/__init__.py` (or document the working invocation). Note also that pytest is
**not** a declared dependency — `cli/requirements.txt` lists only `cryptography>=42.0`, and the tests
are `unittest`-based.

---

## 4. Execution stages

Ordering rationale: compile blockers first (nothing can be verified before the code compiles), then
the migration gap (login and sync are dead without it), then data integrity and security, then
scale and operations. Every stage ends with an **observed** gate and exactly one commit named
`stage-N: <summary> (BUG-0xx)`.

| Stage | Work | Gate that must actually be observed |
|---|---|---|
| **S0** | Add `Pharmco.sln` (BUG-021); capture the baseline | `dotnet build Pharmco.sln -c Debug 2>&1 \| Tee-Object baseline-build.txt` |
| **S1** | Compile blockers: BUG-016, BUG-017, BUG-018, BUG-019 | `dotnet build Pharmco.sln -c Debug` → **0 errors** (client projects require Windows TFM) |
| **S2** | Test baseline, with a real database started | `dotnet test` on both test projects; record pass/fail/**skipped** counts (a skip is not a pass) |
| **S3** | Migration runner + schema de-duplication: BUG-001, BUG-010 | fresh DB → CLI bootstrap → `/api/auth/login` succeeds and writes an audit row; `/api/sync/push` returns 200 |
| **S4** | Sync data integrity: BUG-002, BUG-003, BUG-024 | sale sync inserts succeed; duplicate push produces exactly one sale, one item set, one stock-move set; non-cash modes accepted |
| **S5** | Device identity: BUG-006 | same device keeps its id across restarts; spoofed/foreign `device_id` → 403 + audit row |
| **S6** | M-Pesa correctness: BUG-004, BUG-005, BUG-007 | recorded callback fixture **without** ShortCode marks the correct sale paid; duplicate callback is a no-op; query-accepted-without-receipt stays `pending_verification`; >24h → `unverified` |
| **S7** | Tenant isolation + identifier quoting: BUG-008 | malicious `schema_name` values rejected; Tenant A cannot read/write Tenant B; cashier can sync, non-admin cannot manage users |
| **S8** | Secrets, config, rate limiting, health: BUG-011, BUG-012, BUG-013, BUG-014 | Production start with default/blank secrets **refuses to start** with a clear error; rate-limit counters are shared across two API instances; `/health` returns 503 when the DB is unreachable |
| **S9** | Pending-verification scalability: BUG-009 | one bounded query per cycle instead of one per tenant; two concurrent job instances do not double-apply |
| **S10** | Licensing + hygiene: BUG-015, BUG-020, BUG-022, BUG-023 | limits come from the authoritative source; no unreachable code; `docker compose up` works without a pre-existing `client-publish` |
| **S11** | Docs + end-to-end compose | `docker compose build && docker compose up` succeeds using only documented `.env` values; healthchecks pass |

### 4.1 Rules that apply to every stage

1. **Reproduce first.** For each defect, produce the failing evidence (error code, stack trace, or a
   written scenario for security items) *before* changing code. If it cannot be reproduced, do not
   "fix" it — record it as not reproduced.
2. **Never edit a test to make it pass.** Add a test for every fix; existing assertions keep their
   meaning. Tests are the regression guard between stages.
3. **Do not touch §5 (non-issues).** Those were verified correct in this repository.
4. **One stage, one commit.** If a stage cannot go green, revert it and report — do not stack broken
   work.
5. **No new infrastructure** beyond Redis, which `deploy/docker-compose.yml` already runs.
6. **Every schema change ships as a numbered, idempotent migration**, with evidence that a clean
   database can be built from scratch and that an existing one migrates.
7. **Never log secrets or password material**; client-facing errors stay generic while details are
   logged server-side.
8. **Honesty over green checkmarks.** Do not claim a stage passed without the literal command output.
   If Safaricom, Redis, Docker or DNS is unavailable, mark the item **NOT VERIFIED**, state what was
   substituted (fixtures/mocks) and what therefore remains unproven.

### 4.2 Verification available on this machine today (no .NET SDK)

The server/client cannot be compiled here, but the Python CLI can be exercised:

```powershell
cd cli
python -m unittest discover -s tests -t . -v      # unittest (not pytest); cryptography 49.0.0 present
```

Observed result is recorded in §6.

---

## 5. Non-issues — verified correct in this repository; do not "fix"

Item 1.2 lists the audit claims that do not apply here. In addition, the following were read and
verified; they must survive the repair unchanged in behaviour:

* **`JwtService.cs`** — hand-rolled HS256 issue/verify with issuer, audience, `exp`/`nbf`/`iat`
  validation, an instance `_secret` field, `RandomNumberGenerator`-backed refresh tokens and
  SHA-256 token hashing. Do not rewrite or weaken it.
* **`RateLimiter.cs` concurrency** — `ConcurrentDictionary` + `Queue<DateTimeOffset>` + per-key locks
  is correct for a single process. BUG-013 adds a Redis implementation behind the *same* behaviour;
  it must not change the in-memory semantics the unit tests already assert.
* **`PasswordService.cs`** — bcrypt cost 12 with `string.IsNullOrEmpty` guards. No downgrade.
* **`AuthEndpoints.Login`** — returns a generic `invalid_credentials` for both unknown tenant and bad
  password (no user enumeration), records the failure in the limiter before responding, and resolves
  the client IP via `x-forwarded-for` first hop then `Connection.RemoteIpAddress`. Keep the response
  shape and the audit writes.
* **`UserEndpoints.Create`** — the duplicate-username pre-check already returns **409 Conflict**
  without leaking the PostgreSQL error. Keep it, and *add* a race-safe `23505` → 409 translation on
  top (an addition, not a rewrite).
* **`MpesaCallbackEndpoint` idempotency** — `master.daraja_callback_log.checkout_request_id` UNIQUE
  plus the pre-check is the correct anchor (`006_daraja.sql:31`); keep it and build BUG-004/005
  around it.
* **`ProcessSaleOperationAsync`** — `client_sale_uuid` de-duplication and the append-only rule (sync
  never updates or deletes a sale) are correct.
* **`sales.tenant_id`** — the column is `NOT NULL` with `UNIQUE (tenant_id, invoice_no)`
  (`db/tenant/002_tenant_template.sql:48,62`). The audit's instruction to *remove* a `tenant_id`
  filter is wrong; adding one is allowed hardening.
* **`Authz.RequireAdmin`** — the 2-line handler pattern used everywhere is correct; only the stale
  XML doc at `Authz.cs:23` needs updating.
* **Route authorization in `Program.cs`** — logout/session/sync require authentication, Daraja config
  and `/api/users/*` require admin, and sync deliberately allows any authenticated role (cashiers
  must be able to push sales). This is the intended model.

---

## 6. Verification log

| When | Check | Command | Observed result |
|---|---|---|---|
| 2026-09-25 | Toolchain | `dotnet --list-sdks`, `docker --version`, `python --version` | `.NET SDK: none` (`C:\Program Files\dotnet\sdk` empty), `Docker: not installed`, `winget: absent`, `Python 3.12.3` |
| 2026-09-25 | Repo state | `git status --short` | branch `repair/audit-v2`; 13 repaired server files committed as `c309131`; working tree clean |
| 2026-09-25 | Java scan at HEAD | `git grep -n -E 'java\.\|javax\.\|DateTimeOffset\.now(\|\.IsEmpty(\|toEpochSecond\|…' HEAD -- server client` | confirms the audit's contamination **at HEAD** (client + server tests, plus the server files that the worktree already repaired) |
| 2026-09-25 | Java scan in worktree | `Select-String` across all 76 `.cs` files | server `Api`/`Core` clean; `client/Pharmco.Client/src/Services/{AuthService,CredentialCache,Json,SecretsBox,SessionManager}.cs` and 5 `Pharmco.Tests` files still contaminated → BUG-018/BUG-019 |
| 2026-09-25 | **Python CLI tests** | `cd cli; python -m unittest tests.test_client tests.test_license -v` | **`Ran 7 tests in 1.001s — OK`** (7 passed, 0 failed) |
| 2026-09-25 | Static scan of the 13 repaired files | `Select-String` residual-pattern scan | found `Authz.cs:37` (`StringValues ?? ""`, CS0019) and confirmed the `DarajaService.cs` blockers → BUG-016/BUG-017 |

Verified CLI invocation (use this, not the README's implied `pytest`):

```powershell
cd cli
python -m unittest tests.test_client tests.test_license -v
# equivalent form
$env:PYTHONPATH = (Get-Location).Path
python -m unittest discover -s tests -p 'test_*.py' -t tests -v
```

`python -m unittest discover -s tests -t .` **fails** (`Start directory is not importable`) — see
BUG-025.

### 6.1 NOT verified (and why)

* `dotnet restore/build/test` for all six C# projects — **no .NET SDK on this machine**.
* `docker compose build` / `up`, Redis-backed limiting, healthchecks, Caddy — **Docker not installed**.
* Any live Safaricom Daraja call (STK Push, callback, transaction status) — no credentials. These
  must be verified with recorded callback fixtures and a stubbed `HttpMessageHandler`, and reported
  as fixture-verified rather than live-verified.
* The Windows-only client (`net8.0-windows`, WPF) build and run — needs a Windows .NET SDK.

---

## 7. How to resume

```powershell
cd c:\Users\OPENDESK\Desktop\Pharmco
git log --oneline -3        # c309131 = preserved repair, on branch repair/audit-v2
git status                  # expected: clean
```

1. Install the .NET 8 SDK (see §2), then run S0 → S11 from §4, one commit per stage.
2. `main` is still at `bc86604` and untouched; merge or rebase `repair/audit-v2` once S1+ are green.
3. The preserved commit's author is `Cline (repair session) <cline-repair@localhost>` because git had
   no identity configured. To re-attribute it to yourself:

```powershell
git config user.name  "Your Name"
git config user.email "you@example.com"
git commit --amend --reset-author --no-edit
```

### 7.1 Known risks to watch while fixing

* The preserved repair is **unbuilt**; expect further compile errors beyond BUG-016/017 (they were
  found by static scan, not by a compiler).
* `SyncEndpoints.ProcessOperationAsync` shares a single transaction across all operations in one push
  request, so a mid-request failure rolls back earlier operations in the same request. Preserve that
  contract deliberately (or change it consciously) — do not let it drift while fixing BUG-002/003.
* Rate-limit key shape (`login:<pharmacyCode>:<lowercased username>`) is asserted by unit tests;
  the Redis implementation must keep it byte-for-byte.

<!-- END -->
