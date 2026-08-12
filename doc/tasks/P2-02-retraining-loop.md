---
id: P2-02
title: Retraining loop (outline)
phase: 2
workstream: risk
depends_on: [RSK-08]
size: M
spec_refs: [D4, D18, D19, "§10 Phase 2"]
detail_level: outline
---

# P2-02: Retraining loop (outline)

> **OUTLINE ONLY — expand into a full task file before implementation.** Scope, dependencies, and open decisions below.

## Objective

Turn RSK-08's one-shot "first LightGBM model" pipeline into a recurring loop: scheduled retraining from continuously accumulating labels (D19's override loop doubles as the labeling loop), an evaluation gate comparing each candidate against the incumbent before it may serve, a model registry with rollback, and airtight `scorer_version` lineage from training data to every stamped verdict.

## Spec context (self-contained)

- D4: ML.NET LightGBM in-process; training and serving share the .NET codebase (no Python — D1).
- D18: every verdict is stamped `scorer_version` + `feature_set_version`; heuristic-era, listen-only-era, and each model generation must never be confused in training or reporting. Scorer swap is config-only via `IScorer`.
- D19: review-screen "real customer" marks write negative labels (`LabelSources.ReviewScreen`); T1 rule hits = weak positives; Playwright synthetic bots = guaranteed positives; confirmed conversions = negatives (ANA-01 `ILabelSink`/`LabelEvent` contract).
- Rules only raise scores — retraining never changes rule semantics; the model fills the space under the rule floor.
- D3: whatever is trained must still score in-process within <50 ms.

## Prerequisites

RSK-08 exists: label pipeline (labels + `ClickEvent` history join), ML.NET training code producing a LightGBM model artifact + metrics, listen-only rollout mechanics, and the `IScorer` LightGBM implementation. Read RSK-08's file for artifact format, training-set query, and eval metrics.

## Implementation steps (outline)

1. `dbo.ModelRegistry` migration (TenantId-less platform table or tenant-scoped — open decision): ModelId, ScorerVersion, FeatureSetVersion, TrainedUtc, TrainWindow, LabelCounts, MetricsJson, ArtifactPath/hash, Status (`candidate|listen_only|active|retired|rolled_back`).
2. Scheduled retrain (BackgroundService cadence or CI-triggered `dotnet run` job — open decision) re-running RSK-08's pipeline over the latest label window; excludes verdicts produced by the candidate's own ancestors from label leakage where applicable.
3. Eval gate: holdout metrics (AUC-PR, precision at block band, FP rate on labeled-legit) must beat/equal incumbent by configured margins; failing candidates are recorded and never served.
4. Promotion: candidate → listen-only (scored + logged, not enforcing, reusing RSK-08's mechanism) → active via config/registry flip; `IScorer` resolves the active registry row at startup/refresh.
5. Rollback: single operation flipping `active` back to the previous row; verdicts stamped with whichever version actually scored them (lineage preserved automatically per D18).

## Files to create or modify (outline)

- `TelemetryGuard.Data/migrations/NNNN_model_registry.sql`, registry repository, `TelemetryGuard.RiskEngine/Training/` retrain job + eval gate, scorer resolution wiring, tests.

## Acceptance criteria (outline)

- A candidate failing the gate never serves; promotion and rollback are observable in the registry and in verdict `scorer_version` stamps; retrain is reproducible from registry metadata; listen-only period enforced before any auto-promotion.

## Testing (outline)

Deterministic synthetic label sets where the gate must pass/fail predictably; registry repository integration tests (DAT-08 harness); a full mini train-promote-rollback cycle in integration.

## Out of scope / guardrails

- No server-side Python/Node (D1) — training stays ML.NET; no notebook/sklearn sidecars.
- Rules never lowered/retrained; NaN semantics preserved end-to-end (missing ≠ zero — LightGBM branches on NaN natively); scoring stays in-process <50 ms (model size/latency is a gate criterion).
- Never train across `feature_set_version` boundaries without explicit handling; never mix heuristic-era verdict scores into labels as if they were ground truth.
- Tenant isolation: training reads via the SYSTEM-sentinel pattern (DAT-03); any per-tenant model split is an open decision, not an accident.

## Open decisions (resolve when expanding)

1. Cadence (weekly? volume-triggered?) and minimum label counts per class before retraining.
2. Global model vs per-tenant models (data volume argues global first).
3. Artifact storage (repo-adjacent blob dir vs SQL varbinary vs object storage) and integrity hashing.
4. Where training compute runs: in the Api host (D3 single-service) vs a separate `dotnet` job container — memory pressure argues the latter; both are .NET.
5. Exact gate metrics/margins and the listen-only duration for candidates (spec §11.4 proposed 2–4 weeks for the pilot).
