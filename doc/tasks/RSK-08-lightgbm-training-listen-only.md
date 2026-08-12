---
id: RSK-08
title: "Label pipeline, LightGBM training, listen-only rollout"
phase: 1.5
workstream: risk
depends_on: [RSK-07, API-06, SDK-06, ANA-02, DAT-07]
size: L
spec_refs: [D1, D4, D18, D19, "§10"]
detail_level: full
---

# RSK-08: Label pipeline, LightGBM training, listen-only rollout

## Objective

Build the Phase-1.5 model path: a new `TelemetryGuard.Training` console project (added to the solution here) that extracts labels from live traffic into ANA-02's existing `tg_labels` ClickHouse table, joins labels with stored per-session feature vectors into an ML.NET `IDataView`, trains an ML.NET **LightGBM binary classifier** with a time-based split and an AUC quality gate, and exports `model.zip` + `metadata.json` (scorer version `lgbm-<date>-<hash>`, `feature_set_version`). Serving side: `MlNetScorer : IScorer` via `PredictionEnginePool`, plus `ScoringOptions.Mode = Enforce | ListenOnly` — in ListenOnly the pipeline keeps enforcing the heuristic while logging the model's `shadow_score`/`shadow_scorer_version` (small ClickHouse schema-addition migration included). Heuristic → model swap is a config change only (D18).

## Spec context (self-contained)

- D18 (cold start): no pre-launch labels exist. Label sources once live traffic flows: **T1 rule hits = weak fraud positives; Playwright-generated synthetic bot runs (SDK-06) = guaranteed positives; tenant-confirmed conversions and review-screen/whitelist "real customer" marks (D19) = negatives.** (Conversion intake has NO producing task anywhere in the plan yet — that negative source is DEFERRED, see step 3c.) The pilot dataset runs in **listen-only mode** (model scored and logged, NOT enforcing) before the first trained model takes over. Every verdict is stamped with `scorer_version` so heuristic-era and model-era data are never confused.
- D4: ML.NET trains **LightGBM** natively — gradient-boosted trees are the right class for tabular fraud features; LightGBM branches on NaN natively, which is exactly why the vector uses NaN-for-missing (spec §7). Training and serving share one language/toolchain (D1 — no Python).
- D19: the override loop doubles as the labeling loop — a whitelist/"real customer" mark writes a negative label.
- D7/D23 data placement: raw events and verdicts live in ClickHouse (events table owned by ANA-02, written via ANA-03/API-06); training reads ClickHouse directly (it is the offline side, not the portal path). SQL Server holds config/whitelists (DAT-07).
- Swap mechanics (D18): `IScorer` is the seam — `HeuristicScorer` ("heuristic-1", RSK-06) vs `MlNetScorer` ("lgbm-…") selected by config, no rewrite.
- < 50 ms budget still applies to serving: `PredictionEnginePool` (thread-safe, pooled) keeps inference in-process and sub-millisecond.

## Prerequisites

- RSK-07: `ScoringPipeline`, `ScoringOutcome`, `IScoringPipeline` (this task modifies the pipeline for shadow scoring); RSK-06 `HeuristicScorer`; RSK-01 contracts (`FraudFeatureVector`, `IScorer`, `ScoreResult`, `AsnType`, `ChallengeOutcome`).
- API-06: verdict finalization persists one event row per session to the ClickHouse events table including `session_id`, `tenant_id`, `timestamp`, `score`, `rule_hits`, `scorer_version`, and — REQUIRED by this task — a `features` column holding the JSON-serialized `FraudFeatureVector` captured at scoring time. Read the API-06 and ANA-02 task files for the exact events-table name and columns (assumed `tg_events` below; adjust queries to the real name). If the `features` column does not exist yet, THIS task adds it (step 5 migration) and API-06's writer must be extended to populate it (one-line change; coordinate via the migration's comment).
- SDK-06 (with API-04): synthetic bot sessions are identified by the `X-TG-Synthetic: <runId>` header that `scripts/gen-bot-traffic.ts` sends on every browser request; the API honors it ONLY under dev config `Synthetic:Enabled=true`, and when honored, ingest writes `tg_labels` rows with `label='fraud'`, `label_source='synthetic_bot'` (plus the runId) at capture time. Guaranteed positives therefore ALREADY EXIST in `tg_labels` — this task consumes them (step 3b); it does NOT identify synthetic traffic by tenant id, and there is no `Training:SyntheticTenantIds` setting.
- ANA-02 (in depends_on): OWNS the ClickHouse `tg_labels` table (`schema/0001_events.sql`: `tenant_id UUID, session_id String, label LowCardinality(String) 'fraud'|'legit', label_source LowCardinality(String), created_at DateTime64(3,'UTC')`; plain MergeTree; fixed 400-day TTL) and the `SchemaMigrator` mechanism this task's step-5 script rides on. This task must NOT re-create or re-shape that table — step 5 only ALTERs.
- DAT-07 (in depends_on): whitelist tables in SQL Server (negatives source), read via Dapper with tenant-bound connections; its review-screen adds also emit live `'review_screen'` labels via `ILabelSink`.
- FND-02: ClickHouse + SQL Server reachable from the dev stack.

