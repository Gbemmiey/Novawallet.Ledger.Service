# FirstBank NovaWallet Ledger Service

A high-concurrency, double-entry wallet ledger service built for **FirstBank NovaPay** using C# and .NET 9. This service handles high-frequency inter-wallet transfers, NIP inbound deposit processing, idempotency, daily limit enforcement, and financial audit logging without floating-point precision issues or race conditions.

---

## Architectural Principles & Core Decisions

### 1. Redis Coordinates, Database Guarantees Money
* **Redis (`SET NX EX`)** acts as the front-line coordination layer for rapid idempotency checks and short-circuiting duplicate client submissions before they reach the database.
* **Database (PostgreSQL)** is the single source of truth. All balance mutations and double-entry postings execute within an atomic DB transaction protected by deterministic lock ordering and unique constraints.
* These two layers are wired together, not independent: Redis rejects an obvious duplicate cheaply; the DB lookup on `IdempotencyKey` is the authoritative path that actually determines whether a request is a replay, a conflict, or new (see §5 below).

### 2. Inbound NIP Deposit Architecture (Asynchronous Clearing)
Inbound NIP deposits arrive via API callbacks from NIBSS rails carrying a 30-digit `SessionId`.
* **Instant API Acknowledgment:** To prevent NIP timeout errors, the webhook validates the beneficiary account, records the raw `ExternalCreditRequest`, enqueues a `DepositOutbox` event, and returns HTTP 202 immediately.
* **Idempotent Inflow Processing:** The `DepositConsumer` picks up the outbox message, locks the beneficiary wallet row, credits `AvailableBalanceKobo`, posts the double-entry ledger pair (Debiting `1000-NIP-SETTLEMENT` Asset, Crediting `2100-USER-WALLET` Liability), and marks the NIP Session ID as settled in a single atomic transaction.

### 3. Separation of Concerns: Product vs. Ledger Domain
* **`Wallet` (Product Domain):** Holds mutable state (`AvailableBalanceKobo`), wallet status, and user-facing attributes. Features an $O(1)$ fast-access balance guard against overdrafts.
* **`Account` (Ledger Domain):** Tracks immutable financial records (`AccountEntries`). Its balance is a derived aggregate ($\sum \text{Credits} - \sum \text{Debits}$) serving as the legal audit trail.
* **`WalletTransfer` (Product Domain):** A queryable, wallet-keyed record of each completed transfer (`SourceWalletId`, `DestinationWalletId`, `Narration`, `PaymentReference`, `TransactionDate`), written in the same transaction as its `JournalEntry`/`AccountEntry` pair. Exists because "what transfers happened between which wallets" — and product-facing fields like `Narration` — have no home on the ledger's account-keyed tables, yet are exactly what a wallet statement / transfer history view needs without joining out to `Accounts`.
* **Reconciliation Worker:** An automated `IHostedService` periodically asserts:
  $$\text{Wallet.AvailableBalanceKobo} == \sum \text{AccountEntries.Credit} - \sum \text{AccountEntries.Debit}$$

### 4. Concurrency & Overdraft Guard Strategy
* **Deadlock Prevention:** Before initiating an inter-wallet transfer, wallet IDs are sorted deterministically ($\min(A, B) \to \max(A, B)$) to lock database rows in a consistent order.
* **Atomic DB Mutations:** Balance subtractions use atomic SQL updates (`WHERE AvailableBalanceKobo >= @AmountKobo`). Optimistic concurrency (`xmin`/`rowversion`) is avoided on hot debit paths to eliminate thread starvation under heavy contention.
* **Kobo Integer Math:** All monetary figures are processed as 64-bit integers (`long`) in Kobo ($\text{₦1.00} = 100 \text{ kobo}$) to guarantee zero floating-point drift.

### 5. Idempotency: Redis Fast-Path + DB Authoritative Path
The `Idempotency-Key` header is the client-supplied key for the transfer endpoint. Two requests with the same key must produce the same result; the same key with a *different* payload must be rejected.

1. Compute `RequestPayloadHash = SHA-256(sorted request body)`.
2. `SET idempotency:{key} processing NX EX 30` in Redis — if this fails (key exists), fall through to the DB lookup below rather than assuming it's a duplicate, since Redis is a cache, not the source of truth.
3. In Postgres, look up `JournalEntries` by `IdempotencyKey`:
    - **Not found** → proceed with the transfer, insert a new `JournalEntry` with the key and hash inside the same transaction.
    - **Found, hash matches** → this is a replay; return the previously stored result without reprocessing.
    - **Found, hash differs** → reject with `409 Conflict` (RFC 7807 `type: idempotency-key-reused`).
4. The unique constraint on `IdempotencyKey` is what actually prevents two concurrent requests with the same key from both winning the insert race — the app-level lookup handles the common case, the constraint handles the race.

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
* **Wallet ownership:** the source wallet's `UserId` must match the `sub` claim on the caller's JWT — a valid token alone does not authorize moving funds out of *any* wallet, only the caller's own.
* **Destination status:** `WalletStatus` is checked on both sides of a transfer, not just the source. A frozen or closed beneficiary wallet must reject inbound credits the same way it rejects outbound debits — this applies to the NIP inbound path as well.
* **Amount and self-transfer guards:** `AmountKobo` must be strictly positive, and `SourceWalletId` must differ from `BeneficiaryWalletId`. Enforced in application code and backstopped by a `CHECK ("AmountKobo" > 0)` constraint wherever amounts are persisted, so it isn't solely dependent on the app layer getting it right.
* **Currency scope:** all wallets in this system are NGN-only. `Accounts.Currency` exists for future multi-currency support, but a transfer request is rejected at validation if source and destination currency codes ever differ — this is a stated scope decision, not an unhandled case.

