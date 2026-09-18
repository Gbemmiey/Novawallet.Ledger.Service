# AI Usage

This document describes how AI was used while building NovaWallet.Ledger.Service, per the take-home brief's AI usage requirement. Three tools were used across three distinct phases of the work, and this file is organized the same way: early exploration, primary implementation, and documentation/review.

## Tools used, and for what

**Claude 3.5 Sonnet / ChatGPT (GPT-4o) via Cursor IDE** — used early, before implementation began in earnest, for initial SQL schema prototyping and comparing architectural options (distributed locks vs. database-level concurrency control, idempotency strategy shape) ahead of committing to an approach.

**Cline** (an AI coding agent built on Anthropic's Claude, model Sonnet) was used for the bulk of the implementation, in an explicit **Plan → Act** workflow: every non-trivial change was first proposed as a written plan (files touched, exact SQL/entity shapes, trade-offs called out explicitly), reviewed, and only implemented after approval to switch into Act mode. It was used for:

- **Architecture design** — the double-entry ledger model (`Wallet` product domain vs. `Account`/`AccountEntry` ledger domain), the idempotency strategy (Redis fast-path + DB-authoritative path), the outbox pattern for inbound NIP deposits, and the reconciliation/auto-freeze design.
- **Implementation** — endpoint handlers, services (`DepositService`, `TransferService`), background workers (`DepositConsumer`, `ReconciliationWorker`), EF Core entity/configuration classes, FluentValidation validators, and Docker/Compose infrastructure.
- **Self-review passes** — after most features, the agent was explicitly asked to compare related code paths against each other (e.g. "is there any gap between transfer and deposit?") rather than only reviewing a change in isolation. This caught real issues (see the case studies below), because bugs in this codebase tend to show up as *asymmetries* between two paths that should behave the same way, not as an isolated bad line.
- **Documentation** — keeping `README.md`'s architecture/schema sections in sync with the actual code as it evolved, rather than writing docs once at the end.

All AI-authored code was manually read end-to-end before being accepted — `dotnet build` was not available in the interactive session for most of this build (a local tooling/permission constraint, not a comment on the AI), so the human-in-the-loop step here was closer to "compile by careful reading" than "skim and trust." That constraint is itself part of why the review examples below matter: they were caught by asking the right question, not by a test suite failing.

**Claude (via claude.ai)** — used separately from the Cline implementation track, for documentation, review, and the interview deliverables: reviewing the README and DDL against the take-home brief for gaps, refactoring the EF Core entity model toward encapsulated invariants (private constructors, factory methods, per-entity Fluent API configuration), and generating the system-design presentation deck used for the panel walkthrough.

---

## Concrete example prompts and what came back

### Early exploration (Cursor)

**Locking strategy — distributed locks vs. conditional updates.**
> *"I am building a double-entry financial ledger in .NET 9 and PostgreSQL for an e-wallet. I need to handle 50+ concurrent transfer requests hitting the same wallet. Should I use distributed Redis locks or PostgreSQL row-level locks with conditional updates?"*

Compared Redis distributed locks (`RedLock`) against database-level locking, and recommended PostgreSQL row locks paired with an atomic conditional `UPDATE Wallets SET Balance = Balance - @Amount WHERE Id = @Id AND Balance >= @Amount`, plus deterministic ID sorting (`min(A,B) → max(A,B)`) to eliminate deadlocks during multi-account transfers. Adopted as the primary concurrency mechanism — it avoids the operational overhead and extra failure modes (lock expiry, ownership, crash recovery) a distributed lock would have introduced for database writes.

**Two-tier idempotency pattern.**
> *"Write a C# middleware and EF Core handler that enforces idempotency on POST /wallets/transfers using a Redis fast-path and PostgreSQL backstop with request body hashing."*

Generated an ASP.NET Core filter using `SET idempotency:{key} processing NX EX 30` in Redis, plus a `JournalEntries` lookup computing a SHA-256 hash of the request payload — returning the stored response on a matching hash, RFC 7807 `409 Conflict` on a mismatch. Used as the core shape of the idempotency handling on the transfer endpoint, with the DB lookup-by-key-and-hash as the actual authoritative guarantee.

### Primary implementation (Cline, Plan → Act)

#### Prompt 1 — "Is there any gap in transfer vs deposit?"

Asked after both `TransferService` and `DepositConsumer` had independently converted their balance mutation to a single atomic guarded SQL `UPDATE ... WHERE ... RETURNING` (replacing an earlier `SELECT ... FOR UPDATE` + in-memory-mutate + `SaveChanges` pattern). The two files had been written in separate sessions, and the ask was specifically to diff their *behavior*, not their code.

The AI's response identified that the two paths handled a zero-row result from that guarded `UPDATE` differently:

- `TransferService` re-reads the wallet's status afterward and splits the zero-row case into two outcomes: the wallet is legitimately not `Active` (a normal business rejection), vs. the wallet's status still reads `Active` (meaning the row inexplicably didn't match the guard — treated as a system anomaly, logged at error severity, surfaced as a 500).
- `DepositConsumer` did not make this distinction at all — *any* zero-row result, anomalous or not, was immediately and permanently marked `Failed` on both `DepositOutbox` and `ExternalCreditRequest`, logged only at `Warning`, with **no retry**, even though `DepositConsumer` already has a retry/backoff mechanism (`RecordFailedAttempt`, `MaxRetries = 3`) that would have been the correct fit for a transient anomaly.

Fixed by adding the same status re-check to `DepositConsumer` (`ReadWalletStatusAsync`): a legitimately non-`Active` wallet still fails immediately as before, but an anomalous zero-row result on an `Active` wallet now throws and is routed through the existing transient-failure/retry path instead of being silently written off. See `Workers/ReconciliationWorker.cs`'s sibling `DepositConsumer.cs` for the applied fix.

#### Prompt 2 — "The service and its datastore must start with a single command" (docker compose plan)

Asked to produce a Docker Compose setup satisfying the brief's single-command startup requirement. Before writing any YAML, the AI was asked to first read `Program.cs`, the connection-string/JWT extension classes, and the README's "Technical Stack" section against each other.

What came back: the README's stack description (Postgres, Redis, **RabbitMQ**, **OpenTelemetry Collector + Prometheus + Grafana**) does not match what the running code actually depends on — there is no `ConnectionFactory`, publisher, or consumer for RabbitMQ anywhere in the codebase (only OTel trace-context helper classes reference RabbitMQ types, for future use), and the OTLP exporter is non-blocking on connection failure, so the app boots fine with no collector present. Rather than standing up a compose file that silently starts containers nothing talks to, the AI proposed gating RabbitMQ and the OTel Collector behind an opt-in Compose `profile`, and flagged the README/compose mismatch explicitly instead of resolving it unilaterally.

The useful AI behavior here wasn't generating YAML — it was refusing to let documentation and infrastructure diverge quietly, and asking instead of guessing which one should win.

**Addendum (2026-09-18) — the decision was later revisited, not silently reversed.** The stakeholder subsequently asked, explicitly, for RabbitMQ to be included in the default stack "nonetheless" — i.e. despite the gap above still being true. Rather than editing the original example to pretend RabbitMQ had always been default, the AI moved the `rabbitmq` service from `docker-compose.observability.yml` into `docker-compose.yml` (dropping its `profiles: ["observability"]` gate) and updated every place that had asserted "RabbitMQ is opt-in" (README, both compose files' header comments) to instead say "RabbitMQ runs by explicit request, still consumed by no code path." The one thing that did **not** change: no `depends_on: rabbitmq` was added to the `api` service, because that would still be asserting a dependency that doesn't exist in code. Same session, same request, also used to make `ObservabilityOptions__ExporterUri` env-driven (it was previously hardcoded in the observability overlay) and to move Redis to version 8 with `--requirepass` authentication enforced — neither change altered any C# code, since `ExporterUri` was already bound from config and `REDIS_URI` already flowed straight into `ConfigurationOptions.Parse`, which parses a `password=` token natively.

#### Prompt 3 — "Rate limit the transfer endpoint. Wallet Credit is an Admin OP" (revised, same session: "...doesn't require any access token - anonymous")

The stakeholder's first message asked for `/credit` to become an **admin-gated** operation. Before implementing anything, the AI re-checked `DepositService` for any dependency on caller identity and found none — it derives everything from the NIP payload itself (beneficiary account number, `SessionId`, external transaction reference), the same fact pattern that had already shaped the Docker-compose RabbitMQ decision above. It flagged this back rather than silently implementing "Admin OP" as asked. The very next message from the stakeholder corrected the instruction outright: `/credit` should be **anonymous** — an external NIP-switch callback, not a customer- or admin-authenticated action.

This is kept in the record rather than quietly implemented as if "anonymous" had been the request from the start, for the same reason as the RabbitMQ addendum above: the honest history is that the instruction was wrong once and corrected, not that the AI inferred the right answer unprompted. What *was* caught unprompted, while planning the correction: the rate-limiter registration method still carried its original leftover name from an earlier placeholder domain that never existed anywhere else in this codebase, and it was about to gain a second, unrelated policy (`PerPartnerPolicy`, for `/transfer`'s new rate limit) alongside the existing `InternalAdminPolicy`. Rather than let a second policy accumulate under a misleading name, the AI proposed renaming the method to `RegisterRateLimitingPolicies` as an explicit (separately-confirmed) part of the change, not a silent drive-by rename bundled into an unrelated diff. A follow-up request afterward asked for every remaining trace of that original placeholder name to be swept from the repo; that sweep also caught an unrelated leftover default Redis cache instance-name string in `HybridCacheExtensions.cs` that had inherited the same placeholder domain name and was updated to `NovaWallet:Shared`.

Net implementation: `/credit` moved to `.AllowAnonymous()` (which, confirmed by reading `AuthenticationExtensions`, correctly overrides the app-wide `FallbackPolicy.RequireAuthenticatedUser()` — standard ASP.NET Core behavior, not something requiring a pipeline change); `/transfer` gained `RequireRateLimiting(PerPartnerPolicy)`, a 10-req/min sliding window partitioned by the caller's JWT `sub` claim (safe to key on identity rather than IP specifically because `UseAuthorization()` precedes `UseRateLimiter()` in `Program.cs`'s pipeline).

#### Prompt 4 — "Add custom metrics to the application"

Before writing any instrumentation code, the AI re-read the existing OTel wiring (`OpenTelemetryConfigurationExtensions`, `ObservabilityOptions`) and confirmed that `System.Diagnostics.Metrics` (`Meter`/`Counter<T>`/`Histogram<T>`) needs no new NuGet package — it's part of the BCL since .NET 6 — and that a custom `Meter` just needs `.AddMeter("<name>")` added next to the existing `.AddMeter("Npgsql")` line to flow through the exact same OTLP/Prometheus pipeline already working, rather than standing up a second exporter path.

Given `dotnet build` had been unrunnable via any tool for the entire session up to this point, the AI deliberately chose the lowest-risk instrumentation shape available: wrap each public service/worker entry point exactly once (rename the existing method body to a private `...CoreAsync`/`...Core...` method, add a thin public wrapper that times the call and records one counter+histogram pair) rather than threading metric-recording calls through the 10-15 individual `return` statements inside `TransferService.ProcessTransferAsync` or `DepositConsumer.ProcessOutboxEntryAsync`. This gets full outcome+latency coverage per operation without touching the money-movement logic itself — the same "don't risk what you can't compile" discipline that shaped every other change this session. One exception to the wrap-once shape: `DepositConsumer.ProcessOutboxEntryAsync` already had several named internal branches (beneficiary not found, wallet not Active, self-healed already-completed, etc.), so a closure-captured `outcomeLabel` string was set at each existing `return` point instead of collapsing them all into one generic label — preserving the granularity the code already expressed rather than flattening it for instrumentation's sake.

Tagging discipline was treated as a first-class design decision, not an afterthought: `ServiceApiResponse.ResponseCode` (a numeric HTTP-status-shaped string) was identified as a clean, low-cardinality, already-existing tag, while `ResponseMessage` was explicitly ruled out as a tag source since it sometimes interpolates dynamic content (wallet IDs, statuses) — an unbounded-cardinality trap that would have quietly degraded the metrics backend over time.

#### Prompt 5 — "I want to use a local OpenObserve as the telemetry sink from OtelCol."

Re-reading `ops/otel-collector-config.yaml` and `docker-compose.observability.yml` surfaced a standing gap this request incidentally fixed: `GRAFANA_CLOUD_OTLP_ENDPOINT` / `GRAFANA_CLOUD_BASIC_AUTH_HEADER` had shipped blank in `.env`/`.env.example` since they were first added, because real third-party credentials were never supplied and were never going to be invented. That meant the `observability` Compose profile had never actually been end-to-end testable for the entire session, despite being documented as available. Swapping in a local OpenObserve container (added to the same profile, no external account needed) makes the profile genuinely self-contained and runnable for the first time.

The one non-obvious technical detail: OpenObserve isn't a drop-in replacement for the Grafana Cloud exporter config, because it expects distinct per-signal HTTP paths (`/api/{org}/v1/traces`, `/v1/metrics`, `/v1/logs`) rather than one generic OTLP gateway endpoint. The `otlphttp` exporter component already in use supports this via its `traces_endpoint`/`metrics_endpoint`/`logs_endpoint` overrides, so no new exporter type was needed — just a different shape of the same config block.

The immediate follow-up instruction was to remove the Grafana Cloud env vars outright rather than keep them commented out as a documented alternative. That was executed as a full deletion from both `.env` and `.env.example` (recoverable from git history if ever needed again), consistent with treating "the telemetry sink" as singular rather than hedging with dead, never-configured plumbing left in place for its own sake.

#### Prompt 6 — "Don't hardcode outbound ports because of potential conflicts with existing apps on host machines — esp redis, postgres, rabbitmq, openobserve"

A pass over every `ports:` mapping across both Compose files found that `postgres`, `redis`, `api`, and `openobserve` were already `${VAR:-default}`-driven from earlier turns this session — only `rabbitmq` (`docker-compose.yml`) and `otel-collector` (`docker-compose.observability.yml`, not named in the request but sharing the identical conflict risk, added for consistency) still had bare `"5672:5672"`/`"15672:15672"`/`"4317:4317"`/`"4318:4318"` mappings. Both were parameterized the same way, with defaults matching prior behavior exactly, so nobody's workflow changes unless they hit a real conflict and override.

The detail worth recording: only the host-side (left) number of each `host:container` mapping was made variable. The container-side (right) number stays fixed intentionally, since nothing inside the Compose network ever addresses another service by its host-published port — `api` reaches Postgres/Redis/RabbitMQ/the Collector via Compose service name plus the fixed container port (`postgres:5432`, `otel-collector:4317`, etc.), so changing what's exposed to the host can never break inter-container communication.

#### Prompt 7 — "consolidate the docker compose files"

This request was a chance to re-examine a design decision made two prompts earlier (Prompt 5's addendum), rather than just mechanically merging two YAML files. `docker-compose.observability.yml` had originally been kept as a *separate* file (rather than folding `otel-collector`/`openobserve` straight into `docker-compose.yml`, profile-gated in place) for one stated reason: letting `api`'s `ObservabilityOptions__ExporterUri` be set "only when the collector is present," on the claim that Compose has no native "env var differs by profile on the same service" mechanism.

Re-checking that reasoning once consolidation was actually on the table, it didn't hold up: the exporter is already unconditionally env-driven (`OBSERVABILITY_EXPORTER_URI` in `.env`) and, as documented since it was first added, is async/non-blocking — it simply logs a warning and no-ops if nothing is listening at that address. So there was never a real need to conditionally *set* the env var at all; it's harmless to always set it on `api`, whether or not the `observability` profile is active, the same way `RABBITMQ_URI`-style "provisioned but not yet consumed" values are already handled elsewhere in this file. The original two-file split was solving a problem that didn't actually exist — consolidating back into a single `docker-compose.yml` (with `otel-collector` and `openobserve` both carrying `profiles: ["observability"]` in place) is a genuine simplification, not just a file-count change. `docker-compose.observability.yml` was deleted, and the `.slnx`, README, `.env.example`, and `ops/otel-collector-config.yaml` comment references to the two-file invocation were updated to the single-file `docker compose --profile observability up --build` form.

#### Prompt 8 — "I want docker compose up to start and run everything"

This instruction directly supersedes the profile-gating decisions made in Prompts 5–7: `otel-collector` and `openobserve` were, until this point, always described as "genuinely opt-in" — profile-gated behind `observability` specifically because nothing in the running app depends on them. That framing isn't being called wrong in hindsight; it was a reasonable default for an extras-are-optional posture. This prompt states a different posture outright — the entire stack, unconditionally, on the bare command — so the fix was mechanical once the intent was clear: drop `profiles: ["observability"]` from both services in `docker-compose.yml`, leaving every other piece of the design untouched. Specifically kept as-is: no `depends_on: otel-collector` was added to `api` (the OTLP exporter is still async/non-blocking, so there's still no real startup-ordering need), `otel-collector`'s plain `depends_on: [openobserve]` stays a start-order-only dependency (neither service has a healthcheck to key a `condition` off of), and RabbitMQ/the Collector/OpenObserve are all still honestly documented as unconsumed-by-code today — this prompt changes *when* they start, not *whether* the app actually talks to them. Updated in the same pass: `.env.example`'s and `.env`'s banner comments (dropped "optional"/"profile" framing), and the README's Getting Started section (one command instead of two).

#### Prompt 9 — "Is there a better way to handle migrations on start up rather than running the api in development mode?"

This one started as a question, not an instruction — worth noting because the answer required actually tracing the coupling rather than just adding a feature. `Program.cs` ran `app.ApplyDatabaseMigrationsAsync()` only inside `if (app.Environment.IsDevelopment())`, and `docker-compose.yml`'s `api` service set `ASPNETCORE_ENVIRONMENT: Development` *specifically* to trigger that branch. Tracing what else keys off `IsDevelopment()` in this codebase turned up a second, unrelated coupling: `SerilogConfigurationExtensions.cs` only wired up the console sink when `environment.IsDevelopment() && options.EnableConsoleLog` — so the migration hack was also silently the only thing keeping `docker compose logs api` readable. Neither of those was a deliberate design choice; both were side effects of reaching for the one environment flag ASP.NET Core hands you for free.

Presented three options (an in-process config flag; a dedicated one-shot Compose `migrator` service; EF Core migration bundles) with an explicit recommendation to combine the first two — the user confirmed "Option A + B" without answering two sub-questions left open in the plan (whether `api` should then move to `Production` in Compose, and whether the `migrator` service should shell out to `dotnet ef database update` via the SDK image or reuse the published `api` image with a new CLI mode). Rather than stall on an unanswered sub-question mid-Act-mode, both were resolved with the option that both minimized new moving parts and let the original question's own wording ("rather than running the api in development mode") settle the ambiguity:

- **`api` now runs `ASPNETCORE_ENVIRONMENT=Production`** in `docker-compose.yml`, since decoupling migrations from `IsDevelopment()` only actually answers the question if the container stops running in Development mode at all. This is what surfaced the Serilog coupling above — fixed in the same pass by making `EnableConsoleLog` alone (already an explicit, documented on/off switch) the sole gate, so container log visibility doesn't regress as a side effect of an unrelated migrations fix.
- **The `migrator` service reuses `api`'s own published image** with a new `--migrate-only` startup mode in `Program.cs`, rather than an SDK image running `dotnet ef database update`. Reasoning traced before choosing: it needs no `dotnet-ef` tool, no second Dockerfile stage, and — because it calls the exact same `ApplyDatabaseMigrationsAsync()` extension method `api` itself used to call — it structurally cannot drift from what `api`'s own migration logic does. Before wiring its environment variables, the DI graph was traced to confirm what `--migrate-only` actually touches before returning: `app.Run()` is never reached, so the `ValidateOnStart()`-gated `JwtSettings` validation (`AuthenticationExtensions.cs`) never executes, and `AddAppHybridCache`'s eager-but-`AbortOnConnectFail:false` Redis connect attempt is harmless either way — meaning the `migrator` service's environment could be, and was, kept to just `NOVAWALLET_LEDGER_CONNECTION_STRING`, not a full copy of `api`'s.

#### Prompt 10 — pasted a failed `docker compose up --build` log, no further instruction

The user ran the full stack for the first time after the migrations redesign (Prompt 9) and pasted the raw failure output rather than describing it — three unrelated problems in one log, which had to be told apart before any of them could be fixed:

1. **OpenObserve panicked at boot**: `ZO_ROOT_USER_PASSWORD is too weak: Password must be 8-128 characters and contain at least one lowercase letter, one uppercase letter, one digit, and one special character.` The shipped placeholder, `dev-only-openobserve-password-change-me`, is all-lowercase with no digit — it had never actually been booted against before this run, so the requirement was invisible until now. Fixed by changing `ZO_ROOT_USER_PASSWORD` (in both `.env` and `.env.example`) to `DevOnlyPassword1!` and regenerating `OPENOBSERVE_AUTH_HEADER`'s base64 accordingly, per the existing comment already documenting how to do that.
2. **RabbitMQ crash-looped** on `Error when reading /var/lib/rabbitmq/.erlang.cookie: eacces`. This one needed research, not guessing: it's a recognized Docker-Desktop-on-Windows filesystem race during first boot (the entrypoint writes+chmods the cookie file, and a near-simultaneous read sees stale permission state) — a transient timing bug, not a real permissions fault, and more likely precisely when many containers start at once, which is what the unconditional full-stack `docker compose up` does. Fixed with two changes to the `rabbitmq` service in `docker-compose.yml`: a named `novawallet-rabbitmq-data` volume (consistent with `postgres`/`openobserve`, replacing reliance on the container's own writable layer) and `restart: on-failure:5`, so a transient hit self-heals instead of failing the whole stack.
3. **`otel-collector` failed to bind port 4317**: `Bind for 0.0.0.0:4317 failed: port is already allocated`. Traced with `docker ps -a` rather than assumed — the actual cause was an unrelated, already-running project on the same machine (`heritagehaven-otel-collector` and `heritagehaven-redis` containers, from a different repo) already holding host ports 4317/4318 and 6379. This is exactly the scenario the fully-overridable-host-ports design (Prompt 6) exists for: rather than ask the user to stop an unrelated project's containers, `REDIS_PORT`, `OTEL_COLLECTOR_GRPC_PORT`, and `OTEL_COLLECTOR_HTTP_PORT` were bumped in this repo's own `.env` (not `.env.example` — this collision is specific to this machine's other running project, not a general default worth changing for everyone). Internal Compose-network traffic still addresses both services by their container-side ports (`redis:6379`, `otel-collector:4317`), so remapping only the host side doesn't touch anything inside the stack.

All three were config/environment issues, not application-code bugs — none of the C# changes from Prompt 9 needed to change. Documented both pitfalls (1) and (2) in README's existing "Port conflicts" callout, since they're the kind of first-boot surprise a future reader would otherwise have to re-diagnose from scratch.

#### Prompt 11 — "set names for the dependencies in the container rather than app-1, redis-1, etc"

Compose's default container-naming scheme (`<project>-<service>-<replica>`, e.g. `novawalletledgerservice-api-1`) is verbose and non-obvious to `docker ps`/`docker logs`/`docker exec` at a glance. Added an explicit `container_name:` to all seven services in `docker-compose.yml` (`postgres`, `redis`, `rabbitmq`, `migrator`, `api`, `otel-collector`, `openobserve`), each prefixed `novawallet-` to match the existing volume-naming convention (`novawallet-postgres-data`, etc.) — so e.g. `docker logs novawallet-api` now works directly.

One thing checked before making this change: whether hardcoding `container_name` would break anything that relies on Compose's automatic per-service DNS aliasing (e.g. `api`'s `NOVAWALLET_LEDGER_CONNECTION_STRING` uses `Host=postgres`, and `REDIS_URI`/`ObservabilityOptions__ExporterUri` address `redis`/`otel-collector` by service name). It doesn't: Compose registers both the service name and an explicit `container_name` as network aliases on the shared bridge network, so none of the inter-service hostnames needed to change. Grepped the repo first for any other place the old auto-generated names (`novawalletledgerservice-*-1`) might already be referenced (docs, scripts) — none were found, so no other files needed updates. The one tradeoff worth naming: `container_name` disables `docker compose up --scale <service>=N` for that service (fixed names can't be shared by multiple replicas) — a non-issue here, since nothing in this compose file is ever scaled.

#### Prompt 12 — "apply meaningful memory caps to the docker compose"

Added a `deploy.resources.limits`/`reservations.memory` block to all seven services in `docker-compose.yml`. Worth naming explicitly: the `deploy` key's origins are Swarm-only, but this stack uses Compose V2 (`docker compose`, the CLI plugin — not the legacy `docker-compose` v1 binary), which does apply `deploy.resources` on a plain `docker compose up`, no Swarm mode required. Sizes were picked per service's actual footprint rather than one blanket number: `postgres`/`rabbitmq` 512M (Erlang VM + management plugin baseline is non-trivial), `api` 768M (a .NET 9 Minimal API host with EF Core, an OTel SDK, and a Redis client has more headroom than a bare console app), `openobserve` 1024M, `redis`/`migrator`/`otel-collector` 256M each.

Two follow-on judgment calls, not just mechanical additions:

1. **Redis got its own `--maxmemory 200mb --maxmemory-policy allkeys-lru`**, set below its 256M cgroup cap. Without this, Redis has no internal ceiling of its own — it grows until the kernel OOM-kills the whole container, dropping every connection at once. With `--maxmemory` + `allkeys-lru`, Redis evicts its own least-recently-used keys under pressure and keeps serving. This is safe specifically because everything cached there (idempotency keys via `HybridCache`) is a cache, not a system of record — evicting one just means the next identical request re-executes instead of hitting the fast path, never a correctness problem.
2. **OpenObserve's cap is the one genuinely uncertain call.** Its own boot log (pasted earlier this session) reported auto-sizing its memory cache and DataFusion query pool as a fraction of *host-visible* RAM — "MEM max size 1.90 GB, Datafusion pool size: 2.85 GB" on a 7.6 GB host — not the container's cgroup limit. Capping it at 1024M is a bet that those are lazily-filled ceilings rather than upfront allocations, reasonable for the light single-API ingest/query volume this take-home stack actually produces, but not something confirmed against OpenObserve's own internals or current docs (no web access in this session to verify the exact `ZO_MEMORY_CACHE_*` tuning variables some quick research suggested exist). Documented this honestly as an unverified assumption in both `docker-compose.yml` and the README, with the concrete symptom to watch for (exit code 137 / `OOMKilled: true`) and the fix (raise the limit) rather than asserting it's correct.

#### Prompt 13 — "Modify the reconciliation worker. reconcile accounts with transaction after last snapshot"

Until this point, `ReconciliationWorker` re-summed an account's **entire** `AccountEntries` history on every single sweep tick, forever — correct, but a cost that scales linearly with an account's lifetime transaction count, re-paid every `PollingIntervalSeconds` for the life of the wallet. The ask was to make each sweep only reconcile the transactions posted *since* the wallet's last snapshot, rather than the whole ledger again.

Implemented as a rolling baseline: each `LedgerSnapshot` row now also carries `LastAccountEntryId` — the highest `AccountEntries.Id` folded into that snapshot's `LedgerBalanceKobo`. The next sweep for that wallet computes `LastSnapshot.LedgerBalanceKobo + Sum(AccountEntries newer than LastAccountEntryId)` instead of summing from scratch. The one non-obvious piece: `AccountEntries.Id` is already a sequential UUID (`Uuid.NewSequential()`, the same v7-style scheme used for the wallet keyset cursor a few lines above in the same file), so it's directly usable as a monotonic watermark with no new column type or extra index — comparing UUIDs the same way the existing wallet-sweep cursor already does. A wallet with no prior snapshot still sums its whole history once (first sweep only, `prior` comes back `NULL` from the `LEFT JOIN LATERAL`), so no wallet ever silently skips its pre-existing history; every sweep after that is bounded by "how much moved since last tick," not "how much has ever moved." The whole read stayed a single Postgres statement — `Wallets` LEFT JOIN LATERAL'd to each wallet's latest snapshot, LEFT JOIN LATERAL'd again to the bounded delta sum — which matters because the existing "immediate auto-freeze, no grace period" design rests entirely on the single-statement MVCC-consistency argument; changing that to two round trips would have quietly undermined the justification for auto-freeze without anyone deciding that on purpose.

One race condition was surfaced and deliberately **not** engineered around, only documented: since `AccountEntry.Id` is generated client-side when the entry object is constructed, not by a DB sequence at the moment of commit, two concurrent postings could — in a narrow window — commit in the opposite order from their ID values (the numerically-lower-ID transaction's commit lands after the higher-ID one's). If a sweep's watermark advances past the higher ID before the lower one commits, that lower-ID entry would be permanently invisible to every future incremental sum. Fixing this properly would mean a time-delayed watermark (only advancing the cursor past entries older than some grace window) — flagged as a reasonable follow-up rather than implemented speculatively, since adding unrequested complexity for a race window that may never manifest at this system's scale would be its own risk. This is called out explicitly in three places (`LedgerSnapshot`'s and `ReconciliationWorker`'s XML doc remarks, and README §3) rather than left as a silent gap, matching this document's practice of treating asymmetries/edge-cases in balance-affecting code as something to name, not something to quietly hope never happens.

Since `LedgerSnapshot` was already flagged in this README as a table with no hand-authored migration yet, adding `LastAccountEntryId` cost nothing extra on that front — it's still one not-yet-written migration to catch up on, just with one more nullable column than before.

### Documentation and review (claude.ai)

**Gap review against the brief.**
> *"What about the gaps? — review my architecture doc and schema against the take-home brief."*

Given the full README draft and DDL, returned a structured gap list: the idempotency mechanism relied on a bare unique constraint on `IdempotencyKey` with no request-payload-hash comparison (so a legitimate replay would throw a raw `23505` constraint violation instead of returning the cached response); no explicit double-entry ledger table existed despite the `AccountType` enum implying one; and the transfer-flow narrative in the README described a check-then-act sequence that directly contradicted the atomic-`UPDATE` approach documented elsewhere in the same file. Every item became a concrete README/DDL fix.

**Entity model refactor.**
> *"Use private set; init; & factory methods where necessary & also include their entity configuration files."*

Restructured a first-pass EF Core model (public setters, inline `OnModelCreating`) around private constructors (used via EF's constructor binding), public static `Create()` factories that validate invariants before an instance can exist, and behavior methods instead of open setters — then split the Fluent API into one `IEntityTypeConfiguration<T>` per entity, so `AccountEntry` can only ever be constructed through its parent `JournalEntry`, enforcing the aggregate boundary structurally rather than by convention.

---

## A specific case where AI output was wrong/unsafe for a financial system

**The daily transfer limit's `INSERT` branch didn't enforce the limit.**

The atomic `UPSERT` guarding the ₦500,000/day outbound transfer limit (`WalletDailyUsage`, README §6) went through an earlier version that looked like this:

```sql
INSERT INTO "WalletDailyUsage" ("WalletId", "UsageDate", "TotalSpentKobo")
VALUES (@WalletId, @UsageDate, @AmountKobo)
ON CONFLICT ("WalletId", "UsageDate")
DO UPDATE SET
    "TotalSpentKobo" = "WalletDailyUsage"."TotalSpentKobo" + EXCLUDED."TotalSpentKobo"
WHERE "WalletDailyUsage"."TotalSpentKobo" + EXCLUDED."TotalSpentKobo" <= @DailyLimitKobo;
```

The `WHERE` clause on the `UPDATE` branch is correct — it makes the update a no-op (0 rows affected) if applying this transfer would push the day's total over the limit, and the caller treats 0 rows affected as "limit breached, roll back." The bug: **the `INSERT` branch has no equivalent guard.** For any wallet's *first* transfer of the day (no existing `WalletDailyUsage` row for that `WalletId`/`UsageDate`), Postgres takes the `INSERT` path, which always succeeds regardless of `@AmountKobo` versus `@DailyLimitKobo` — so a single transfer of, say, ₦10,000,000 on a wallet's first attempt of the day would have gone through untouched by the limit, and only *subsequent* transfers that same day would ever hit the guard. This is exactly backwards for a control that exists to cap exposure per wallet per day: it protected every case except the one most likely to be a large, single fraudulent or erroneous transfer.

This was not something a type checker or a happy-path test would catch — the `UPDATE` branch working correctly makes the feature look done, and a test suite that always seeds a `WalletDailyUsage` row before asserting limit behavior would never exercise the `INSERT` branch at all. It was caught by deliberately tracing *both* branches of the `ON CONFLICT` against the requirement ("the limit applies to the day's total, not just to already-recorded days") rather than trusting that a `WHERE`-guarded `UPDATE` implied the whole statement was guarded.

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

Turning the bare `VALUES (...)` into a guarded `SELECT ... WHERE @AmountKobo <= @DailyLimitKobo` means the `INSERT` branch now also yields 0 affected rows — and therefore also triggers the "limit breached" rollback path — when a wallet's first transfer of the day already exceeds the daily cap on its own. (This example is summarized from the project's real build history, traceable via README §6's own note and git history, not reproduced as a verbatim chat transcript.)

## A second example: naive full-history `SUM()` reconciliation

Early in reconciliation design, the first proposal for the periodic balance-reconciliation query was a straightforward aggregate:

```sql
SELECT w."Id" AS WalletId, w."AvailableBalanceKobo",
       COALESCE(SUM(e."CreditAmountKobo" - e."DebitAmountKobo"), 0) AS CalculatedLedgerBalance
FROM "Wallets" w
JOIN "Accounts" a ON a."Id" = w."AccountId"
LEFT JOIN "AccountEntries" e ON e."AccountId" = a."Id"
GROUP BY w."Id", w."AvailableBalanceKobo";
```

Correct on a small seed dataset, but unsafe at real ledger volume: it re-sums every `AccountEntries` row for every wallet, on every scheduled run, with no bound on how far back it scans. As the ledger grows to months or years of history, this becomes an unbounded O(N) scan competing directly with the live transfer path's `INSERT`s into the same table — the reconciliation job would get slower every day it ran, and would degrade production write latency while doing it.

Caught the same way as the daily-limit gap above — by tracing the design against realistic data volume rather than the seed dataset it was demonstrated against. Fixed by replacing the unbounded sweep with a snapshot-based closing balance (`LedgerSnapshots(AccountId, SnapshotDate, ClosingBalanceKobo, LastJournalEntryId)`), re-summing only entries posted since the latest snapshot:

```sql
WITH LastSnapshot AS (
    SELECT "ClosingBalanceKobo", "SnapshotDate"
    FROM "LedgerSnapshots"
    WHERE "AccountId" = @AccountId
    ORDER BY "SnapshotDate" DESC
    LIMIT 1
)
SELECT COALESCE(s."ClosingBalanceKobo", 0)
     + COALESCE(SUM(e."CreditAmountKobo" - e."DebitAmountKobo"), 0) AS CalculatedBalanceKobo
FROM "AccountEntries" e
LEFT JOIN LastSnapshot s ON TRUE
WHERE e."AccountId" = @AccountId
  AND (s."SnapshotDate" IS NULL OR e."CreatedAt" > s."SnapshotDate")
GROUP BY s."ClosingBalanceKobo";
```

This turns the scan from O(N) total history into O(k) — only the entries since the last snapshot — and the query runs against a read replica, so reconciliation reads never contend with the write path at all. The snapshot mechanism itself was later refined further during implementation (Prompt 13 above) into a per-sweep watermark, so each tick only re-sums since the *previous tick*, not since the last full snapshot.

---

## Where this leaves review responsibility

Both examples above share a shape: the AI produced code that was *locally* plausible — it compiled (mentally, given the build-tooling constraint noted above), matched the pattern of surrounding code, and handled the case someone would naturally test or demo first — but was wrong on a case that only shows up when you deliberately ask "what about the other branch / the other code path doing the same job / what happens at real volume instead of the seed dataset?" For a system that moves real money, that means AI-authored financial logic was never treated as trustworthy by default here; the practice adopted was to explicitly interrogate boundary/asymmetry cases (first-of-day, wallet-vanished-vs-not-Active, INSERT-vs-UPDATE, unbounded-vs-bounded reconciliation scans) as a follow-up review step on every AI-authored change to a balance-affecting code path, rather than relying on the AI to have already considered them.