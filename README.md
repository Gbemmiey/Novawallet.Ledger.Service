# NovaWallet Ledger Service

A high-concurrency, double-entry wallet ledger for FirstBank NovaPay, built with C# / .NET 8. It handles P2P wallet transfers, asynchronous NIP deposit clearing, idempotency, daily limits and audit logging. Money is stored as integer kobo (`long`), so there is no floating-point risk.

**Stack:** .NET 8 Web API · PostgreSQL 16 · Redis 8 · OpenTelemetry Collector + OpenObserve + Prometheus · RFC 7807 errors · JWT auth (mock issuer)

## Key design decisions

- **The database guarantees the money.** All balance changes and ledger postings run in one atomic PostgreSQL transaction. Redis is only a `HybridCache` fast-path for redelivered deposit webhooks and is not on the transfer path.
- **Overdraft and deadlock safety.** Debits use a guarded atomic `UPDATE ... WHERE AvailableBalanceKobo >= @Amount`, so there are no optimistic-concurrency retry storms. Transfers lock wallet IDs in `min(A,B) → max(A,B)` order.
- **Idempotency is decided by the database alone.**
    - Every transfer is looked up by `Idempotency-Key` in `WalletTransfers`.
    - The same key with the same payload hash replays the stored outcome, and the same key with a different payload returns `409` (code `26`).
    - The unique constraint on the key settles concurrent races.
    - The hash includes the caller's resolved source wallet, so another user's key never leaks anything.
    - Rejected transfers (codes `51`, `61`, `57`, `30`) are stored as `Failed` rows and replay as failures. Transient `96` errors are not stored, so they stay retryable.
- **O(1) daily limit.** The ₦500,000/day limit is enforced by a guarded `UPSERT` on a materialized `WalletDailyUsage` table. The guard covers both the insert and update branches, and the date is anchored to `Africa/Lagos` (WAT) rather than the database session's timezone.
- **Product vs. ledger separation.**
    - `Wallet` and `WalletTransfer` hold mutable, product-facing state.
    - `Account`, `JournalEntry` and `AccountEntry` are the immutable double-entry trail, with a positive-amount `CHECK` and no UPDATE/DELETE grants.
    - `AuditLog` is a separate insert-only compliance record.
- **Source wallet comes from the JWT.** The transfer body has no `sourceWalletId`, and sending one is rejected. Transfers to yourself or between currencies are also rejected.

## Flows

1. **Inbound NIP deposit.**
    - `POST /wallets/credit` is anonymous, being a switch callback.
    - It validates, stores an `ExternalCreditRequest` and a `DepositOutbox` row, and returns `202` immediately.
    - `DepositConsumer` then credits the wallet with a single guarded atomic `UPDATE` (no application-held wallet lock), posts Debit `1000-NIP-SETTLEMENT` / Credit `2100-USER-WALLET`, and marks the session settled in one transaction. It only locks the `DepositOutbox` row, to dedupe across worker instances.
    - Delivery is at-least-once with exactly-once effect, enforced by the `SessionId` and `IdempotencyKey` unique constraints.
2. **P2P transfer.**
    - Resolve the caller's wallet from the JWT and do the idempotency lookup.
    - Run business validation, then one transaction: lock ordering, daily-usage upsert, debit, credit, journal posting, and a `TransferOutbox` event.

## Reconciliation

`ReconciliationWorker` sweeps all wallets with a keyset cursor and checks `Wallet.AvailableBalanceKobo == Σ credits − Σ debits`.

- **Incremental.** Each check is `last snapshot watermark balance + entries newer than the watermark`, so per-tick cost doesn't grow with account history.
- **Consistent read.** It uses a single SQL statement, so there are no read-skew false positives.
- **Grace-period watermark** (default 60s). The stored watermark only advances past entries older than the grace period. This stops slow, out-of-order commits from being skipped forever.
- **Auto-freeze.** An unbalanced `Active` wallet is frozen and audited in the same transaction as its snapshot. A config kill-switch (`AutoFreezeOnDiscrepancy`) can turn this off.
- **Recovery.** Admins restore wallets via `PATCH /admin/wallets/{id}/status`. `Closed` is terminal, and status changes use an atomic compare-and-swap.

## API surface

| Endpoint | Auth |
|---|---|
| `POST /auth/login` (mock JWT, role `Customer` or `Admin`) | Anonymous |
| Wallet create / fetch | JWT |
| `GET /wallets/statement` (paginated, newest first, date filters) | JWT |
| `POST /wallets/transfer` (`Idempotency-Key` required) | JWT |
| `GET /wallets/transfer/{idempotencyKey}` (requery) | JWT, own transfers only |
| `POST /wallets/credit` | Anonymous (NIP callback) |
| `GET /wallets/credit/{sessionId}` (requery) | Anonymous |
| `GET /admin/audit-logs`, `PATCH /admin/wallets/{id}/status` | `AdminOnly` |

