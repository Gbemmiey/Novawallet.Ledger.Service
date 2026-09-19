# FirstBank NovaWallet Ledger Service

A high-concurrency, double-entry wallet ledger service built for **FirstBank NovaPay** using C# and .NET 9. This service handles high-frequency inter-wallet transfers, NIP inbound deposit processing, idempotency, daily limit enforcement, and financial audit logging without floating-point precision issues or race conditions.

---

## Architectural Principles & Core Decisions

### 1. Redis Coordinates, Database Guarantees Money
* **Redis** backs the deposit webhook's `HybridCache` fast-path for redelivered NIP callbacks. It is not on the transfer path.
* **Database (PostgreSQL)** is the single source of truth. All balance mutations and double-entry postings execute within an atomic DB transaction protected by deterministic lock ordering and unique constraints.
* Transfer idempotency is decided by the DB alone: the lookup on `WalletTransfers.IdempotencyKey` determines whether a request is a replay, a conflict, or new (see §5 below).

### 2. Inbound NIP Deposit Architecture (Asynchronous Clearing)
Inbound NIP deposits arrive via API callbacks from NIBSS rails carrying a 30-digit `SessionId`.
* **Instant API Acknowledgment:** To prevent NIP timeout errors, the webhook validates the beneficiary account, records the raw `ExternalCreditRequest`, enqueues a `DepositOutbox` event, and returns HTTP 202 immediately.
* **Idempotent Inflow Processing:** The `DepositConsumer` picks up the outbox message (locking only the `DepositOutbox` row itself, to dedupe across worker instances), credits `AvailableBalanceKobo` via a single atomic, guarded SQL `UPDATE` with no application-held wallet row lock (see §4), posts the double-entry ledger pair (Debiting `1000-NIP-SETTLEMENT` Asset, Crediting `2100-USER-WALLET` Liability), and marks the NIP Session ID as settled in a single atomic transaction.

### 3. Separation of Concerns: Product vs. Ledger Domain
* **`Wallet` (Product Domain):** Holds mutable state (`AvailableBalanceKobo`), wallet status, and user-facing attributes. Features an $O(1)$ fast-access balance guard against overdrafts.
* **`Account` (Ledger Domain):** Tracks immutable financial records (`AccountEntries`). Its balance is a derived aggregate ($\sum \text{Credits} - \sum \text{Debits}$) serving as the legal audit trail.
* **`WalletTransfer` (Product Domain):** A queryable, wallet-keyed record of each completed transfer (`SourceWalletId`, `DestinationWalletId`, `Narration`, `PaymentReference`, `TransactionDate`), written in the same transaction as its `JournalEntry`/`AccountEntry` pair. Exists because "what transfers happened between which wallets" — and product-facing fields like `Narration` — have no home on the ledger's account-keyed tables, yet are exactly what a wallet statement / transfer history view needs without joining out to `Accounts`.
* **Reconciliation Worker:** `ReconciliationWorker` (an `IHostedService`) continuously sweeps every wallet via an in-memory keyset cursor (`WHERE "Id" > cursor ORDER BY "Id" LIMIT BatchSize`, wrapping back to the start once a batch comes back short of `BatchSize`) and asserts, per wallet:
  $$\text{Wallet.AvailableBalanceKobo} == \sum \text{AccountEntries.Credit} - \sum \text{AccountEntries.Debit}$$
  Reconciliation is **incremental**, not a full-history re-sum every tick: each wallet's `LedgerBalanceKobo` is computed as `LastSnapshot.WatermarkBalanceKobo + Sum(AccountEntries posted after the watermark)`, using the wallet's most recent `LedgerSnapshot` row as the baseline and its `LastAccountEntryId` (the highest `AccountEntries.Id` folded into `WatermarkBalanceKobo` — a sequential UUID, so comparable the same way as the wallet keyset cursor itself) as the "already accounted for" watermark. A wallet with no prior snapshot sums its whole history once, on its first sweep only; every sweep after that only touches entries newer than the watermark, so per-tick cost no longer scales with an account's lifetime transaction count. This is still read via a **single SQL statement** (`Wallets` LEFT JOIN LATERAL'd to each wallet's latest `LedgerSnapshot` baseline, LEFT JOIN LATERAL'd again to a correlated `SUM` over only the newer `AccountEntries`), so `WalletBalanceKobo` and the freshly-computed `LedgerBalanceKobo` are always read from the same Postgres MVCC snapshot — no read-skew false positives from an in-flight transfer, since every write to a wallet's balance and its paired `AccountEntries` commits together in one DB transaction (`TransferService`/`DepositConsumer`). **Grace-period watermark:** entry IDs are assigned before commit (true of any ID scheme, DB-generated included), so concurrent postings can commit out of ID order, and a watermark that jumped to the highest committed ID would permanently skip a slower, lower-ID entry committing afterwards. So the compared `LedgerBalanceKobo` always includes *every* committed entry past the previous watermark, but the *stored* watermark (`LastAccountEntryId` + its matching `WatermarkBalanceKobo`) only advances to the highest entry older than `ReconciliationWorker:WatermarkGracePeriodSeconds` (default `60`). Younger entries are just re-summed on the next sweep(s) until they age out. Residual assumption: no posting transaction outlives the grace period, and clocks across API instances don't skew by more than it. A `LedgerSnapshot` row is written for **every** wallet checked each sweep — balanced or not — giving a full historical timeline of ledger health rather than an alert-only log. Any wallet found unbalanced while still `Active` is **immediately frozen** (`Wallet.Status → Frozen`, via the same atomic guarded-`UPDATE` pattern as §4) and audited (`AuditLog` action `"Freeze"`, actor `"system:reconciliation"`) in the same transaction as the snapshot write — safe to do without a grace period precisely because of the single-statement consistency guarantee above. Auto-freeze is gated by `ReconciliationWorker:AutoFreezeOnDiscrepancy` (default `true`) as an operational kill-switch, in case the snapshot computation itself is ever suspected of producing false positives — with it off (or the wallet already non-`Active`), the discrepancy is still recorded and logged, just not acted on. There is currently no automated unfreeze path — `Wallet.Reactivate()` exists but nothing calls it yet, so an auto-frozen wallet requires manual/admin intervention to restore.