## Implementation steps

1. **Project**: create `TelemetryGuard.Training/TelemetryGuard.Training.csproj` (console, `net8.0`) and `dotnet sln add` it. Packages: `Microsoft.ML` , `Microsoft.ML.LightGbm`, `System.CommandLine` (or hand-rolled arg parsing — keep it simple), the ClickHouse client package ANA-03 uses (read the ANA-03 task file; expected `ClickHouse.Client`), `Microsoft.Data.SqlClient` + `Dapper`, `Microsoft.Extensions.Configuration.*` (Json + EnvironmentVariables). Project references: `TelemetryGuard.RiskEngine.Contracts` (and `TelemetryGuard.RiskEngine` ONLY if reusing its JSON options — keep the dependency surface minimal).

2. **Shared ML row + mapper** — in `TelemetryGuard.RiskEngine.Contracts` (plain POCO, zero package deps, usable by both training and serving):
   - `MlFeatureRow.cs`: a class with ONE public `float` field per model input, in this FROZEN v1 order (bool? → 1/0/NaN; bool → 1/0; NaN preserved; enums one-hot):
     1–14 (T1): `honeypot_touched, click_before_render, pointer_untrusted, webdriver_flag, headless_browser, beacon_integrity_failed` (1 when explicitly false!), `ip_tor, tls_ua_mismatch, ip_datacenter_asn, ip_clicks_last_min, ua_os_mismatch, mouse_path_linearity, std_inter_event_ms, click_id_invalid`;
     15–28 (T2): `has_js_beacon, emulator_or_vm, screen_res_anomalous, storage_age_zero_repeat, ip_proxy_or_vpn, ip_geo_target_mismatch, time_on_page_sec, form_fill_time_sec, first_interaction_delay_ms, input_modality_mismatch, mean_inter_event_ms, device_sessions_last_hour, ip_distinct_uas_last_hour, device_ids_this_ip_hour`;
     29–38 (T3): `cookies_disabled, canvas_fp_blocked, timezone_ip_mismatch, language_geo_mismatch, ip_reputation_bad, paste_in_identity_fields, referrer_missing, input_event_count, scroll_events, pages_viewed`;
     39–50 (CTX): `is_mobile, asn_residential, asn_mobile, asn_business, asn_datacenter, asn_education, asn_government, asn_cdn` (AsnType one-hot, Unknown = all zero), `is_private_relay, form_submitted, autofill_detected, is_paid_click` — `challenge_outcome` is EXCLUDED from model inputs (it does not exist at first-score time; including it would leak the label-time state).
     Plus `public bool Label;` and `public float Weight;` fields used only in training.
   - `MlFeatureMapper.cs`: `static MlFeatureRow ToRow(FraudFeatureVector v)` implementing the encoding, with `beacon_integrity_failed = v.BeaconIntegrityOk == false ? 1 : v.BeaconIntegrityOk == null ? float.NaN : 0` (encode the FIRING direction, mirroring rule 6 of RSK-05); doc-comment: "column order is feature_set_version 1; any change bumps FeatureSetVersion".