### 8. Outbox Delivery Guarantee
The `DepositConsumer` and any `TransferOutbox` publisher operate under **at-least-once delivery, exactly-once effect**: a crash or retry after the DB transaction has already committed will reprocess the same outbox row, but `ExternalCreditRequests.SessionId UNIQUE` (for deposits) and the ledger's `IdempotencyKey` uniqueness (for transfers) mean a reprocessed message cannot post a second credit/debit — it fails on the constraint and is discarded as already-handled rather than silently retried into a duplicate.

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
             [ Redis Fast-Path Claim ]
             (SET idempotency:key NX EX)
                       │
                 ┌─────┴─────┐
              Exists        New
                 │             │
                 ▼             ▼
        [ DB Lookup by Key ]  [ JWT & Business Validation ]
        ├── Hash matches                │
        │   → return cached result      ▼
        └── Hash differs      [ PostgreSQL Transaction ]
            → 409 Conflict    ├── Lock IDs in Order: Min(A, B) -> Max(A, B)
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
                            [ Transfer Outbox Worker ] ──► [ RabbitMQ / OTel ]

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
    "DateProcessed" TIMESTAMPTZ -- set once the row reaches a terminal state (Processed or Failed)
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
    "JournalEntryId" UUID NOT NULL UNIQUE REFERENCES "JournalEntries"("Id"),
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
```

---

## Technical Stack & Infrastructure

* **Framework:** .NET 9 Web API (C# 13)
* **Primary Database:** PostgreSQL 16
* **Cache & Idempotency:** Redis 7
* **Message Broker:** RabbitMQ 3.13 (for Outbox consumers)
* **Observability:** OpenTelemetry Collector + Prometheus + Grafana
* **Error Format:** RFC 7807 Problem Details

---

## Getting Started

### Prerequisites

* Docker Engine 24+ & Docker Compose v2+
* .NET 9 SDK (for local test running)

### Running via Docker Compose

Spin up the backend API, PostgreSQL datastore, Redis cache, RabbitMQ broker, and OpenTelemetry stack with a single command:

```bash
docker compose up --build -d
```

Once started:

* **OpenAPI / Swagger Specs:** `http://localhost:5000/swagger`
* **Health / Readiness Endpoint:** `http://localhost:5000/healthz`
* **Prometheus Metrics:** `http://localhost:5000/metrics`

---

## Automated Testing Suite

The project includes unit tests, integration tests via Testcontainers, and a load/concurrency test asserting non-negative balances under race conditions.

### Running All Tests

```bash
dotnet test --configuration Release
```

### Concurrency Load Test Highlight

`ConcurrentTransferTests.cs` fires **50 simultaneous HTTP requests** (`Task.WhenAll`) against a wallet with a balance of $\text{₦10,000}$ ($\text{1,000,000 Kobo}$) attempting to transfer $\text{₦500}$ each ($\text{₦25,000}$ total attempt):

```csharp
[Fact]
public async Task Transfer_ConcurrentRequests_GuaranteesNonNegativeBalanceAndNoDoubleSpend()
{
    // Arrange: 50 requests of 50,000 Kobo (Total 2.5m Kobo) against 1.0m Kobo balance
    var tasks = requests.Select(req => _client.PostAsJsonAsync("/api/v1/wallets/transfer", req));

    // Act
    await Task.WhenAll(tasks);

    // Assert
    var finalBalance = await GetWalletBalanceAsync(_sourceWalletId);
    Assert.True(finalBalance >= 0, "Balance drifted into negative!");
    Assert.Equal(0, finalBalance); // Exactly 20 succeeded, 30 rejected
}
```

### Idempotency Regression Test Highlight

`IdempotencyTests.cs` verifies both halves of the requirement: a replayed key with an identical payload returns the original result without a second debit, and a reused key with a different payload is rejected outright.

```csharp
[Fact]
public async Task Transfer_ReplayedIdempotencyKey_ReturnsSameResultWithoutDoubleDebit()
{
    var request = BuildTransferRequest(amountKobo: 50_000);
    var key = Guid.NewGuid().ToString();

    var first = await _client.PostAsJsonAsync("/api/v1/wallets/transfer", request, IdempotencyHeader(key));
    var second = await _client.PostAsJsonAsync("/api/v1/wallets/transfer", request, IdempotencyHeader(key));

    Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());

    var finalBalance = await GetWalletBalanceAsync(_sourceWalletId);
    Assert.Equal(_initialBalanceKobo - 50_000, finalBalance); // debited exactly once
}

[Fact]
public async Task Transfer_ReusedIdempotencyKeyDifferentPayload_ReturnsConflict()
{
    var key = Guid.NewGuid().ToString();
    await _client.PostAsJsonAsync("/api/v1/wallets/transfer", BuildTransferRequest(amountKobo: 50_000), IdempotencyHeader(key));

    var response = await _client.PostAsJsonAsync("/api/v1/wallets/transfer", BuildTransferRequest(amountKobo: 75_000), IdempotencyHeader(key));

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
}
```