### 4. Concurrency & Overdraft Guard Strategy
* **Deadlock Prevention:** Before initiating an inter-wallet transfer, wallet IDs are sorted deterministically ($\min(A, B) \to \max(A, B)$) to lock database rows in a consistent order.
* **Atomic DB Mutations:** Balance subtractions use atomic SQL updates (`WHERE AvailableBalanceKobo >= @AmountKobo`). Optimistic concurrency (`xmin`/`rowversion`) is avoided on hot debit paths to eliminate thread starvation under heavy contention.
* **Kobo Integer Math:** All monetary figures are processed as 64-bit integers (`long`) in Kobo ($\text{₦1.00} = 100 \text{ kobo}$) to guarantee zero floating-point drift.

### 5. Idempotency: DB Authoritative Path
The `Idempotency-Key` header is the client-supplied key for the transfer endpoint. Two requests with the same key must produce the same result; the same key with a *different* payload must be rejected.

1. Resolve the caller's wallet from the JWT (the source wallet is never in the request body) and compute `RequestPayloadHash = SHA-256(sorted { AmountInKobo, DestinationWalletId, Narration, resolved SourceWalletId })`. Because the source is part of the hash, the same key sent by a different user is a payload mismatch, never a replay, and never reveals the other user's outcome.
2. In Postgres, look up `WalletTransfers` by `IdempotencyKey` — for **every** request, before any business check. The row exists for both completed and failed transfers:
    - **Not found** → proceed with the transfer.
    - **Found, hash matches** → this is a replay; return the stored outcome without reprocessing: the original success data, or the stored failure code and reason.
    - **Found, hash differs** → reject with `409 Conflict` (`responseCode` `26`, "Idempotency-Key has already been used with a different request payload.").
3. The unique constraints on `IdempotencyKey` (`WalletTransfers` and `JournalEntries`) are what actually prevent two concurrent requests with the same key from both winning the insert race. The loser re-reads the winner's row and applies the same replay/conflict rule.

Redis is no longer part of this path: the earlier best-effort `SET NX` claim never changed the outcome (a lost or unavailable claim just fell through to this lookup), so it was removed.

**Failed transfers are recorded.** When a transfer is rejected after both wallets are confirmed to exist, a `WalletTransfers` row is written with `Status = Failed`, no `JournalEntryId`, and the NIP `FailureCode` plus `FailureReason`. Recorded: insufficient balance (`51`), daily limit (`61`), a non-Active wallet (`57`) and a currency mismatch (`30`). Not recorded: validation errors, a missing wallet (the row would violate its foreign keys), and `96` (transient, so the key stays retryable). Because there is one row per key, a key that ended in a stored failure stays failed on replay — the client sends a new key to try again. The failed row is saved after the transaction rolls back and is best-effort: if that save fails, the caller still gets the original failure response.

**Requery endpoints**
* `GET /api/v1/wallets/transfer/{idempotencyKey}` *(JWT)* — returns `status` (`Completed`/`Failed`/`Reversed`), `responseCode`, `failureReason`, `paymentReference`, the wallet ids, `amountInKobo`, `narration` and `transactionDate`. Only the caller's own transfers are visible; anything else is `404` / `25`.
* `GET /api/v1/wallets/credit/{sessionId}` *(anonymous, like `POST /credit`)* — returns the credit's `status` (`Pending`/`Completed`/`Failed`) with `responseCode` `09` / `00` / `96`. Unknown session is `404` / `25`.

### 6. $O(1)$ Atomic Daily Limit Enforcement
Daily outbound transfer limits ($\text{₦500,000 / day}$) are tracked using an atomic `UPSERT` on a materialized `WalletDailyUsage` table within the same DB transaction. The `INSERT` branch is guarded with a `WHERE` clause exactly like the `UPDATE` branch — a wallet's *first* transfer of the day must be checked against the limit too, not just subsequent ones. `UsageDate` is anchored explicitly to WAT (`Africa/Lagos`, UTC+1, no DST) rather than the database session's timezone, since `CURRENT_DATE` alone would follow whatever timezone the Postgres session/container defaults to (typically UTC) and could bucket a transfer made late at night WAT into the wrong day:

```sql
INSERT INTO "WalletDailyUsage" ("WalletId", "UsageDate", "TotalSpentKobo")
SELECT @WalletId, (CURRENT_TIMESTAMP AT TIME ZONE 'Africa/Lagos')::DATE, @AmountKobo
WHERE @AmountKobo <= @DailyLimitKobo
ON CONFLICT ("WalletId", "UsageDate")
DO UPDATE SET
    "TotalSpentKobo" = "WalletDailyUsage"."TotalSpentKobo" + EXCLUDED."TotalSpentKobo"
WHERE "WalletDailyUsage"."TotalSpentKobo" + EXCLUDED."TotalSpentKobo" <= @DailyLimitKobo;
```