Error bodies use NIP-style `responseCode` values (`00`, `25`, `26`, `30`, `51`, `57`, `61`, `63`, `65`, `94`, `96`), mapped separately to HTTP statuses. Rate limits are covered in the next section.

## Rate limiting

Requests are rate limited per endpoint group with a sliding window: login and the NIP credit callback by client IP, wallet routes and transfers by JWT `sub`. Limits are configurable through the `RateLimiting` config section (`RATELIMIT_*` env vars), and a rejected request returns `429` with a `Retry-After` header. The compose file raises the transfer limit so the overdraw load test can send bursts.

## Observability

- **Metrics.** Custom `NovaWalletMetrics` cover deposits, transfers, reconciliation and rate limiting. Tags come from small fixed vocabularies to avoid cardinality blow-ups.
- **Tracing.** The webhook's W3C `traceparent` is stored in `DepositOutbox.TraceParent`, so the webhook and the later settlement appear as one trace (`deposit.accept` → `deposit.settle`).
- **Where to look.** OpenObserve is at `http://localhost:5080` and Prometheus scrapes `/metrics`.

## Running it

**Prerequisites:** Docker Engine 24+ with Docker Compose v2+. The .NET 8 SDK is only needed to run the tests locally.

```bash
cp .env.example .env   # optional, a dev .env ships in the repo
docker compose up --build
```

Postgres, Redis, a one-shot `migrator` service, the API, the OTel Collector and OpenObserve all start with this one command.

- **Migrations.** The `migrator` service runs the API image with `--migrate-only`, applies all pending EF Core migrations and exits. The API waits for it to finish, so the schema is always current before the API starts. For a plain `dotnet run` against a fresh local database, `Startup:ApplyMigrationsOnStartup` is enabled in `appsettings.Development.json`.
- **Endpoints:** Swagger at `http://localhost:5000/swagger`, health at `/health`, metrics at `/metrics`.
- **Ports:** every host port is overridable in `.env`.
- **Memory:** every service has a memory cap in `docker-compose.yml`. If a container exits with code 137 it was OOM-killed, so raise its `limits.memory`.
- **OpenObserve password:** `ZO_ROOT_USER_PASSWORD` must be strong (8-128 characters with lowercase, uppercase, a digit and a special character), or the container won't start. The shipped `.env` already meets this.

## Testing

```bash
dotnet test --configuration Release
```

There are two xUnit projects targeting `net8.0`:

- **Unit tests** need no Docker.
- **Integration tests** run against a real PostgreSQL 16 via Testcontainers, so Docker must be running. They avoid SQLite and the EF in-memory provider because the money code depends on `ON CONFLICT`, `RETURNING`, row locks and `CHECK`/unique constraints. All migrations are applied to the test database first, so the migration chain is exercised too.

Highlighted tests:

- **50-request overdraw.** 50 concurrent ₦500 transfers against a ₦10,000 balance give exactly 20 successes, 30 rejections with code `51`, and a final balance of 0.
- **Idempotent replay.** A replay returns a byte-identical body and debits once.
- **Key reuse.** The same key with a different payload returns `409`.

Coverage also includes deadlock checks (opposite-direction transfers), reconciliation and auto-freeze, rate limits, and database constraint backstops.

## Postman collection & JMeter load test

Two extra assets ship in the repo for exercising the running stack (`docker compose up --build`) beyond the automated suite:

- **Postman collection.** `Docs/NovaWallet.Api v1.0.postman_collection.json` covers the API surface above (mock login, wallet create/fetch, statement, transfer, requery, the NIP credit callback and the admin routes), for exploring and demoing the service by hand.
- **JMeter overdraw test.** `loadtests/Jmeter/overdraw` is a load-test plan for the overdraw scenario. It complements the in-suite 50-request overdraw test by running the same kind of contention against the real Docker stack, where you can scale the load up.

For heavy load runs, keep the [rate limits](#rate-limiting) in mind. `docker-compose.yml` already raises the transfer limit to 1000 and leaves credit unlimited, and both can be raised further through the `RATELIMIT_*` variables in `.env`.

## Known limitations

- There is no real identity provider. `POST /auth/login` is a mock, and the `role` claim in its JWT is trusted as-is.
- The reconciliation watermark assumes no posting transaction outlives the grace period, and that clocks across API instances don't skew by more than it.

## Further notes

See `AI_USAGE.md` for how AI assistance was used and what was corrected during review.