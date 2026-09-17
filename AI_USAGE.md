# AI Usage

This document describes how AI was used while building NovaWallet.Ledger.Service,
per the take-home brief's AI usage requirement.

## Tools used, and for what

**Cline** (an AI coding agent built on Anthropic's Claude, model Sonnet) was used
throughout the build, in an explicit **Plan → Act** workflow: every non-trivial
change was first proposed as a written plan (files touched, exact SQL/entity
shapes, trade-offs called out explicitly), reviewed, and only implemented after
approval to switch into Act mode. It was used for:

- **Architecture design** — the double-entry ledger model (`Wallet` product
  domain vs. `Account`/`AccountEntry` ledger domain), the idempotency strategy
  (Redis fast-path + DB-authoritative path), the outbox pattern for inbound NIP
  deposits, and the reconciliation/auto-freeze design.
- **Implementation** — endpoint handlers, services (`DepositService`,
  `TransferService`), background workers (`DepositConsumer`,
  `ReconciliationWorker`), EF Core entity/configuration classes, FluentValidation
  validators, and Docker/Compose infrastructure.
- **Self-review passes** — after most features, the agent was explicitly asked
  to compare related code paths against each other (e.g. "is there any gap
  between transfer and deposit?") rather than only reviewing a change in
  isolation. This caught real issues (see the first example below), because
  bugs in this codebase tend to show up as *asymmetries* between two paths that
  should behave the same way, not as an isolated bad line.
- **Documentation** — keeping `README.md`'s architecture/schema sections in
  sync with the actual code as it evolved, rather than writing docs once at
  the end.

All AI-authored code was manually read end-to-end before being accepted —
`dotnet build` was not available in the interactive session for most of this
build (a local tooling/permission constraint, not a comment on the AI), so the
human-in-the-loop step here was closer to "compile by careful reading" than
"skim and trust." That constraint is itself part of why the review examples
below matter: they were caught by asking the right question, not by a test
suite failing.

## Concrete example prompts and what came back

### Prompt 1 — "Is there any gap in transfer vs deposit?"

Asked after both `TransferService` and `DepositConsumer` had independently
converted their balance mutation to a single atomic guarded SQL `UPDATE ...
WHERE ... RETURNING` (replacing an earlier `SELECT ... FOR UPDATE` +
in-memory-mutate + `SaveChanges` pattern). The two files had been written in
separate sessions, and the ask was specifically to diff their *behavior*, not
their code.

The AI's response identified that the two paths handled a zero-row result from
that guarded `UPDATE` differently:

- `TransferService` re-reads the wallet's status afterward and splits the
  zero-row case into two outcomes: the wallet is legitimately not `Active`
  (a normal business rejection), vs. the wallet's status still reads `Active`
  (meaning the row inexplicably didn't match the guard — treated as a system
  anomaly, logged at error severity, surfaced as a 500).
- `DepositConsumer` did not make this distinction at all — *any* zero-row
  result, anomalous or not, was immediately and permanently marked `Failed`
  on both `DepositOutbox` and `ExternalCreditRequest`, logged only at
  `Warning`, with **no retry**, even though `DepositConsumer` already has a
  retry/backoff mechanism (`RecordFailedAttempt`, `MaxRetries = 3`) that would
  have been the correct fit for a transient anomaly.

This was fixed by adding the same status re-check to `DepositConsumer`
(`ReadWalletStatusAsync`): a legitimately non-`Active` wallet still fails
immediately as before, but an anomalous zero-row result on an `Active` wallet
now throws and is routed through the existing transient-failure/retry path
instead of being silently written off. See `Workers/ReconciliationWorker.cs`'s
sibling `DepositConsumer.cs` for the applied fix.

### Prompt 2 — "The service and its datastore must start with a single
command" (docker compose plan)

Asked to produce a Docker Compose setup satisfying the brief's single-command
startup requirement. Before writing any YAML, the AI was asked to first read
`Program.cs`, the connection-string/JWT extension classes, and the README's
"Technical Stack" section against each other.

What came back: the README's stack description (Postgres, Redis, **RabbitMQ**,
**OpenTelemetry Collector + Prometheus + Grafana**) does not match what the
running code actually depends on — there is no `ConnectionFactory`, publisher,
or consumer for RabbitMQ anywhere in the codebase (only OTel trace-context
helper classes reference RabbitMQ types, for future use), and the OTLP
exporter is non-blocking on connection failure, so the app boots fine with
no collector present. Rather than standing up a compose file that silently
starts containers nothing talks to (or worse, quietly dropping them and
leaving the README's claim uncorrected), the AI proposed gating RabbitMQ and
the OTel Collector behind an opt-in Compose `profile`, and flagged the
README/compose mismatch explicitly instead of resolving it unilaterally.

This is the pattern worth naming: the useful AI behavior here wasn't
generating YAML — it was refusing to let documentation and infrastructure
diverge quietly, and asking instead of guessing which one should win.

## A specific case where AI output was wrong/unsafe for a financial system

**The daily transfer limit's `INSERT` branch didn't enforce the limit.**

The atomic `UPSERT` guarding the ₦500,000/day outbound transfer limit
(`WalletDailyUsage`, README §6) went through an earlier version that looked
like this:

```sql
INSERT INTO "WalletDailyUsage" ("WalletId", "UsageDate", "TotalSpentKobo")
VALUES (@WalletId, @UsageDate, @AmountKobo)
ON CONFLICT ("WalletId", "UsageDate")
DO UPDATE SET
    "TotalSpentKobo" = "WalletDailyUsage"."TotalSpentKobo" + EXCLUDED."TotalSpentKobo"
WHERE "WalletDailyUsage"."TotalSpentKobo" + EXCLUDED."TotalSpentKobo" <= @DailyLimitKobo;
```

The `WHERE` clause on the `UPDATE` branch is correct — it makes the update a
no-op (0 rows affected) if applying this transfer would push the day's total
over the limit, and the caller treats 0 rows affected as "limit breached, roll
back." The bug: **the `INSERT` branch has no equivalent guard.** For any
wallet's *first* transfer of the day (no existing `WalletDailyUsage` row for
that `WalletId`/`UsageDate`), Postgres takes the `INSERT` path, which always
succeeds regardless of `@AmountKobo` versus `@DailyLimitKobo` — so a single
transfer of, say, ₦10,000,000 on a wallet's first attempt of the day would
have gone through untouched by the limit, and only *subsequent* transfers that
same day would ever hit the guard. This is exactly backwards for a control
that exists to cap exposure per wallet per day: it protected every case
except the one most likely to be a large, single fraudulent or erroneous
transfer.

This was not something a type checker or a happy-path test would catch — the
`UPDATE` branch working correctly makes the feature look done, and a test
suite that always seeds a `WalletDailyUsage` row before asserting limit
behavior would never exercise the `INSERT` branch at all. It was caught by
deliberately tracing *both* branches of the `ON CONFLICT` against the
requirement ("the limit applies to the day's total, not just to
already-recorded days") rather than trusting that a `WHERE`-guarded `UPDATE`
implied the whole statement was guarded.

**Fix applied** (current state, `TransferService`, also reproduced in README §6):

```sql
INSERT INTO "WalletDailyUsage" ("WalletId", "UsageDate", "TotalSpentKobo")
SELECT @WalletId, (CURRENT_TIMESTAMP AT TIME ZONE 'Africa/Lagos')::DATE, @AmountKobo
WHERE @AmountKobo <= @DailyLimitKobo
ON CONFLICT ("WalletId", "UsageDate")
DO UPDATE SET
    "TotalSpentKobo" = "WalletDailyUsage"."TotalSpentKobo" + EXCLUDED."TotalSpentKobo"
WHERE "WalletDailyUsage"."TotalSpentKobo" + EXCLUDED."TotalSpentKobo" <= @DailyLimitKobo;
```

Turning the bare `VALUES (...)` into a guarded `SELECT ... WHERE @AmountKobo <=
@DailyLimitKobo` means the `INSERT` branch now also yields 0 affected rows —
and therefore also triggers the "limit breached" rollback path — when a
wallet's first transfer of the day already exceeds the daily cap on its own.
(This example is summarized from the project's real build history, traceable
via README §6's own note and git history, not reproduced as a verbatim chat
transcript.)

## Where this leaves review responsibility

Both examples above share a shape: the AI produced code that was *locally*
plausible — it compiled (mentally, given the build-tooling constraint noted
above), matched the pattern of surrounding code, and handled the case someone
would naturally test first — but was wrong on a case that only shows up when
you deliberately ask "what about the other branch / the other code path doing
the same job?" For a system that moves real money, that means AI-authored
financial logic was never treated as trustworthy by default here; the
practice adopted was to explicitly interrogate boundary/asymmetry cases
(first-of-day, wallet-vanished-vs-not-Active, INSERT-vs-UPDATE) as a
follow-up review step on every AI-authored change to a balance-affecting code
path, rather than relying on the AI to have already considered them.
