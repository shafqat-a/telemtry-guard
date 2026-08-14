------------------------------------------------------------------------------
-- 0009_model_registry.sql  (P2-02)
-- Model registry: ONE ROW PER TRAINING RUN of the global LightGBM model, with
-- the candidate -> shadow -> active lifecycle and rollback (D18).
--
-- PLATFORM-SCOPED, NOT TENANT-SCOPED — deliberately no TenantId column and
-- therefore deliberately NO rls.TenantIsolationPolicy predicate. The DAT-03 RLS
-- rule covers tenant-scoped tables; ONE GLOBAL MODEL is trained across all
-- tenants' labels (per-tenant models are explicitly out of scope, P2-02), and
-- this table stores model metadata only (metrics, windows, artifact paths) —
-- never tenant data. Access is background/CLI only, through
-- ISystemConnectionFactory; nothing on the request path reads it.
--
-- Status values mirror TelemetryGuard.Data.Repositories.ModelStatuses.
------------------------------------------------------------------------------
CREATE TABLE dbo.ModelRegistry
(
    ModelId           uniqueidentifier NOT NULL CONSTRAINT DF_MR_ModelId DEFAULT (NEWID()),
    ScorerVersion     varchar(64)      NULL,      -- 'lgbm-yyyyMMdd-hash8'; NULL only for runs that exported no artifact
    FeatureSetVersion int              NOT NULL,
    Status            varchar(16)      NOT NULL,  -- 'candidate'|'shadow'|'active'|'rejected'|'retired'
    TrainedUtc        datetime2(3)     NOT NULL,
    WindowFromUtc     datetime2(3)     NOT NULL,  -- training window [From, To)
    WindowToUtc       datetime2(3)     NOT NULL,
    TrainRows         int              NOT NULL,
    ValidationRows    int              NOT NULL,
    Positives         int              NOT NULL,
    Negatives         int              NOT NULL,
    DroppedConflicts  int              NOT NULL,
    Auc               float            NOT NULL,
    Auprc             float            NOT NULL,
    F1                float            NOT NULL,
    MinAucGate        float            NOT NULL,
    ScoreP99Ms        float            NULL,      -- measured single-row Predict latency (D3 gate)
    ArtifactPath      nvarchar(400)    NULL,      -- ABSOLUTE dir holding model.zip + metadata.json
    ArtifactSha256    binary(32)       NULL,      -- SHA-256 of model.zip, re-verified at load
    MetadataJson      nvarchar(max)    NULL,      -- verbatim metadata.json (reproducibility)
    GateJson          nvarchar(max)    NULL,      -- promotion-gate decision + reasons + divergence snapshot
    RejectReason      nvarchar(400)    NULL,
    Notes             nvarchar(400)    NULL,      -- who promoted / why / forced
    ShadowSinceUtc    datetime2(3)     NULL,
    ActiveSinceUtc    datetime2(3)     NULL,
    RetiredUtc        datetime2(3)     NULL,
    CreatedUtc        datetime2(3)     NOT NULL CONSTRAINT DF_MR_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    UpdatedUtc        datetime2(3)     NULL,
    CONSTRAINT PK_ModelRegistry PRIMARY KEY CLUSTERED (ModelId),
    CONSTRAINT CK_MR_Status CHECK (Status IN ('candidate', 'shadow', 'active', 'rejected', 'retired')),
    -- Anything that may ever serve MUST carry a version + verifiable artifact.
    CONSTRAINT CK_MR_Artifact CHECK
        (Status = 'rejected'
         OR (ScorerVersion IS NOT NULL AND ArtifactPath IS NOT NULL AND ArtifactSha256 IS NOT NULL))
);
GO
-- Natural key: one row per exported model version (NULL for gate-failed runs).
CREATE UNIQUE NONCLUSTERED INDEX UX_ModelRegistry_ScorerVersion
    ON dbo.ModelRegistry (ScorerVersion) WHERE ScorerVersion IS NOT NULL;
GO
-- At most ONE active and ONE shadow row — enforced by the DATABASE, not by
-- application discipline (same rationale as D11's RLS choice).
CREATE UNIQUE NONCLUSTERED INDEX UX_ModelRegistry_SingleActive
    ON dbo.ModelRegistry (Status) WHERE Status = 'active';
GO
CREATE UNIQUE NONCLUSTERED INDEX UX_ModelRegistry_SingleShadow
    ON dbo.ModelRegistry (Status) WHERE Status = 'shadow';
GO
CREATE NONCLUSTERED INDEX IX_ModelRegistry_TrainedUtc ON dbo.ModelRegistry (TrainedUtc DESC);
GO