3. **Label pipeline** (`TelemetryGuard.Training/LabelBuilder.cs`, command `build-labels`) — writes ANA-02's EXISTING `tg_labels` shape (see Prerequisites; step 5 adds a `weight Float32 DEFAULT 1` column). Values follow ANA-01's contract: `label` ∈ `'fraud'|'legit'` (LabelValues), `label_source` ∈ the LabelSources strings, `tenant_id` as UUID, timestamp column `created_at`:
   - Reads config `appsettings.json` in the Training project: `ConnectionStrings:ClickHouse`, `ConnectionStrings:Main` (SQL), `Training:MinAuc` (default 0.85), `Training:T1PositiveWeight` (0.6).
   - Sources:
     a. **Weak positives (backfill)**: sessions in `tg_events` within `[--from, --to)` where `length(rule_hits) > 0` AND NOT whitelisted-flag (exclude `'whitelisted'` hit) → INSERT `label='fraud', label_source='t1_rule', weight=T1PositiveWeight`. NOTE: API-06 already writes these labels LIVE at verdict finalization via `ILabelSink` — duplicates are EXPECTED and resolved at read time (step 4); the table is a plain MergeTree, nothing dedupes on disk.
     b. **Guaranteed positives**: already written AT INGEST by API-04 for sessions carrying SDK-06's `X-TG-Synthetic` header (`label='fraud', label_source='synthetic_bot'`, honored only under `Synthetic:Enabled` dev config). LabelBuilder does NOT derive these — it only counts and logs how many fall inside the window.
     c. **Negatives — conversions: DEFERRED.** No task produces a conversion indicator anywhere (no endpoint, no `EventKind`, no `ClickEvent` field, no ClickHouse column). Enabling this source requires a conversion-intake task first (suggested id API-09, phase 1.5, API workstream: e.g. `POST /convert?k=..&sid=..` called from the tenant thank-you page, or an admin import of confirmed conversions, writing `LabelEvent(Legit, Conversion)` through the existing `ILabelSink`). Until that task exists, LabelBuilder logs `"conversion label source deferred — no producer (see API-09)"` and skips it; do NOT invent or assume an indicator column.
     d. **Negatives — review marks (D19)**: DAT-07 already emits `label='legit', label_source='review_screen'` LIVE via `ILabelSink` when a review-screen whitelist add carries a SessionId. LabelBuilder only backfills entries created in the window that lack a label row: query DAT-07 whitelist entries (SQL, Dapper, tenant-bound connections per tenant being processed), join matching sessions on ip/visitor id in `tg_events` → INSERT `label='legit', label_source='review_screen', weight=1.0`. Use ANA-01's canonical `LabelSources.ReviewScreen` string `'review_screen'` — never a new `'review_mark'` value.
   - Conflict/duplicate resolution is a READ-TIME concern (step 4): guaranteed sources (`synthetic_bot`, `review_screen`, `conversion`) beat `t1_rule`; a session with contradictory guaranteed labels is DROPPED with a logged count.
   - CLI: `dotnet run --project TelemetryGuard.Training -- build-labels --from 2026-08-01 --to 2026-08-31`.

4. **Training** (`TelemetryGuard.Training/Trainer.cs`, command `train`):
   - Query: first DEDUPE `tg_labels` per `(tenant_id, session_id)` — the table is ANA-02's plain MergeTree, so the query must collapse duplicates and resolve conflicts itself: rank sources (guaranteed `synthetic_bot`/`review_screen`/`conversion` > `t1_rule`), e.g. `argMax(label, (source_priority, created_at))` grouped by `(tenant_id, session_id)`, dropping (and counting) sessions with contradictory guaranteed labels. Map `label`: `'fraud'` → 1, `'legit'` → 0. Effective `Weight` is computed AT READ TIME: `label_source='t1_rule'` → `Training:T1PositiveWeight`, else the stored `weight`. Then join with `tg_events` on `(tenant_id, session_id)` in the window; deserialize the `features` JSON column into `FraudFeatureVector` (System.Text.Json, same options as the writer); map via `MlFeatureMapper.ToRow`, attach label/weight; stream into `mlContext.Data.LoadFromEnumerable<MlFeatureRow>`.
   - **Time-based split** (never random — fraud drifts): order by event `timestamp`; first 80 % = train, last 20 % = validation.
   - Pipeline: `Concatenate("Features", all 50 feature field names)` → `mlContext.BinaryClassification.Trainers.LightGbm(new LightGbmBinaryTrainerOptions { LabelColumnName = "Label", ExampleWeightColumnName = "Weight", FeatureColumnName = "Features", NumberOfLeaves = 31, NumberOfIterations = 200, LearningRate = 0.05, MinimumExampleCountPerLeaf = 20, HandleMissingValue = true, UnbalancedSets = true })`. `HandleMissingValue = true` is MANDATORY — it is the NaN-branching behavior the whole missing≠zero design relies on.
   - Evaluate on the validation slice: `mlContext.BinaryClassification.Evaluate(...)` → AUC, AUPRC, F1. **Gate: `AUC < Training:MinAuc` → print metrics, exit code 2, export NOTHING.**
   - Export on pass: `artifacts/models/{scorerVersion}/model.zip` (`mlContext.Model.Save`) where `scorerVersion = $"lgbm-{yyyyMMdd}-{hash8}"`, `hash8` = first 8 hex chars of SHA-256 of `model.zip`; write `metadata.json` alongside:

```json
{
  "scorer_version": "lgbm-20260915-a1b2c3d4",
  "feature_set_version": 1,
  "trained_at_utc": "2026-09-15T10:11:12Z",
  "window": { "from": "2026-08-01", "to": "2026-08-31" },
  "rows": { "train": 0, "validation": 0, "positives": 0, "negatives": 0, "dropped_conflicts": 0 },
  "metrics": { "auc": 0.0, "auprc": 0.0, "f1": 0.0 },
  "min_auc_gate": 0.85
}
```

   - CLI: `-- train --from 2026-08-01 --to 2026-08-31 --out artifacts/models`.

5. **ClickHouse schema addition** — new numbered script in `TelemetryGuard.Analytics.ClickHouse/schema/` (next free number; check the directory — e.g. `004_training_and_shadow.sql`), run by the same ANA-02 schema mechanism:

```sql
-- RSK-08: training weight column + listen-only shadow columns + serialized feature vector.
-- tg_labels itself is OWNED BY ANA-02 (schema/0001_events.sql): tenant_id UUID,
-- session_id String, label 'fraud'|'legit', label_source LowCardinality(String),
-- created_at DateTime64(3,'UTC'), plain MergeTree, fixed 400-day TTL.
-- Do NOT re-create or re-shape it here — ALTER only. Duplicate/conflicting label rows
-- are resolved at training-read time (step 4), not by the storage engine.
-- NOTE for API-06 owner: populate `features` (JSON of FraudFeatureVector) and, when
-- Scoring:Mode=ListenOnly, shadow_score/shadow_scorer_version on verdict finalization.
ALTER TABLE tg_labels ADD COLUMN IF NOT EXISTS weight Float32 DEFAULT 1;

ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS features String DEFAULT '';
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS shadow_score Nullable(UInt8);
ALTER TABLE tg_events ADD COLUMN IF NOT EXISTS shadow_scorer_version LowCardinality(String) DEFAULT '';
```

(The `weight` default of 1 keeps ANA-03's `ClickHouseLabelSink` inserts — which name only the five ANA-02 columns — working unchanged; live-written `t1_rule` rows therefore store weight 1, and the trainer applies `Training:T1PositiveWeight` at read time instead, step 4. `tg_labels` keeps ANA-02's fixed 400-day TTL — labels must outlive the raw-event window; the join naturally shrinks as `tg_events` rows expire per D20.)

6. **Serving — `MlNetScorer`** (`TelemetryGuard.RiskEngine/Scoring/MlNetScorer.cs`; add packages `Microsoft.ML`, `Microsoft.ML.LightGbm`, `Microsoft.Extensions.ML` to `TelemetryGuard.RiskEngine.csproj`):
   - `public sealed class MlPrediction { [ColumnName("PredictedLabel")] public bool PredictedLabel; public float Probability; public float Score; }`
   - `MlNetScorer(PredictionEnginePool<MlFeatureRow, MlPrediction> pool, string scorerVersion) : IScorer` — `Score(in FraudFeatureVector v)`: `var row = MlFeatureMapper.ToRow(v); var p = pool.Predict(row); var score = (int)Math.Clamp(Math.Round(p.Probability * 100), 0, 100); return new ScoreResult(score, Array.Empty<string>(), scorerVersion, FraudFeatureVector.FeatureSetVersion);`
   - `scorerVersion` is read from `metadata.json` next to the model file at registration time — NEVER hard-coded.

7. **ScoringOptions + pipeline modification** (`TelemetryGuard.RiskEngine/Scoring/ScoringOptions.cs`, config section `"Scoring"`):

