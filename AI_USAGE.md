# AI Usage

How AI was used on NovaWallet.Ledger.Service, where it got things wrong, and how I checked it.

## Tools

| Tool | Used for |
|---|---|
| Cursor (Claude 3.5 Sonnet / GPT-4o) | Early exploration: schema sketches, comparing distributed locks vs database concurrency control, idempotency design |
| Cline (Claude Sonnet), Plan → Act | Most of the implementation: services, workers, EF Core configuration, validators, Docker Compose, tests, the JMeter overdraw plan |
| Claude (claude.ai) | Gap review of the README/DDL against the brief, entity-model refactor, the system-design deck |

## How I worked

- **Plan → Act.** Every non-trivial change was proposed as a written plan (files, SQL, trade-offs) and approved before any code was written.
- **I read all AI-written code before accepting it.** `dotnet build` was unavailable in the agent session for most of the build, so review was by careful reading. The stack was then run with Docker Compose and checked with the JMeter overdraw plan.
- **Cross-path reviews.** After features I asked the AI to compare code paths that should behave alike. Most bugs here showed up as asymmetries between two paths, not as one bad line.

## Example prompts

1. **Locking.** *"I need to handle 50+ concurrent transfers on the same wallet. Redis distributed locks or PostgreSQL row locks with conditional updates?"*
   → Atomic `UPDATE … WHERE Balance >= @Amount` plus min→max wallet-ID lock ordering. Adopted: no lock-expiry or crash-recovery failure modes to manage.
2. **Idempotency.** *"Write middleware enforcing idempotency on POST /wallets/transfers with a Redis fast-path and PostgreSQL backstop with request body hashing."*
   → Redis `SET NX EX 30` plus a DB lookup by key and SHA-256 payload hash (same hash = replay, different = 409). The DB was always authoritative. Later I simplified the transfer path to rely on the DB lookup and unique constraint alone.
3. **Cross-path review.** *"Is there any gap in transfer vs deposit?"*
   → `DepositConsumer` permanently failed any zero-row balance update with no retry, while `TransferService` re-checked the wallet status and treated an unexplained miss as a retryable anomaly. Fixed so the consumer uses its existing retry path.
4. **Hidden coupling.** *"Is there a better way to handle migrations on start up rather than running the api in development mode?"*
   → Tracing it showed migrations only ran under `IsDevelopment()`, and Compose ran the API in Development just to trigger that. The same flag was also the only thing enabling the Serilog console sink, so container logs were silently tied to it. Result: a one-shot `migrator` service reuses the API image with a `--migrate-only` mode (same migration code the API used), the API now runs as Production, and console logging has its own setting.

## Where AI output was wrong or unsafe

1. **Daily limit bypass.** The `INSERT` branch of the `WalletDailyUsage` upsert had no limit guard, so a wallet's first transfer of the day skipped the ₦500,000 cap. The `UPDATE` branch was correct, so the feature looked done, and tests that seed a usage row never reach the `INSERT` path. Found by tracing both `ON CONFLICT` branches. Fix: `INSERT … SELECT … WHERE @Amount <= @Limit`.
2. **Unbounded reconciliation.** The first reconciliation query re-summed every `AccountEntries` row for every wallet on every sweep, an O(N) scan competing with transfer writes. Replaced with last snapshot plus new entries. Reworking it exposed more problems:
    - The AI's own SQL used `MAX(uuid)` (doesn't exist in Postgres) and `SUM(bigint)` (returns `numeric`).
    - Concurrent postings can commit out of ID order and be missed. This is handled with a grace-period watermark. When I suggested DB-generated IDs, the AI correctly pushed back: IDs are assigned before commit, so they don't fix commit ordering.
    - A snapshot row was written on every idle tick. Now written only on change or a periodic heartbeat.
3. **Overdraw test that would pass for the wrong reason.** The transfer rate limit was hardcoded to 20 per minute per user, so 30 of 50 requests would have got 429 before reaching the balance guard. "20 succeeded" would have looked right by accident. Limits are now configurable, and the plan fails on any 429. A 422 only counts if the body says "Insufficient balance", because the daily-limit failure also returns 422.
4. **Load plans that hid failures.** JMeter's bundled Groovy rejected the newest JDK, so no requests were ever sent while the report showed 0 errors. Separately, expected 4xx responses were marked failed because the assertions didn't ignore the HTTP status, and the AI's first analysis blamed the API for it. All scripting was replaced with built-in functions and the assertions were fixed.
5. **Instructions also needed correcting.** I first asked for `/credit` to be admin-only. The AI noted the service has no dependency on caller identity; I then corrected the request to anonymous, since it is an external NIP callback.

## Verification and limits

- **Overdraw plan** (50 concurrent ₦500 debits against ₦10,000): 20 accepted, 30 rejected with "Insufficient balance", sender 0, receiver 1,000,000 kobo. The result was the same on a warm re-run.
- **Tests.** The xUnit unit tests and the Testcontainers integration tests on real PostgreSQL were written with AI assistance. The agent session could not compile or run them.
- **Clean Architecture split.** The single `NovaWallet.Api` project was split into `NovaWallet.Domain`, `NovaWallet.Application`, `NovaWallet.Infrastructure` and a slimmed-down `NovaWallet.Api`, moving roughly 90 files and rewriting every namespace and `using` by hand (bulk `sed` was refused by the same approval gate that blocked `dotnet build` all session, so this was done file-by-file with `Read`/`Edit`). Two new abstractions came out of it: `IApplicationDbContext` (so services depend on the DB's shape, not on Npgsql) and `IUniqueConstraintViolationDetector` (collapsing four copy-pasted `IsUniqueViolation` methods into one Postgres-aware implementation). The EF migration history was kept: only the `using`/namespace lines and the live model snapshot's entity-name strings were updated, not each historical migration's own model dump, since those are point-in-time records `dotnet ef` never re-reads. None of this was compiled - `dotnet build` was refused throughout. Run it yourself before trusting it: `dotnet build`, then `docker compose up --build` so `migrator` proves the migration chain still applies.
- **Review rule.** Every AI change to a balance-affecting path got a second pass asking: what about the other branch, the other code path, and real data volume?