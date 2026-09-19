# NovaWallet JMeter plan

One plan for JMeter 5.6.x: `overdraw/wallet-transfer-overdraw.jmx`. It targets `http://localhost:5000` by default (the `api` container's `API_HTTP_PORT`). It uses no Groovy/JSR223, so it runs on any JDK (JMeter's bundled Groovy 4 fails on very new JDKs with `Unsupported class file major version`).

Auth is the API's mock login (`POST /api/v1/auth/login` with a random `userId` and `role: Customer`), so no credentials are stored in the plan.

## Overdraw plan (concurrent debits against one balance)

Checks that the guarded `UPDATE` never lets concurrent debits overdraw a wallet. Defaults reproduce the scenario "50 concurrent N500 debits vs a N10,000 balance": exactly **20** transfers succeed, **30** are rejected with `422 Insufficient balance`, the sender ends at **0** and the receiver at **1,000,000** kobo. The 30 rejections are also stored as `WalletTransfers` rows with `Status = 'Failed'`, `FailureCode = '51'` and `FailureReason = 'Insufficient balance.'`, one per Idempotency-Key (`SELECT "FailureCode", "FailureReason", count(*) FROM "WalletTransfers" WHERE "Status" = 'Failed' GROUP BY 1, 2;`). The transfer body carries no `sourceWalletId`: the source is the wallet of the sender whose token the plan uses.

**Order of steps:** (1) log in sender and receiver, (2) create both wallets, (3) fund the sender through `/credit` and poll until it settles, (4) pre-flight: read both balances once (sender must equal `balance_kobo`, receiver must equal 0, otherwise `SETUP FAILED` is emitted), (5) the burst: transfers only, released together, with no balance or statement calls in between, (6) final check in tearDown: tally the outcomes and read both balances.

Run from inside the overdraw folder so the `.jtl`, the HTML report and `jmeter.log` all land there (JMeter resolves `-l`, `-o` and `jmeter.log` relative to the current directory). The `-o` folder must not exist or must be empty, so use a timestamped name:

```powershell
cd loadtest/jmeter/overdraw
$ts = Get-Date -Format yyyyMMdd-HHmmss
jmeter -n -t wallet-transfer-overdraw.jmx `
  -Jjmeter.save.saveservice.response_data.on_error=true `
  -Jjmeter.save.saveservice.responseHeaders=true `
  -l "overdraw-$ts.jtl" -e -o "overdraw-report-$ts"
```

Smoke test (same folder): add `-Jrequests=5 -Jamount_kobo=300000 -Jsettle_wait_ms=3000` (balance 1,000,000 => 3 succeed, 2 rejected, sender ends at 100,000).

Import into the GUI with File -> Open.

| Property | Default | Meaning |
|---|---|---|
| `host`, `port`, `protocol` | `localhost`, `5000`, `http` | target |
| `requests` | 50 | concurrent transfers (threads, all released together by a Synchronizing Timer) |
| `amount_kobo` | 50000 | amount of each transfer |
| `balance_kobo` | 1000000 | the sender's funded balance |
| `settle_wait_ms` | 5000 | wait per attempt for the deposit consumer to settle the funding credit |
| `setup_attempts` | 6 | max settle polls before setUp gives up |
| `sync_timeout_ms` | 30000 | how long the timer waits for all threads before releasing anyway |

Expectations are derived, not hard-coded: successes = `min(requests, floor(balance / amount))`, rejections = `requests - successes`, sender ends at `balance - successes x amount`, receiver at `successes x amount`.

- **Rate limit must be above `requests`.** A 429 (or 5xx, or 401) **fails** this plan: if the limiter answered first, the balance guard would never be exercised and the result would be meaningless. docker-compose ships `RATELIMIT_TRANSFER_PERMIT_LIMIT=1000`; with the code default of 20 the run would fail on its 429s. Rebuild/restart the API after changing `.env`.
- **Login is rate limited per client IP** (`LoginPolicy`, default 30/min; `RATELIMIT_LOGIN_PERMIT_LIMIT`). The plan logs in only a few times, but the local `.env` raises the limit to 1000 anyway.
- **Only `Insufficient balance` counts as a guard rejection.** A 422 for another reason (e.g. the daily limit; the plan's amounts are far below it) is tallied as "other" and fails the run.
- **Settle wait:** funding is asynchronous (outbox + `DepositConsumer`). setUp polls until the sender's balance shows the credit, up to `setup_attempts` times.
- **Result:** tearDown emits one sample. `OVERDRAW OK ok=... rejected=... sender=... receiver=...` on success, or `OVERDRAW FAILED (401 by design) ...` with observed vs expected values in the label (a deliberately failing call, since scripting-free JMeter cannot fail a run from a computed value). The "401" is an unauthenticated `GET /wallets` that only marks the run as failed; it is unrelated to the transfer `422`. If the pre-flight balance check fails (sender not funded, or receiver not 0) it emits `SETUP FAILED (deliberate 401 flags the run) ...` and the results are not valid.
- Concurrent outcomes are recorded in per-thread properties (`s_{n}`), not a shared counter, which would lose updates under 50 simultaneous threads.
- **Data growth:** each run creates two users and wallets and one deposit. Use a throwaway database.

## Server-side stats alongside a run

`loadtest/capture-stats.ps1` samples every 5s into a CSV (UTC timestamps, so it lines up with the `.jtl`):

```
./loadtest/capture-stats.ps1 -OutFile results/stats-overdraw.csv     # Ctrl+C when the JMeter run ends
```

Columns: API and Postgres container CPU% / memory (`docker stats`), Postgres connections (total / active / idle-in-transaction), sessions waiting on a lock, ungranted locks, and cumulative deadlocks and rollbacks (`pg_stat_activity`, `pg_locks`, `pg_stat_database`). It assumes the container names from `docker-compose.yml` and the `novawallet` DB user/database (override with `-PgUser`, `-PgDatabase`).

Npgsql connection-pool usage is not in the CSV: it is exported over OTLP (meter `Npgsql`, `db.client.connections.*`), so read it from OpenObserve for the same window.
