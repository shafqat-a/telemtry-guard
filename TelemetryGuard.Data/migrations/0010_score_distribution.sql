------------------------------------------------------------------------------
-- 0010_score_distribution.sql
-- REQ-01: score histogram + sum-of-squares on the four D23/P2-01 daily rollup
-- tables. TG bands verdicts at 30/70 (allow/challenge/block —
-- TelemetryGuard.Api/Options/ScoringBandOptions.cs); a plain ScoreSum cannot be
-- re-banded by a downstream consumer with different thresholds (e.g. MarketIQ's
-- 30/60 Low/Medium/High). Eleven decile buckets (0-9 .. 90-99, plus an exact
-- bucket for score==100) let any consumer recompute exact tier counts under its
-- OWN thresholds from these small aggregate tables, without touching ClickHouse.
--
-- MERGEABILITY: every new column is an absolute, summable count (never an
-- average) — same contract as the existing ScoreSum column on each table
-- (0003/0008's MERGEABILITY CONTRACT). All four tables are already RLS-scoped
-- from 0003/0008; adding columns to an existing table needs no RLS changes.
------------------------------------------------------------------------------

ALTER TABLE dbo.VerdictDailySummaries ADD
    ScoreBucket00  int    NOT NULL CONSTRAINT DF_VDS_SB00  DEFAULT (0),   -- scores 0-9
    ScoreBucket10  int    NOT NULL CONSTRAINT DF_VDS_SB10  DEFAULT (0),   -- scores 10-19
    ScoreBucket20  int    NOT NULL CONSTRAINT DF_VDS_SB20  DEFAULT (0),   -- scores 20-29
    ScoreBucket30  int    NOT NULL CONSTRAINT DF_VDS_SB30  DEFAULT (0),   -- scores 30-39
    ScoreBucket40  int    NOT NULL CONSTRAINT DF_VDS_SB40  DEFAULT (0),   -- scores 40-49
    ScoreBucket50  int    NOT NULL CONSTRAINT DF_VDS_SB50  DEFAULT (0),   -- scores 50-59
    ScoreBucket60  int    NOT NULL CONSTRAINT DF_VDS_SB60  DEFAULT (0),   -- scores 60-69
    ScoreBucket70  int    NOT NULL CONSTRAINT DF_VDS_SB70  DEFAULT (0),   -- scores 70-79
    ScoreBucket80  int    NOT NULL CONSTRAINT DF_VDS_SB80  DEFAULT (0),   -- scores 80-89
    ScoreBucket90  int    NOT NULL CONSTRAINT DF_VDS_SB90  DEFAULT (0),   -- scores 90-99
    ScoreBucket100 int    NOT NULL CONSTRAINT DF_VDS_SB100 DEFAULT (0),   -- exactly 100
    ScoreSumSq     bigint NOT NULL CONSTRAINT DF_VDS_SSQ   DEFAULT (0);   -- sum of score^2, for variance
GO

ALTER TABLE dbo.FlaggedSourcesDaily ADD
    ScoreBucket00  int    NOT NULL CONSTRAINT DF_FSD_SB00  DEFAULT (0),
    ScoreBucket10  int    NOT NULL CONSTRAINT DF_FSD_SB10  DEFAULT (0),
    ScoreBucket20  int    NOT NULL CONSTRAINT DF_FSD_SB20  DEFAULT (0),
    ScoreBucket30  int    NOT NULL CONSTRAINT DF_FSD_SB30  DEFAULT (0),
    ScoreBucket40  int    NOT NULL CONSTRAINT DF_FSD_SB40  DEFAULT (0),
    ScoreBucket50  int    NOT NULL CONSTRAINT DF_FSD_SB50  DEFAULT (0),
    ScoreBucket60  int    NOT NULL CONSTRAINT DF_FSD_SB60  DEFAULT (0),
    ScoreBucket70  int    NOT NULL CONSTRAINT DF_FSD_SB70  DEFAULT (0),
    ScoreBucket80  int    NOT NULL CONSTRAINT DF_FSD_SB80  DEFAULT (0),
    ScoreBucket90  int    NOT NULL CONSTRAINT DF_FSD_SB90  DEFAULT (0),
    ScoreBucket100 int    NOT NULL CONSTRAINT DF_FSD_SB100 DEFAULT (0),
    ScoreSumSq     bigint NOT NULL CONSTRAINT DF_FSD_SSQ   DEFAULT (0);
GO

ALTER TABLE dbo.PublisherDailySummaries ADD
    ScoreBucket00  int    NOT NULL CONSTRAINT DF_PDS_SB00  DEFAULT (0),
    ScoreBucket10  int    NOT NULL CONSTRAINT DF_PDS_SB10  DEFAULT (0),
    ScoreBucket20  int    NOT NULL CONSTRAINT DF_PDS_SB20  DEFAULT (0),
    ScoreBucket30  int    NOT NULL CONSTRAINT DF_PDS_SB30  DEFAULT (0),
    ScoreBucket40  int    NOT NULL CONSTRAINT DF_PDS_SB40  DEFAULT (0),
    ScoreBucket50  int    NOT NULL CONSTRAINT DF_PDS_SB50  DEFAULT (0),
    ScoreBucket60  int    NOT NULL CONSTRAINT DF_PDS_SB60  DEFAULT (0),
    ScoreBucket70  int    NOT NULL CONSTRAINT DF_PDS_SB70  DEFAULT (0),
    ScoreBucket80  int    NOT NULL CONSTRAINT DF_PDS_SB80  DEFAULT (0),
    ScoreBucket90  int    NOT NULL CONSTRAINT DF_PDS_SB90  DEFAULT (0),
    ScoreBucket100 int    NOT NULL CONSTRAINT DF_PDS_SB100 DEFAULT (0),
    ScoreSumSq     bigint NOT NULL CONSTRAINT DF_PDS_SSQ   DEFAULT (0);
GO

ALTER TABLE dbo.SiteDailySummaries ADD
    ScoreBucket00  int    NOT NULL CONSTRAINT DF_SDS_SB00  DEFAULT (0),
    ScoreBucket10  int    NOT NULL CONSTRAINT DF_SDS_SB10  DEFAULT (0),
    ScoreBucket20  int    NOT NULL CONSTRAINT DF_SDS_SB20  DEFAULT (0),
    ScoreBucket30  int    NOT NULL CONSTRAINT DF_SDS_SB30  DEFAULT (0),
    ScoreBucket40  int    NOT NULL CONSTRAINT DF_SDS_SB40  DEFAULT (0),
    ScoreBucket50  int    NOT NULL CONSTRAINT DF_SDS_SB50  DEFAULT (0),
    ScoreBucket60  int    NOT NULL CONSTRAINT DF_SDS_SB60  DEFAULT (0),
    ScoreBucket70  int    NOT NULL CONSTRAINT DF_SDS_SB70  DEFAULT (0),
    ScoreBucket80  int    NOT NULL CONSTRAINT DF_SDS_SB80  DEFAULT (0),
    ScoreBucket90  int    NOT NULL CONSTRAINT DF_SDS_SB90  DEFAULT (0),
    ScoreBucket100 int    NOT NULL CONSTRAINT DF_SDS_SB100 DEFAULT (0),
    ScoreSumSq     bigint NOT NULL CONSTRAINT DF_SDS_SSQ   DEFAULT (0);
GO