```csharp
public enum ScoringMode { Enforce, ListenOnly }
public sealed class ScoringOptions
{
    public string Scorer { get; set; } = "Heuristic";   // "Heuristic" | "MlNet"
    public ScoringMode Mode { get; set; } = ScoringMode.Enforce;
    public string? ModelPath { get; set; }              // dir containing model.zip + metadata.json
}
```

   - DI (`AddScoringPipeline` update): `Scorer == "MlNet"` → `AddPredictionEnginePool<MlFeatureRow, MlPrediction>().FromFile(Path.Combine(ModelPath, "model.zip"), watchForChanges: true)`; register `MlNetScorer` as `IScorer`. `Mode == ListenOnly` → the ENFORCING `IScorer` stays `HeuristicScorer` regardless of `Scorer`, and the MlNet scorer is registered as a keyed/secondary `IShadowScorer` (marker interface wrapping `IScorer`).
   - `ScoringOutcome` (RSK-07) gains `int? ShadowScore = null, string? ShadowScorerVersion = null` (append with defaults — existing call sites unaffected). `ScoringPipeline` step 8½: when a shadow scorer is registered and the session was not whitelisted, run it on the SAME vector inside a try/catch (shadow failure logs a warning and never affects the verdict), populate the two fields. API-06 writes them to the `shadow_*` columns.
   - **Rollout is pure config (D18):** listen-only pilot = `{ "Scoring": { "Scorer": "Heuristic", "Mode": "ListenOnly", "ModelPath": "artifacts/models/lgbm-..." } }`; promotion = `{ "Scorer": "MlNet", "Mode": "Enforce", "ModelPath": ... }`. Document both snippets in `ScoringOptions` XML docs.
   - T1 floors keep applying via `Math.Max` in BOTH modes — rules outrank any model.

## Files to create or modify

- `TelemetryGuard.Training/TelemetryGuard.Training.csproj` (+ `dotnet sln add`)
- `TelemetryGuard.Training/Program.cs` (command dispatch: `build-labels`, `train`)
- `TelemetryGuard.Training/LabelBuilder.cs`
- `TelemetryGuard.Training/Trainer.cs`
- `TelemetryGuard.Training/appsettings.json`
- `TelemetryGuard.RiskEngine.Contracts/MlFeatureRow.cs`
- `TelemetryGuard.RiskEngine.Contracts/MlFeatureMapper.cs`
- `TelemetryGuard.Analytics.ClickHouse/schema/00X_training_and_shadow.sql` (next free number)
- `TelemetryGuard.RiskEngine/Scoring/MlNetScorer.cs`
- `TelemetryGuard.RiskEngine/Scoring/ScoringOptions.cs`
- `TelemetryGuard.RiskEngine/Pipeline/ScoringPipeline.cs` (shadow-scoring step; `ScoringOutcome` extension)
- `TelemetryGuard.RiskEngine/Pipeline/PipelineServiceCollectionExtensions.cs` (scorer/mode switch)
- `TelemetryGuard.RiskEngine/TelemetryGuard.RiskEngine.csproj` (ML packages)
- `tests/TelemetryGuard.Tests.Unit/Training/MlFeatureMapperTests.cs`
- `tests/TelemetryGuard.Tests.Unit/Training/TrainerSmokeTests.cs`
- `tests/TelemetryGuard.Tests.Unit/Pipeline/ListenOnlyTests.cs`

## Acceptance criteria