If 0 rows are affected — on either the insert or update path — the transaction rolls back due to a daily limit breach, avoiding costly historical `SUM()` table scans. (An earlier version of this guard only checked the `UPDATE` branch, which let a wallet's first transfer of the day bypass the limit entirely — caught during review, see `AI_USAGE.md`.) The `(CURRENT_TIMESTAMP AT TIME ZONE 'Africa/Lagos')::DATE` expression is used everywhere a daily boundary is derived, not just here — including any reporting query that groups by day.

### 7. Authorization, Input Validation & Scope Guards
Several checks sit alongside the core concurrency/idempotency machinery and are enforced in the transfer handler before the DB transaction opens:
* **Wallet ownership:** the source wallet is inferred from the caller's session — it is the wallet whose `UserId` matches the `sub` claim on the JWT, so a caller can only ever move funds out of their own wallet. The transfer body therefore has no `sourceWalletId`; sending one is rejected with `30` ("SourceWalletId is not accepted; the source wallet is inferred from your session."). A caller with no wallet gets `25`.
* **Destination status:** `WalletStatus` is checked on both sides of a transfer, not just the source. A frozen or closed beneficiary wallet must reject inbound credits the same way it rejects outbound debits — this applies to the NIP inbound path as well.
* **Amount and self-transfer guards:** `AmountKobo` must be strictly positive, and the destination wallet must differ from the caller's own wallet (`30`). Enforced in application code and backstopped by a `CHECK ("AmountKobo" > 0)` constraint wherever amounts are persisted, so it isn't solely dependent on the app layer getting it right.
* **Currency scope:** all wallets in this system are NGN-only. `Accounts.Currency` exists for future multi-currency support, but a transfer request is rejected at validation if source and destination currency codes ever differ — this is a stated scope decision, not an unhandled case.
* **`POST /api/v1/wallets/credit` is intentionally unauthenticated** (`.AllowAnonymous()`) — it's an inbound callback from the NIP switch, not a customer-initiated action, and NIBSS-style inbound-credit callers don't carry this API's own bearer tokens. `DepositService`/`DepositConsumer` never consult caller identity; trust is placed entirely in the NIP payload (beneficiary account number, `SessionId`, external transaction reference) and its own idempotency guards (§5/§8). This overrides the app-wide `FallbackPolicy` (`RequireAuthenticatedUser()`, set in `AuthenticationExtensions`), which is standard ASP.NET Core behavior — `AllowAnonymous` metadata on an endpoint suppresses the fallback policy for that endpoint only.
* **Rate limiting is per endpoint group and configurable** (`RateLimitingOptions`, config section `RateLimiting`, validated on startup; sliding window, registered in `RateLimiterServiceExtensions.RegisterRateLimitingPolicies`). Class defaults are conservative; `appsettings.json` / `docker-compose.yml` (`RATELIMIT_*` env vars) override them:

  | Policy | Applied to | Partition key | Default (per 60s) |
  |---|---|---|---|
  | `LoginPolicy` | `POST /auth/login` | client IP only - the body/userId is never read, so inventing user IDs cannot buy a fresh bucket | 30 |
  | `UserPolicy` | wallet create, fetch, statement | JWT `sub` | 120 |
  | `TransferPolicy` | `POST /wallets/transfer` | JWT `sub` | 20 (compose sets 1000 so the overdraw load test can send bursts) |
  | `CreditPolicy` | `POST /wallets/credit` (anonymous webhook) | client IP | 600; **0 = unlimited** (compose default) |
  | `InternalAdminPolicy` | admin routes | client IP | effectively unlimited |

  Behind a reverse proxy every client would share the proxy's IP, so set `ForwardedHeaders:Enabled=true` (`FORWARDED_HEADERS_ENABLED`) - **only** behind a trusted proxy, because it trusts `X-Forwarded-For` from any sender. Rejections return 429 with a `Retry-After` header. The transfer route is rate-limited in addition to being ownership-scoped. Partitioning by authenticated caller rather than by IP is safe here specifically because `UseAuthorization()` runs before `UseRateLimiter()` in the pipeline (`Program.cs`), so the caller is already authenticated by the time the partition key is computed.

### 8. Outbox Delivery Guarantee
The `DepositConsumer` and any `TransferOutbox` publisher operate under **at-least-once delivery, exactly-once effect**: a crash or retry after the DB transaction has already committed will reprocess the same outbox row, but `ExternalCreditRequests.SessionId UNIQUE` (for deposits) and the ledger's `IdempotencyKey` uniqueness (for transfers) mean a reprocessed message cannot post a second credit/debit — it fails on the constraint and is discarded as already-handled rather than silently retried into a duplicate.

### 9. Mock-Auth Roles & Admin Endpoints
There is no `Users` table or real identity provider in this codebase — `POST /api/v1/auth/login` is a stated mock that mints a JWT from a caller-supplied `{ "userId": "...", "role": "Customer" | "Admin" }` body (`role` defaults to `Customer`). The `role` claim is trusted as-is and checked by the `AdminOnly` authorization policy (`RequireRole(nameof(UserRole.Admin))`, registered in `AuthenticationExtensions`) that gates every route under `/api/v1/admin`. No new tables or migrations were needed for this — the role lives only in the JWT.

* **`GET /api/v1/wallets/statement`** *(self-service, any authenticated caller)* — paginated, newest-first `AccountEntries` for the caller's own wallet, filterable by `fromDate`/`toDate`. Takes no `walletId` — a user has exactly one wallet, so it's resolved from the caller's identity (mirrors `GET /api/v1/wallets/`), meaning there's no other-wallet access to guard against; `404` if the caller has no wallet yet. Backed by the existing `IX_AccountEntries_AccountId_CreatedAt` index.
* **`GET /api/v1/admin/audit-logs`** *(`AdminOnly`)* — paginated, newest-first `AuditLog` rows, optionally filtered by `walletId`. Deliberately **not** ownership-scoped — an admin sees across wallets by design.
* **`PATCH /api/v1/admin/wallets/{walletId}/status`** *(`AdminOnly`)* — admin override of a wallet's status (`{ "status": "Active" | "Frozen" | "Closed" }`). This is the previously-missing self-service recovery path for wallets `ReconciliationWorker` auto-freezes (`Wallet.Reactivate()` existed but nothing called it before this). Rules: `Closed` is terminal (no transition out, including via this endpoint); setting the same status it's already at is an idempotent no-op (no `AuditLog` spam, matching `ReconciliationWorker`'s own philosophy); any genuine transition is an atomic, guarded compare-and-swap `UPDATE` (`WHERE "Status" = <status just read>`) plus an `AuditLog` row (`Action = "AdminStatusChange"`) written in the same transaction — same philosophy as the hot balance-mutation paths in §4, so a concurrent status change (another admin call, or `ReconciliationWorker` itself) can never be silently clobbered.
* All three routes require pagination query params clamped server-side (`pageSize` 1–100, default 20 — see `NovaWalletConstants.PaginationConstants`) and reuse the existing `PagedResponse<T>` envelope.

### 10. Response Codes (NIP style)
The body's `responseCode` uses NIBSS NIP-style codes (`Core/Models/Response/ResponseCodes.cs`, constants in `NipResponseCodes`). The HTTP status on the wire is chosen separately by `ApiResponseTransformer`, so a client reads `00` in the body alongside `200`.

| Response | `responseCode` | NIP meaning | HTTP status |
|---|---|---|---|
| `Success` | `00` | Approved or completed | 200 / 201 / 202 |
| `InvalidEntryDetected` | `30` | Format error | 400 |
| `NoRecordReturned` | `25` | Unable to locate record | 404 |
| `DuplicateRecord`, `Conflict` | `26` | Duplicate record | 409 |
| `DuplicateTransactionReference` | `94` | Duplicate transaction | 409 |
| `InsufficientBalance` | `51` | No sufficient funds | 422 |
| `DailyLimitExceeded` | `61` | Transfer limit exceeded | 422 |
| `RequestNotAllowed` | `57` | Transaction not permitted to sender | 403 |
| `AccessDenied` | `63` | Security violation | 401 |
| `TooManyRequests` | `65` | Exceeds withdrawal frequency (closest NIP code; NIP has no rate-limit code) | 429 |
| `Failed`, `SystemMalfunction` | `96` | System malfunction | 500 |

The `response_code` metric tag and the `deposit.outcome` span tag carry these values, so dashboards filtering on the old HTTP-shaped values (`"200"`, `"422"`) must switch to `"00"`, `"51"` and so on.

---

## End-to-End System Workflows

```
========================================================================================
1. INBOUND NIP CREDIT FLOW (Deposit)
========================================================================================

 [ NIP Switch ] ──► [ POST /api/v1/wallets/credit ]
                           │
                           ├── 1. Validate NIP SessionId & Account
                           ├── 2. Save ExternalCreditRequest Record
                           ├── 3. Write DepositOutbox Entry (Status: Pending)
                           └── 4. Return HTTP 202 Accepted
                                       │
                                       ▼
                             [ DepositConsumer Worker ]
                                       │
                                       ▼
                           [ PostgreSQL DB Transaction ]
                           ├── Lock Beneficiary Wallet Row
                           ├── Credit Wallet.AvailableBalanceKobo
                           ├── Post JournalEntry (Debit NIP Settlement / Credit User)
                           └── Mark Outbox & NIP SessionId as Treated
                                       │
                                       ▼
                                  [ DB COMMIT ]


========================================================================================
2. INTER-WALLET TRANSFER FLOW (Debit / P2P)
========================================================================================

 [ Client ] ──► [ POST /api/v1/wallets/transfer ]  (Idempotency-Key header required)
                       │
                       ▼
        [ JWT -> resolve caller's wallet as source ]
                       │
                       ▼
          [ DB Lookup by IdempotencyKey ]
                       │
                 ┌─────┴─────┐
              Found         New
                 │             │
                 ▼             ▼
        ├── Hash matches      [ Business Validation ]
        │   → stored result   (rejections: recorded as Failed row)
        │     (success/failure)       │
        └── Hash differs              ▼
            → 409 Conflict    [ PostgreSQL Transaction ]
                              ├── Lock IDs in Order: Min(A, B) -> Max(A, B)
                               ├── Atomic Daily Usage Guarded UPSERT
                               ├── Deduct Sender AvailableBalance
                               ├── Credit Recipient AvailableBalance
                               ├── Post Double-Entry Journal Entries
                               └── Write TransferOutbox Event
                                         │
                                         ▼
                                  [ DB COMMIT ]
                                         │
                                         ▼
                            [ Transfer Outbox Worker ] ──► [ OTel ]

```

---

## Domain Data Model Schema

### Database DDL (PostgreSQL)

```sql
-- Product Domain: Fast State Guard
CREATE TABLE "Wallets" (
    "Id" UUID PRIMARY KEY,
    "UserId" UUID NOT NULL,
    "Currency" VARCHAR(3) NOT NULL DEFAULT 'NGN',
    "AvailableBalanceKobo" BIGINT NOT NULL DEFAULT 0,
    "Status" INT NOT NULL DEFAULT 1, -- 1 = Active, 2 = Frozen, 3 = Closed
    "AccountId" UUID UNIQUE NOT NULL REFERENCES "Accounts"("Id"),
    "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT "CHK_Wallet_AvailableBalance_NonNegative" CHECK ("AvailableBalanceKobo" >= 0)
);

-- Materialized Daily Transfer Limit
CREATE TABLE "WalletDailyUsage" (
    "WalletId" UUID NOT NULL,
    "UsageDate" DATE NOT NULL,
    "TotalSpentKobo" BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY ("WalletId", "UsageDate"),
    FOREIGN KEY ("WalletId") REFERENCES "Wallets"("Id")
);

-- Ledger Domain: Chart of Accounts
CREATE TABLE "Accounts" (
    "Id" UUID PRIMARY KEY,
    "AccountNumber" VARCHAR(32) UNIQUE NOT NULL,
    "AccountType" INT NOT NULL, -- 1=Asset, 2=Liability, 3=Equity, 4=Revenue, 5=Expense
    "Currency" VARCHAR(3) NOT NULL DEFAULT 'NGN',
    "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- External NIP Credit Inflow Tracking
CREATE TABLE "ExternalCreditRequests" (
    "Id" UUID PRIMARY KEY,
    "SessionId" VARCHAR(30) UNIQUE NOT NULL, -- 30-digit NIP Session Reference
    "TransactionReference" VARCHAR(64) UNIQUE NOT NULL,
    "AmountKobo" BIGINT NOT NULL,
    "BeneficiaryAccountNumber" VARCHAR(32) NOT NULL,
    "OriginatingAccountNumber" VARCHAR(32) NOT NULL,
    "OriginatingBankCode" VARCHAR(10) NOT NULL,
    "Status" VARCHAR(20) NOT NULL DEFAULT 'Pending', -- Pending, Completed, Failed
    "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "DateModified" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "CompletedDate" TIMESTAMPTZ -- set only when Status transitions to Completed
);

-- Deposit Outbox for Asynchronous Processing
CREATE TABLE "DepositOutbox" (
    "Id" UUID PRIMARY KEY,
    "ExternalCreditRequestId" UUID NOT NULL REFERENCES "ExternalCreditRequests"("Id"),
    "Status" VARCHAR(20) NOT NULL DEFAULT 'Pending', -- Pending, Processed, Failed
    "NumberOfRetries" INT NOT NULL DEFAULT 0, -- transient-failure attempts; capped at 3, then Status -> Failed
    "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "DateProcessed" TIMESTAMPTZ, -- set once the row reaches a terminal state (Processed or Failed)
    "TraceParent" VARCHAR(64) -- W3C traceparent of the webhook request; lets DepositConsumer continue the same trace (nullable: pre-tracing rows have none)
);

-- Transfer Outbox for Asynchronous Notification / Event Publishing
CREATE TABLE "TransferOutbox" (
    "Id" UUID PRIMARY KEY,
    "JournalEntryId" UUID NOT NULL REFERENCES "JournalEntries"("Id"),
    "EventType" VARCHAR(40) NOT NULL DEFAULT 'TransferCompleted',
    "Status" VARCHAR(20) NOT NULL DEFAULT 'Pending', -- Pending, Published, Failed
    "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Double-Entry Header Record
CREATE TABLE "JournalEntries" (
    "Id" UUID PRIMARY KEY,
    "IdempotencyKey" VARCHAR(128) UNIQUE NOT NULL,
    "RequestPayloadHash" VARCHAR(64) NOT NULL, -- SHA-256 of the normalized request body
    "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Immutable Double-Entry Lines (Audit Trail)
CREATE TABLE "AccountEntries" (
    "Id" UUID PRIMARY KEY,
    "JournalEntryId" UUID NOT NULL REFERENCES "JournalEntries"("Id"),
    "AccountId" UUID NOT NULL REFERENCES "Accounts"("Id"),
    "AmountKobo" BIGINT NOT NULL,
    "EntryType" VARCHAR(20) NOT NULL, -- 'Debit' | 'Credit'
    "TransParticulars" VARCHAR(100) NOT NULL, -- system-composed, per-leg narration (auto-trimmed/truncated);
                                               -- distinct from WalletTransfers.Narration, which is one
                                               -- free-text value shared by both legs of a transfer
    "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    -- The amount is always positive; EntryType discriminates the side of the posting
    CONSTRAINT "CHK_AccountEntries_PositiveAmount" CHECK ("AmountKobo" > 0)
);

-- Statement query support: paginated, newest-first history per account
CREATE INDEX "IX_AccountEntries_AccountId_CreatedAt"
    ON "AccountEntries" ("AccountId", "CreatedAt" DESC);

-- Immutable, insert-only Audit Trail — distinct from AccountEntries.
-- AccountEntries answers "what moved, in double-entry terms"; AuditLog answers
-- "who did what, when, and what did the balance look like before/after" for
-- compliance and incident review. No UPDATE or DELETE is granted on this table
-- at the DB-role level.
CREATE TABLE "AuditLog" (
    "Id" UUID PRIMARY KEY,
    "WalletId" UUID NOT NULL REFERENCES "Wallets"("Id"),
    "ActorSubject" VARCHAR(128) NOT NULL,   -- JWT 'sub' claim of the caller
    "Action" VARCHAR(40) NOT NULL,           -- Debit, Credit, StatusChange, LimitBreach, etc.
    "BalanceBeforeKobo" BIGINT NOT NULL,
    "BalanceAfterKobo" BIGINT NOT NULL,
    "CorrelationId" UUID NOT NULL,
    "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Product-domain record of a completed inter-wallet transfer, written in the same
-- transaction as its JournalEntry/AccountEntry pair. AccountEntries answers "what moved,
-- in double-entry terms" keyed by AccountId; WalletTransfers answers "what transfers
-- happened between which wallets" keyed by WalletId on both sides, and carries
-- Narration/PaymentReference — product-domain fields with no home on the ledger tables —
-- so a "list my transfers" / wallet statement view can query it directly.
CREATE TABLE "WalletTransfers" (
    "Id" UUID PRIMARY KEY,
    "JournalEntryId" UUID UNIQUE REFERENCES "JournalEntries"("Id"), -- NULL for a Failed transfer (no ledger posting)
    "IdempotencyKey" VARCHAR(128) UNIQUE NOT NULL,                  -- one row per key, completed or failed
    "RequestPayloadHash" VARCHAR(64) NOT NULL,
    "FailureCode" VARCHAR(4),                                       -- NIP code of a Failed transfer, e.g. '51'
    "FailureReason" VARCHAR(500),                                   -- e.g. 'Insufficient balance.'
    "SourceWalletId" UUID NOT NULL REFERENCES "Wallets"("Id"),
    "DestinationWalletId" UUID NOT NULL REFERENCES "Wallets"("Id"),
    "AmountKobo" BIGINT NOT NULL,
    "Narration" VARCHAR(200),
    "PaymentReference" VARCHAR(64) UNIQUE NOT NULL, -- independently system-generated UUID v7, decoupled from JournalEntryId
    "Status" VARCHAR(20) NOT NULL DEFAULT 'Completed', -- Completed, Reversed, Failed
    "TransactionDate" TIMESTAMPTZ NOT NULL,
    "DateModified" TIMESTAMPTZ NOT NULL,
    CONSTRAINT "CHK_WalletTransfers_AmountPositive" CHECK ("AmountKobo" > 0)
);

-- Transfer-history query support: paginated, newest-first per wallet, on either side
CREATE INDEX "IX_WalletTransfers_SourceWalletId_TransactionDate"
    ON "WalletTransfers" ("SourceWalletId", "TransactionDate" DESC);
CREATE INDEX "IX_WalletTransfers_DestinationWalletId_TransactionDate"
    ON "WalletTransfers" ("DestinationWalletId", "TransactionDate" DESC);

-- Amount guard backstop, independent of application-layer validation
ALTER TABLE "ExternalCreditRequests" ADD CONSTRAINT "CHK_ExternalCredit_AmountPositive" CHECK ("AmountKobo" > 0);

-- Insert-only reconciliation record (README §3), written by ReconciliationWorker. Every wallet is
-- checked each sweep, but a row is only written on first check, when the watermark advances, on any
-- discrepancy, or as an idle heartbeat (SnapshotHeartbeatMinutes) - not once per wallet per tick.
CREATE TABLE "LedgerSnapshot" (
    "Id" UUID PRIMARY KEY,
    "RunId" UUID NOT NULL,                 -- groups every wallet checked in one sweep tick
    "WalletId" UUID NOT NULL REFERENCES "Wallets"("Id"),
    "AccountId" UUID NOT NULL REFERENCES "Accounts"("Id"),
    "WalletBalanceKobo" BIGINT NOT NULL,   -- Wallet.AvailableBalanceKobo at snapshot time
    "LedgerBalanceKobo" BIGINT NOT NULL,   -- WatermarkBalanceKobo of prior snapshot + Sum(all AccountEntries after its watermark)
    "DiscrepancyKobo" BIGINT NOT NULL,     -- WalletBalanceKobo - LedgerBalanceKobo
    "IsBalanced" BOOLEAN NOT NULL,
    "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "LastAccountEntryId" UUID NULL,        -- grace-lagged incremental-reconciliation watermark; NULL = nothing past grace yet
    "WatermarkBalanceKobo" BIGINT NOT NULL -- balance of only the entries up to LastAccountEntryId
);

-- Latest snapshot per wallet - "show current reconciliation status" query
CREATE INDEX "IX_LedgerSnapshot_WalletId_CreatedAt"
    ON "LedgerSnapshot" ("WalletId", "CreatedAt" DESC);

-- Fast "list current discrepancies, newest first" query - partial index over mismatches only
CREATE INDEX "IX_LedgerSnapshot_IsBalanced_Partial"
    ON "LedgerSnapshot" ("CreatedAt" DESC) WHERE "IsBalanced" = false;
```

---

## Technical Stack & Infrastructure

* **Framework:** .NET 9 Web API (C# 13)
* **Primary Database:** PostgreSQL 16
* **Cache & Idempotency:** Redis 8, password-protected (`--requirepass`, no anonymous access even in local dev)
* **Observability:** OpenTelemetry Collector (forwarding to a local OpenObserve instance, UI at `http://localhost:5080`) + Prometheus — the app exports OTLP traces/metrics directly and Prometheus scrapes `/metrics` on the API itself regardless of whether a collector is present (the OTLP exporter is non-blocking/async on connection failure). The Collector's endpoint (`ObservabilityOptions__ExporterUri`) is fully config/env-driven via `OBSERVABILITY_EXPORTER_URI`. OpenObserve needs no third-party account — it's a local container, part of the default stack alongside the Collector, even though no running code path consumes either yet.
* **Error Format:** RFC 7807 Problem Details

### Custom Application Metrics

Beyond the built-in OTel instrumentation (ASP.NET Core, HttpClient, EF Core, SQL, `Npgsql`,
runtime, process), `NovaWalletMetrics` (`Infrastructure/Extensions/OpenTelemetry/NovaWalletMetrics.cs`)
adds domain/business-level metrics via the BCL's `System.Diagnostics.Metrics` API — no extra
NuGet package required. The `Meter` is registered into the same OTel `MeterProvider` pipeline
(`.AddMeter(NovaWalletMetrics.MeterName)` in `OpenTelemetryConfigurationExtensions`), so every
instrument below flows through the existing OTLP exporter and the optional Prometheus `/metrics`
scrape endpoint (`EnablePrometheusMetricsEndpoint: true`) without any separate wiring:

| Instrument | Type | Tags | What it measures |
|---|---|---|---|
| `novawallet.deposit.requests` | Counter | `response_code` | `POST /wallets/credit` webhook-accept requests |
| `novawallet.deposit.request.duration` | Histogram (ms) | — | Webhook-accept-path latency |
| `novawallet.deposit.settlements` | Counter | `outcome` | `DepositConsumer` settlement attempts (`settled`, `already_processed`, `skipped_not_pending`, `rejected_beneficiary_not_found`, `rejected_wallet_not_active`, `unique_violation_race`, `transient_failure`, `retries_exhausted`) |
| `novawallet.deposit.settlement.duration` | Histogram (ms) | `outcome` | Settlement-attempt latency |
| `novawallet.transfer.requests` | Counter | `response_code` | `POST /wallets/transfer` requests |
| `novawallet.transfer.duration` | Histogram (ms) | `response_code` | End-to-end transfer latency |
| `novawallet.transfer.amount` | Histogram (kobo) | — | Distribution of successfully-settled transfer amounts |
| `novawallet.reconciliation.discrepancies` | Counter | — | Wallets found unbalanced per sweep |
| `novawallet.reconciliation.wallets_frozen` | Counter | — | Auto-freezes actually claimed by `ReconciliationWorker` |
| `novawallet.reconciliation.wallets_checked` | Counter | — | Sweep throughput |
| `novawallet.reconciliation.sweep.duration` | Histogram (ms) | — | Per-tick sweep latency |
| `novawallet.ratelimit.rejections` | Counter | `path` | HTTP 429 rejections, by route |

Tagging is deliberately restricted to small, fixed vocabularies (`ResponseCode` strings, a
handful of named outcomes, or a request path) — never caller-supplied values like wallet IDs or
narrations — to avoid unbounded cardinality in the metrics backend. Instrumentation is applied at
each public entry point via a thin timing wrapper around the existing implementation (renamed to
a private `...CoreAsync`/`...Core...` method), so none of the money-movement logic itself was
touched to add this.

### Tracing a Credit: Webhook to Funds Applied

The credit path crosses a database hand-off (`DepositService` writes `ExternalCreditRequest` +
`DepositOutbox`; the polling `DepositConsumer` settles it later), and nothing carries trace context
across a table. So the webhook stores its W3C `traceparent` in `DepositOutbox.TraceParent`, and the
consumer parents its settlement span on it, producing **one continuous trace** (custom
`ActivitySource` `NovaWallet.Deposits`, `Infrastructure/Extensions/OpenTelemetry/NovaWalletTracing.cs`,
registered in `ObservabilityExtensions`):

```
POST /api/v1/wallets/credit                (ASP.NET Core)
└─ deposit.accept                          (DepositService; EF/Npgsql spans for the inserts)
   └─ deposit.settle                       (DepositConsumer, kind=Consumer; possibly seconds later)
      ├─ deposit.lock_outbox               (SELECT ... FOR UPDATE on the outbox row)
      ├─ deposit.credit_wallet             (the guarded atomic UPDATE on Wallets)
      └─ deposit.post_journal              (journal + audit insert and COMMIT)
```

| Span | Key attributes |
|---|---|
| `deposit.accept` | `deposit.session_id`, `deposit.transaction_reference`, `deposit.amount_kobo`, `deposit.outcome` (response code), `deposit.replay` |
| `deposit.settle` | the same ids, plus `deposit.outbox_id`, `deposit.retry_count`, `wallet.id`, `deposit.outcome` (same labels as the settlement metric), `deposit.retries_exhausted` |
| `deposit.credit_wallet` | `wallet.id`, `deposit.amount_kobo`, `deposit.credit_applied` |
| `deposit.post_journal` | `journal_entry.id` |

A failed attempt marks `deposit.settle` as Error with an `exception` event, and each recorded retry adds a
`retry_recorded` event to it; retries of the same row all attach to the original trace. Permanent business
rejections (`rejected_*`) are also flagged Error. To find a credit in OpenObserve, search traces for
`deposit.session_id = '<NIP session id>'`. Unlike metrics, span attributes have no cardinality limit,
so ids are fine here; account numbers are still never attached. Rows written before `TraceParent`
existed (or with no ambient activity) simply start a new trace at `deposit.settle`.

---

## Getting Started

### Prerequisites

* Docker Engine 24+ & Docker Compose v2+
* .NET 8 SDK or newer (the API and both test projects target `net8.0`; the `net8.0` runtime must be installed to run them)

### Running via Docker Compose

Copy `.env.example` to `.env` (a working `.env` with dev-only placeholder values already ships in this repo, so this step is optional) and start the **entire** stack — PostgreSQL, Redis, a one-shot database migration step, the API, the OpenTelemetry Collector, and its local OpenObserve sink — with a single, unconditional command (no Compose profile to remember):

```bash
docker compose up --build
```

The Collector forwards traces/metrics/logs to OpenObserve (`ops/otel-collector-config.yaml`), running locally in the same Compose network — no third-party account needed. Once up, open `http://localhost:5080` and log in with `ZO_ROOT_USER_EMAIL` / `ZO_ROOT_USER_PASSWORD` from `.env` (dev-only placeholders ship by default, no setup required) — traces, metrics, and logs land under the `default` org/stream. As noted above, the Collector/OpenObserve run unconditionally but aren't consumed by any code path yet — they start regardless.

### Database Migrations

Migrations no longer piggyback on `ASPNETCORE_ENVIRONMENT=Development` — that coupling meant the only way to get automatic migrations in a container was to also silently opt every other `IsDevelopment()`-gated behavior (e.g. Serilog's console sink) into "dev mode," for a reason unrelated to logging. Two independent, explicit mechanisms replace it:

* **A dedicated one-shot `migrator` Compose service.** It builds the same image as `api`, runs it as `dotnet NovaWallet.Api.dll --migrate-only` (a mode added to `Program.cs` that applies pending EF Core migrations and exits immediately, without starting Kestrel), and exits 0. `api` declares `depends_on: migrator: condition: service_completed_successfully`, so the schema is guaranteed current before the API ever starts — as an isolated, observable step in `docker compose up`'s output, not folded into the API container's own boot logs. `api` itself now runs as `ASPNETCORE_ENVIRONMENT=Production` in Compose, a genuine choice rather than a migration side-effect.
* **`Startup:ApplyMigrationsOnStartup`** (`appsettings.json`, default `false`). An explicit, environment-agnostic config flag for scenarios outside Compose — e.g. `dotnet run` against a fresh local database — where invoking the one-shot mode separately isn't convenient. It stays off in the Docker stack, since the `migrator` service already covers it. `appsettings.Development.json` overrides it to `true`, so `dotnet run`/Visual Studio F5 debugging (both use `ASPNETCORE_ENVIRONMENT=Development` per `launchSettings.json`) keeps auto-migrating against a fresh local database exactly as it did before this flag existed.

> **Pending migration note:** `Migrations/` currently only covers schema up through the `OutboxEnums` migration. The `WalletTransfer` and `LedgerSnapshot` tables (added in later iterations — see §3) do not have migrations yet — the `migrator` service still runs and exits 0 (there's simply nothing pending for those tables to apply) — so against a fresh volume `POST /api/v1/wallets/transfer` and the `ReconciliationWorker` will fail once those migrations are hand-authored and until they're applied. Deposits, wallet creation, and wallet reads work end-to-end today.

Once started:

* **OpenAPI / Swagger Specs:** `http://localhost:5000/swagger`
* **Health / Readiness Endpoint:** `http://localhost:5000/health`
* **Prometheus Metrics:** `http://localhost:5000/metrics`

> **Port conflicts:** every published host port (`POSTGRES_PORT`, `REDIS_PORT`,
> `API_HTTP_PORT`, `OPENOBSERVE_HTTP_PORT`,
> `OTEL_COLLECTOR_GRPC_PORT`/`OTEL_COLLECTOR_HTTP_PORT`) is overridable via `.env`. Only the
> host-side number is configurable — the container-side port is fixed and unaffected, since
> inter-container traffic addresses other services by Compose service name (e.g. `postgres:5432`,
> `otel-collector:4317`), never through the host-published mapping. If any default collides with
> something already running on your machine, change the corresponding variable in `.env` and
> re-run `docker compose up --build`.

> **Two first-boot pitfalls, both already handled by the shipped `.env`:**
>
> * **OpenObserve won't start with a weak `ZO_ROOT_USER_PASSWORD`.** It panics the container at
>   boot (`... is too weak: Password must be 8-128 characters and contain at least one lowercase
>   letter, one uppercase letter, one digit, and one special character.`) rather than falling back
>   to anything. If you change the password, keep it meeting that rule, and regenerate
>   `OPENOBSERVE_AUTH_HEADER` per the comment above it in `.env`/`.env.example`.

> **Memory caps:** every service in `docker-compose.yml` carries a `deploy.resources` block
> (`postgres` 512M, `api` 768M, `openobserve` 1024M, `redis`/`migrator`/`otel-collector`
> 256M, all with a proportionate `reservations` floor) — this is Compose V2, so `docker compose up`
> applies these directly, no Swarm mode needed. `redis` additionally sets its own `--maxmemory 200mb
> --maxmemory-policy allkeys-lru` below the cgroup cap, so it evicts idempotency-cache keys under
> pressure instead of being OOM-killed outright (nothing cached there is a system of record). If any
> container ever exits with code 137 (OOM-killed — check via `docker inspect <name> --format
> '{{.State.OOMKilled}}'`), raise that service's `limits.memory` in `docker-compose.yml`.

---

## Automated Testing Suite

The suite is two xUnit projects under `tests/`, both listed in the solution:

| Project | Needs Docker | What it covers |
|---|---|---|
| `tests/NovaWallet.Api.UnitTests` | No | Validators, domain model rules (`Wallet`, `WalletTransfer`, `JournalEntry`, `DepositOutbox`, `ExternalCreditRequest`), the NIP response-code → HTTP-status mapping, request helpers, JWT generation, rate-limit option validation, and the request guards that run before any database work. |
| `tests/NovaWallet.Api.IntegrationTests` | **Yes** | The real API (real pipeline, EF Core, migrations and background workers) on a throwaway PostgreSQL 16 started with Testcontainers. Auth and admin access, wallets and statements, the deposit flow, transfers, idempotency, concurrency, requery, rate limiting, reconciliation, and database-level constraints. |

The integration tests use a real PostgreSQL rather than SQLite or the EF in-memory provider on purpose: the money-movement code depends on `INSERT ... ON CONFLICT`, `UPDATE ... RETURNING`, row locking and the `CHECK` / unique constraints, none of which those providers reproduce. The container image is `postgres:16-alpine`; Docker must be running. All migrations are applied to the test database first, so the migration chain is exercised too. The API runs as `Production`, as in `docker-compose.yml`, with Redis unset (so `HybridCache` uses memory), the deposit consumer polling every second, and rate limits raised out of the way except in the rate-limit tests.

### Running the Tests

```bash
# everything
dotnet test --configuration Release

# unit tests only - no Docker needed
dotnet test tests/NovaWallet.Api.UnitTests

# integration tests only - Docker must be running
dotnet test tests/NovaWallet.Api.IntegrationTests
```

Test collections run one after another (`DisableTestParallelization`): they share process-wide environment variables and the rate-limit tests depend on exact request counts.

| Integration test class | Covers |
|---|---|
| `AuthTests` | Login, bad/expired/wrongly-signed tokens (401), admin-only routes (403 for customers), anonymous credit and health endpoints. |
| `WalletEndpointTests` | Wallet creation (idempotent, one wallet per user), retrieval, paginated statements. |
| `DepositFlowTests` | Anonymous `/credit` → 202, settlement by the consumer, balanced journal, replays and concurrent redeliveries credit once, duplicate transaction references (94), frozen beneficiary, `TraceParent` on outbox rows, credit requery (`09` / `00` / `96`). |
| `TransferTests` | Happy path, source wallet inferred from the JWT, a stale `sourceWalletId` rejected with `30`, validation, daily limit (61), frozen wallets (57), which rejections are recorded as `Failed` rows and which are not. |
| `IdempotencyTests` | Replay returns a byte-identical body and debits once; same key with a different payload is rejected (`26`); failed transfers replay their stored failure; another user's key leaks nothing; concurrent same-key requests yield one transfer. |
| `ConcurrentTransferTests` | The 50-request overdraw test below, opposite-direction transfers (deadlock check), many receivers, transfers racing deposits. |
| `TransferRequeryTests` | `GET /wallets/transfer/{idempotencyKey}` for completed, failed, unknown and other users' keys. |
| `RateLimitTests` | Login (per IP), transfer (per user) and credit (per IP) limits, with `Retry-After` on the 429. |
| `ReconciliationTests` | A discrepancy is snapshotted, freezes the wallet and is audited; healthy wallets under concurrent transfers are never flagged. |
| `LedgerInvariantTests` | Every journal balances, every wallet equals its ledger, and the database's own `CHECK` / unique-index backstops fire. |

### Concurrency Load Test Highlight

`ConcurrentTransferTests.cs` fires **50 simultaneous HTTP requests** (`Task.WhenAll`) against a wallet with a balance of $\text{₦10,000}$ ($\text{1,000,000 Kobo}$) attempting to transfer $\text{₦500}$ each ($\text{₦25,000}$ total attempt):

```csharp
[Fact]
public async Task Transfer_ConcurrentRequests_GuaranteesNonNegativeBalanceAndNoDoubleSpend()
{
    // Arrange: 50 requests of 50,000 kobo (2.5m kobo in total) against a 1.0m kobo balance.
    // The sender is whoever the JWT says - there is no sourceWalletId in the request.
    var sender = await _fixture.CreateUserAsync(fundKobo: 1_000_000);
    var receiver = await _fixture.CreateUserAsync();

    // Act: all 50 in flight at once, each under its own Idempotency-Key.
    var responses = await Task.WhenAll(Enumerable.Range(0, 50)
        .Select(_ => sender.Client.TransferAsync(receiver.WalletId, 50_000, Guid.NewGuid().ToString())));

    // Assert: exactly 20 succeed (200) and 30 are rejected (422, code 51) ...
    Assert.Equal(20, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
    Assert.Equal(30, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));

    // ... the sender ends at exactly zero, the receiver holds everything that was debited,
    // and WalletTransfers holds 20 Completed rows plus 30 Failed rows (code 51).
    Assert.Equal(0, await _fixture.GetBalanceAsync(sender));
    Assert.Equal(1_000_000, await _fixture.GetBalanceAsync(receiver));
}
```

### Idempotency Regression Test Highlight

`IdempotencyTests.cs` verifies both halves of the requirement: a replayed key with an identical payload returns the original result without a second debit, and a reused key with a different payload is rejected outright.

```csharp
[Fact]
public async Task Transfer_ReplayedIdempotencyKey_ReturnsSameResultWithoutDoubleDebit()
{
    var sender = await _fixture.CreateUserAsync(fundKobo: 1_000_000);
    var receiver = await _fixture.CreateUserAsync();
    var key = Guid.NewGuid().ToString();

    var first = await sender.Client.TransferAsync(receiver.WalletId, 50_000, key);
    var second = await sender.Client.TransferAsync(receiver.WalletId, 50_000, key);

    // Byte-for-byte identical, including the transaction timestamp and payment reference.
    Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());

    Assert.Equal(1_000_000 - 50_000, await _fixture.GetBalanceAsync(sender)); // debited exactly once
}

[Fact]
public async Task Transfer_ReusedIdempotencyKeyWithADifferentAmount_ReturnsConflict()
{
    var sender = await _fixture.CreateUserAsync(fundKobo: 1_000_000);
    var receiver = await _fixture.CreateUserAsync();
    var key = Guid.NewGuid().ToString();

    await sender.Client.TransferAsync(receiver.WalletId, 50_000, key);
    var response = await sender.Client.TransferAsync(receiver.WalletId, 75_000, key);

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); // responseCode "26"
}
```

The same rules hold for a **failed** transfer: replaying its key returns the stored failure (same code and reason) without adding a second row, and reusing the key with a different payload is a conflict.