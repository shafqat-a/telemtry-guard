# Runbook: model retraining loop (registry, promotion, rollback)

P2-02 (Phase 2). Audience: an operator with **no production traffic** — everything
below drives the loop end to end using only SDK-06's synthetic-bot fixture path
against the local compose stack. `TelemetryGuard.Training` is the offline console;
nothing in this runbook trains inside the API host (D3 — training compute stays
offline) and **CI never runs `retrain`**, `promote`, or `rollback` against a real
database.

The heuristic (`heuristic-1`) enforces until a human explicitly runs
`promote --status active`. No command in this loop can flip enforcement
automatically (D18) — `retrain` only ever registers `candidate` or `rejected` rows.

## 0. Prerequisites

- `./scripts/dev-up.sh && ./scripts/dev-seed.sh` — brings up `mssql`/`redis`/
  `clickhouse`/`grafana` and applies migrations + seeds a demo tenant/site key/
  campaign. Note the seeded site key printed by `dev-seed.sh`.
- Run the API host with synthetic traffic accepted:
  `Synthetic__Enabled=true dotnet run --project TelemetryGuard.Api`
  (or set `"Synthetic": { "Enabled": true }` in `TelemetryGuard.Api/
  appsettings.Development.json`, which already ships this on for local dev).
- `TelemetryGuard.Training/appsettings.json` already points `ConnectionStrings:
  ClickHouse`/`ConnectionStrings:Main` at the compose stack's defaults — override
  per command with environment variables (`Training__Retrain__MinPositives=20`, …)
  rather than editing the file for a one-off run.

## 1. Generate fixture traffic (positives)

```bash
cd TelemetryGuard.Sdk
npm run bot-traffic -- --site-key <key> --runs 40 --profile linear
npm run bot-traffic -- --site-key <key> --runs 40 --profile grid
npm run bot-traffic -- --site-key <key> --runs 40 --profile jitter
```

Each Playwright run carries `X-TG-Synthetic: <runId>`; because `Synthetic:Enabled`
is on, the API writes a guaranteed `synthetic_bot` fraud label at ingest for every
run. T1 rule hits (if any fire on the bot traffic) also land live as `t1_rule`
labels via API-06 — `LabelResolver` treats `synthetic_bot` as the guaranteed source,
so a `t1_rule` duplicate never overrides it.

## 2. Generate fixture traffic (negatives)

For a handful of clean sessions, whitelist them through the review screen so DAT-07
emits `legit`/`review_screen` labels (D19):

```bash
curl -X POST http://localhost:5120/admin/whitelist \
  -H "Content-Type: application/json" \
  -d '{"type":"ip","value":"203.0.113.50","source":"review_screen","sessionId":"<sid-8to64-chars>","reason":"real customer"}'
```

API-07 requires `sessionId` to match `^[A-Za-z0-9_-]{8,64}$` when
`source=review_screen` — use a real session id captured from a clean browser visit
to the tracked landing page (not a bot run).

## 3. Retrain with fixture-sized thresholds

The shipped defaults (`Training:Retrain:MinPositives`/`MinNegatives` = 500,
`Training:MinAuc` = 0.85) are production-sized. Override them for a dev walkthrough:

```bash
Training__Retrain__MinPositives=20 \
Training__Retrain__MinNegatives=20 \
Training__MinAuc=0.6 \
dotnet run --project TelemetryGuard.Training -- retrain --from <yyyy-MM-dd> --to <yyyy-MM-dd>
```

Steps this runs, in order (each prints a line, exit codes documented in
`-- --help`): cadence guard → rebuild labels (`build-labels`, unless
`--skip-label-build`) → feature-set-boundary guard → load the training set → train +
export → measure single-row serving latency (p99) → compare against the incumbent →
register the outcome. On success:

```
Registered candidate lgbm-20260915-a1b2c3d4. Nothing is serving it.
Next: dotnet run --project TelemetryGuard.Training -- promote --model lgbm-20260915-a1b2c3d4 --status shadow
```

Exit 2 means the candidate was registered as `rejected` (AUC gate or incumbent
regression) — it can never be promoted. Exit 3 means nothing was registered at all
(too few labels, or the window straddles a `feature_set_version` boundary — narrow
`--from`/`--to`).

## 4. Promote to shadow, restart, generate more traffic, check divergence

```bash
dotnet run --project TelemetryGuard.Training -- promote --model lgbm-20260915-a1b2c3d4 --status shadow
```