- `dotnet build TelemetryGuard.sln` passes with the new Training project in the solution; `TelemetryGuard.RiskEngine.Contracts` still has zero PACKAGE references (POCO row + mapper only).
- `MlFeatureMapperTests`: default `FraudFeatureVector` maps to NaN for absent signals and 0/1 for known ones; `BeaconIntegrityOk=false` → `beacon_integrity_failed == 1`, `null` → NaN, `true` → 0; `AsnType.Mobile` → only `asn_mobile == 1`; `ChallengeOutcome` is absent from the row's feature fields; field count (excluding Label/Weight) == 50 asserted by reflection.
- `TrainerSmokeTests` (pure in-memory, no containers): generate 2000 synthetic `MlFeatureRow`s (separable: positives with `webdriver_flag=1`/high velocity, negatives clean, 20 % NaN sprinkle), run the exact training pipeline from step 4 → model trains, validation AUC > 0.9, `mlContext.Model.Save`/`Load` round-trips, `PredictionEngine` scores a NaN-bearing row without throwing. This proves LightGBM NaN handling end-to-end.
- AUC gate: feeding label-shuffled data (AUC ≈ 0.5) makes `Trainer` return exit code 2 and write no artifacts.
- Metadata: a passing train run writes `model.zip` + `metadata.json` with all fields of the step-4 schema; `scorer_version` matches `^lgbm-\d{8}-[0-9a-f]{8}$`.
- `ListenOnlyTests`: pipeline with heuristic enforcing + fake shadow scorer → `Result.ScorerVersion == "heuristic-1"`, band from heuristic, `ShadowScore`/`ShadowScorerVersion` populated from the fake; shadow scorer throwing → verdict unaffected, shadow fields null; `Mode=Enforce, Scorer=MlNet` with fake pool → `ScorerVersion` from metadata; whitelisted sessions get NO shadow score.
- T1 floor still binds in MlNet mode: fake model score 10 + floor 85 → final 85.
- Migration file applies cleanly against the FND-02 ClickHouse container AFTER ANA-02's `0001_events.sql` (its ALTERs require `tg_events`/`tg_labels` to exist); running it twice is idempotent (`ADD COLUMN IF NOT EXISTS`), and `SHOW CREATE TABLE tg_labels` still shows ANA-02's engine/ORDER BY/TTL — only the added `weight` column differs.
- `dotnet run --project TelemetryGuard.Training -- train --help` (or bare invocation) prints usage and exits 0; unknown command exits non-zero.

## Testing

- Unit: mapper tests, trainer smoke (in-memory LightGBM — a few seconds), listen-only pipeline tests with fakes, exit-code tests invoking `Trainer`/`LabelBuilder` classes directly (not the process).
- Integration (only where the dev stack is available; mark with the existing integration-test category): `build-labels` against a Testcontainers ClickHouse with ANA-02's `0001_events.sql` + this migration applied, seeded with a handful of `tg_events` rows and pre-seeded ingest-written `synthetic_bot` label rows → correct `tg_labels` rows for `t1_rule`/`review_screen` (ANA-01 shapes: `'fraud'`/`'legit'` strings, UUID tenant_id), `synthetic_bot` rows counted but NOT re-inserted, conversion source logged as deferred and skipped, read-time dedupe collapses a duplicated `t1_rule` row (live API-06 write + backfill), conflict-drop counted, `whitelisted` hits excluded from `t1_rule` positives.
- Do NOT commit trained models or training data; `artifacts/` goes into `.gitignore`.

## Out of scope / guardrails

- **No server-side Python/Node (D1)** — training runs in ML.NET/C# only. No notebooks, no sklearn exports, no ONNX conversion scripts (ONNX Runtime is a LATER escape hatch for sequence models, not this task).
- **Rules only raise, always (§6.3):** the T1 `Math.Max` floor applies identically under heuristic, listen-only, and model enforcement. The model can never lower a rule floor.
- **NaN is signal (spec §7):** `HandleMissingValue = true` is mandatory; never impute NaN to 0/means in the mapper, the trainer, or the scorer. `has_js_beacon` remains an input feature.
- **Listen-only means listen-only (D18):** in `ListenOnly` the model's output may influence NOTHING — not the band, not challenges, not exclusion sync; it is logged to shadow columns only. Promotion to `Enforce` is a config change by a human, never automatic.
- **Version stamping (D18):** `scorer_version` comes from metadata; heuristic-era (`heuristic-1`) and model-era rows must remain distinguishable forever. Never reuse a `scorer_version` for a retrained model.
- Exclude whitelisted verdicts from label building (their "allow" is a human override artifact, and their `whitelisted` pseudo-hit is not a T1 rule).
- Tenancy: `tenant_id` leads every ClickHouse `ORDER BY` and every query filter (D11); the label builder iterates tenants explicitly — never an unscoped cross-tenant query surface, and SQL access goes through tenant-bound Dapper connections (RLS, D9/D11). No EF.
- No retraining loop/scheduling (Phase 2, P2-02), no publisher-aggregate features (P2-01), no Kusto reads (D6 later), no generic cross-engine query layer (D7) — the training queries are hand-written ClickHouse SQL, and that is correct.
- Serving stays in-process and inside the < 50 ms budget: `PredictionEnginePool` only; no model-server sidecar, no HTTP inference hop (D3).
