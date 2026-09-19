# NovaWallet JMeter plans

Five separate plans for JMeter 5.6.x. Targets default to `http://localhost:5000` (the `api` container's `API_HTTP_PORT`).

## Plans

The `.jmx` files in this folder are ready to import. `loadtest/_gen_jmx.py` (standard library only) is the optional generator they were modelled on. Running `python loadtest/_gen_jmx.py` from the repo root overwrites them with the generator's output.

| Plan | What it does |
|---|---|
| `wallet-creation.jmx` | Each iteration: mock login as a brand-new user, then `POST /api/v1/wallets`. |
| `wallet-statement.jmx` | Per thread: login, create and fund a wallet once, then loop `GET /api/v1/wallets/statement` (plain, and with `fromDate`/`toDate`). |
| `wallet-credit.jmx` | Anonymous `POST /api/v1/wallets/credit` with unique `sessionId` (<= 30 chars) and `transactionReference`, plus a W3C `traceparent` header. `-Jreplay=true` re-sends the same session to exercise idempotency. |
| `wallet-transfer.jmx` | Per thread: create a funded sender A and receiver B once, then `POST /api/v1/wallets/transfer` A -> B with a fresh `Idempotency-Key`. |
| `wallet-concurrent-multiwallet.jmx` | A setUp group creates and funds N wallets; concurrent users then run a weighted credit / statement / transfer mix against random wallets. |

Auth is the API's mock login (`POST /api/v1/auth/login` with a random `userId` and `role: Customer`), so no credentials are stored in the plans.

## Run (non-GUI)

```
jmeter -n -t loadtest/jmeter/wallet-credit.jmx -l results/credit.jtl -e -o results/credit-report
jmeter -n -t loadtest/jmeter/wallet-concurrent-multiwallet.jmx -Jwallets=20 -Jthreads=50 -Jloops=20 -l results/concurrent.jtl
```

Import into the GUI with File -> Open.

## Properties (all optional, pass as `-Jname=value`)

| Property | Default | Used by |
|---|---|---|
| `host`, `port`, `protocol` | `localhost`, `5000`, `http` | all |
| `threads` | 10 | all |
| `rampup` | 30 (seconds) | all |
| `loops` | 5 | all except the setUp group |
| `think_ms` | 0 | all (constant timer) |
| `credit_kobo` | 100000000 | funding credit in setup |
| `settle_wait_ms` | 10000 | pause after funding so the deposit consumer settles the credit |
| `amount_kobo` | 1000 | credit and concurrent plans |
| `transfer_kobo` | 100 | transfer and concurrent plans |
| `replay` | false | credit plan |
| `wallets` | 10 (min 2) | concurrent plan: wallets created in setUp |
| `credit_pct`, `statement_pct`, `transfer_pct` | 40, 40, 20 | concurrent plan; each is an independent percentage, so an iteration may run 0-3 actions |

## Things to know

- **Transfer rate limit:** `POST /wallets/transfer` allows 10 requests/min per user. The transfer plan gives each thread its own sender so throughput scales with threads, and it accepts `200` or `429`. Filter the `.jtl` by response code to see how many were limited. In the concurrent plan, 200, 422 (insufficient funds) and 429 are accepted; any 5xx fails.
- **Login rate limit:** login allows 300 requests/min. The creation plan logs in every iteration, so very high thread counts will start returning 429 and fail the status assertion. Raise `think_ms` or lower `threads` if that is not what you want to measure.
- **Settle wait:** funding is asynchronous (outbox + `DepositConsumer`). If `PollingIntervalSeconds` is larger than `settle_wait_ms`, the first transfers return 422 because the credit has not landed. Increase `settle_wait_ms`.
- **Tracing:** the credit plan sends `traceparent: 00-<traceId>-<spanId>-01`. Search OpenObserve for that trace id, or for `deposit.session_id`, to follow a webhook through to the wallet credit.
- **Data growth:** every run creates new users, wallets, deposits and journal entries. Use a throwaway database.