**Restart the API host** — a running process never changes its enforcing scorer
(D18 lineage); promotion only takes effect on the next boot, when
`ModelRegistryBootstrap` re-reads `dbo.ModelRegistry`. After restart, verdicts still
stamp `scorer_version = heuristic-1` (shadow never enforces) but also populate
`shadow_score`/`shadow_scorer_version` for every non-whitelisted session.

Generate more fixture traffic (step 1), then:

```bash
dotnet run --project TelemetryGuard.Training -- divergence --from <d> --to <d>
```

`--model` defaults to whichever row is currently `shadow`. This prints the
band-agreement matrix, mean absolute delta, would-block-more/fewer counts, and a
rank-AUC comparison on the labeled subset, and writes
`artifacts/divergence/divergence-<from>_<to>.json` (already `.gitignore`d).
Rule-floored sessions (any T1 hit) are excluded from the comparison and reported
separately — the enforced score already has the T1 floor folded in, the shadow
score never does.

## 5. Promote to active, restart, confirm the model is enforcing

The production gate (`Training:Promotion:*`) expects a real listen-only pilot
(§11.4's proposed 14-day window, `MinShadowHours = 336`) with thousands of
shadow-scored sessions. For a dev walkthrough, relax it or use `--force`:

```bash
Training__Promotion__MinShadowHours=0 \
Training__Promotion__MinShadowSessions=10 \
Training__Promotion__MinLabeledShadowSessions=5 \
dotnet run --project TelemetryGuard.Training -- promote --model lgbm-20260915-a1b2c3d4 --status active

# or, to see every unmet reason and bypass anyway:
dotnet run --project TelemetryGuard.Training -- promote --model lgbm-20260915-a1b2c3d4 --status active --force
```

`--force` records `Notes` starting with `FORCED: <reasons>` — it can never promote a
`rejected` row (that transition is refused unconditionally, exit code 4).

**Restart the API host again.** Confirm new verdicts stamp
`scorer_version = lgbm-20260915-a1b2c3d4` (query `tg_events` or watch the JSON
console log). The model now enforces the band/challenge/block decision and the
exclusion queue; the heuristic no longer does.

## 6. Rollback

```bash
dotnet run --project TelemetryGuard.Training -- rollback
```

With no `--to`, this targets the most recently retired row that once carried
`ActiveSinceUtc` (i.e. the previous active generation — `heuristic-1` has no
registry row, so a first-ever rollback with nothing else ever promoted refuses with
exit code 4: *"nothing to roll back to — use `promote --status active` explicitly"*).
To fall back to the heuristic explicitly:

```bash
dotnet run --project TelemetryGuard.Training -- rollback --to none
```

**Restart the API host.** Confirm verdicts stamp `scorer_version = heuristic-1`
again — the registry now holds no `active` row, so `ModelRegistryBootstrap` resolves
to the pure heuristic at the next boot regardless of what `Scoring:ModelPath` says
in `appsettings.json` (it is explicitly cleared).

## 7. Cron / ops

A scheduled `retrain` on the training host (never the API host — D3), e.g. weekly:

```cron
0 3 * * 1  cd /opt/telemetry-guard && dotnet run --project TelemetryGuard.Training -- retrain >> /var/log/tg-retrain.log 2>&1
```

Safe to run more often than that: the cadence guard
(`Training:Retrain:MinIntervalHours`, default 144h = 6 days) makes an early
re-invocation a harmless no-op (exit 0, nothing registered) unless `--force` is
passed. `retrain` never promotes anything by itself, so a scheduled run is exactly
as safe as a human running it manually and walking away before the `promote` step.

**CI never trains.** `.github/workflows/ci.yml` runs the unit and Testcontainers
integration suites only; no CI step invokes `retrain`, `promote`, or `rollback`
against a real database, and no trained model or divergence dump is ever committed
(`artifacts/` stays `.gitignore`d).

## Command reference

```
build-labels --from <yyyy-MM-dd> --to <yyyy-MM-dd>
train        --from <yyyy-MM-dd> --to <yyyy-MM-dd> [--out <dir>]
retrain      [--from <d>] [--to <d>] [--out <dir>] [--skip-label-build] [--force]
divergence   --from <d> --to <d> [--model <scorer-version>] [--out <dir>]
promote      --model <scorer-version> --status shadow|active [--note "<text>"] [--force]
rollback     [--to <scorer-version>|none] [--note "<text>"]
models       [--limit 20]

Exit codes: 0 = success (or --help/nothing to do), 1 = bad args/config,
            2 = training/candidate gate failed (run recorded as 'rejected'),
            3 = not enough labels / mixed feature_set_version (nothing recorded),
            4 = promotion or rollback refused (illegal transition or unmet gate).
```
