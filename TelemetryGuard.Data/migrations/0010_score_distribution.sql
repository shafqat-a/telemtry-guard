------------------------------------------------------------------------------
-- 0010_score_distribution.sql (REQ-01)
-- Additive, mergeable score evidence for consumer-defined bands and variance.
------------------------------------------------------------------------------

ALTER TABLE dbo.VerdictDailySummaries ADD
    ScoreBucket00 int NOT NULL CONSTRAINT DF_VDS_SB00 DEFAULT (0),
    ScoreBucket10 int NOT NULL CONSTRAINT DF_VDS_SB10 DEFAULT (0),
    ScoreBucket20 int NOT NULL CONSTRAINT DF_VDS_SB20 DEFAULT (0),
    ScoreBucket30 int NOT NULL CONSTRAINT DF_VDS_SB30 DEFAULT (0),
    ScoreBucket40 int NOT NULL CONSTRAINT DF_VDS_SB40 DEFAULT (0),
    ScoreBucket50 int NOT NULL CONSTRAINT DF_VDS_SB50 DEFAULT (0),
    ScoreBucket60 int NOT NULL CONSTRAINT DF_VDS_SB60 DEFAULT (0),
    ScoreBucket70 int NOT NULL CONSTRAINT DF_VDS_SB70 DEFAULT (0),
    ScoreBucket80 int NOT NULL CONSTRAINT DF_VDS_SB80 DEFAULT (0),
    ScoreBucket90 int NOT NULL CONSTRAINT DF_VDS_SB90 DEFAULT (0),
    ScoreBucket100 int NOT NULL CONSTRAINT DF_VDS_SB100 DEFAULT (0),
    ScoreSumSq bigint NOT NULL CONSTRAINT DF_VDS_SSQ DEFAULT (0);
GO
ALTER TABLE dbo.VerdictDailySummaries ADD CONSTRAINT CK_VDS_ScoreDistribution_NonNegative CHECK
    (ScoreBucket00 >= 0 AND ScoreBucket10 >= 0 AND ScoreBucket20 >= 0 AND ScoreBucket30 >= 0
     AND ScoreBucket40 >= 0 AND ScoreBucket50 >= 0 AND ScoreBucket60 >= 0 AND ScoreBucket70 >= 0
     AND ScoreBucket80 >= 0 AND ScoreBucket90 >= 0 AND ScoreBucket100 >= 0 AND ScoreSumSq >= 0);
GO

ALTER TABLE dbo.FlaggedSourcesDaily ADD
    ScoreBucket00 int NOT NULL CONSTRAINT DF_FSD_SB00 DEFAULT (0),
    ScoreBucket10 int NOT NULL CONSTRAINT DF_FSD_SB10 DEFAULT (0),
    ScoreBucket20 int NOT NULL CONSTRAINT DF_FSD_SB20 DEFAULT (0),
    ScoreBucket30 int NOT NULL CONSTRAINT DF_FSD_SB30 DEFAULT (0),
    ScoreBucket40 int NOT NULL CONSTRAINT DF_FSD_SB40 DEFAULT (0),
    ScoreBucket50 int NOT NULL CONSTRAINT DF_FSD_SB50 DEFAULT (0),
    ScoreBucket60 int NOT NULL CONSTRAINT DF_FSD_SB60 DEFAULT (0),
    ScoreBucket70 int NOT NULL CONSTRAINT DF_FSD_SB70 DEFAULT (0),
    ScoreBucket80 int NOT NULL CONSTRAINT DF_FSD_SB80 DEFAULT (0),
    ScoreBucket90 int NOT NULL CONSTRAINT DF_FSD_SB90 DEFAULT (0),
    ScoreBucket100 int NOT NULL CONSTRAINT DF_FSD_SB100 DEFAULT (0),
    ScoreSumSq bigint NOT NULL CONSTRAINT DF_FSD_SSQ DEFAULT (0);
GO
ALTER TABLE dbo.FlaggedSourcesDaily ADD CONSTRAINT CK_FSD_ScoreDistribution_NonNegative CHECK
    (ScoreBucket00 >= 0 AND ScoreBucket10 >= 0 AND ScoreBucket20 >= 0 AND ScoreBucket30 >= 0
     AND ScoreBucket40 >= 0 AND ScoreBucket50 >= 0 AND ScoreBucket60 >= 0 AND ScoreBucket70 >= 0
     AND ScoreBucket80 >= 0 AND ScoreBucket90 >= 0 AND ScoreBucket100 >= 0 AND ScoreSumSq >= 0);
GO

ALTER TABLE dbo.PublisherDailySummaries ADD
    ScoreBucket00 int NOT NULL CONSTRAINT DF_PDS_SB00 DEFAULT (0),
    ScoreBucket10 int NOT NULL CONSTRAINT DF_PDS_SB10 DEFAULT (0),
    ScoreBucket20 int NOT NULL CONSTRAINT DF_PDS_SB20 DEFAULT (0),
    ScoreBucket30 int NOT NULL CONSTRAINT DF_PDS_SB30 DEFAULT (0),
    ScoreBucket40 int NOT NULL CONSTRAINT DF_PDS_SB40 DEFAULT (0),
    ScoreBucket50 int NOT NULL CONSTRAINT DF_PDS_SB50 DEFAULT (0),
    ScoreBucket60 int NOT NULL CONSTRAINT DF_PDS_SB60 DEFAULT (0),
    ScoreBucket70 int NOT NULL CONSTRAINT DF_PDS_SB70 DEFAULT (0),
    ScoreBucket80 int NOT NULL CONSTRAINT DF_PDS_SB80 DEFAULT (0),
    ScoreBucket90 int NOT NULL CONSTRAINT DF_PDS_SB90 DEFAULT (0),
    ScoreBucket100 int NOT NULL CONSTRAINT DF_PDS_SB100 DEFAULT (0),
    ScoreSumSq bigint NOT NULL CONSTRAINT DF_PDS_SSQ DEFAULT (0);
GO
ALTER TABLE dbo.PublisherDailySummaries ADD CONSTRAINT CK_PDS_ScoreDistribution_NonNegative CHECK
    (ScoreBucket00 >= 0 AND ScoreBucket10 >= 0 AND ScoreBucket20 >= 0 AND ScoreBucket30 >= 0
     AND ScoreBucket40 >= 0 AND ScoreBucket50 >= 0 AND ScoreBucket60 >= 0 AND ScoreBucket70 >= 0
     AND ScoreBucket80 >= 0 AND ScoreBucket90 >= 0 AND ScoreBucket100 >= 0 AND ScoreSumSq >= 0);
GO

ALTER TABLE dbo.SiteDailySummaries ADD
    ScoreBucket00 int NOT NULL CONSTRAINT DF_SDS_SB00 DEFAULT (0),
    ScoreBucket10 int NOT NULL CONSTRAINT DF_SDS_SB10 DEFAULT (0),
    ScoreBucket20 int NOT NULL CONSTRAINT DF_SDS_SB20 DEFAULT (0),
    ScoreBucket30 int NOT NULL CONSTRAINT DF_SDS_SB30 DEFAULT (0),
    ScoreBucket40 int NOT NULL CONSTRAINT DF_SDS_SB40 DEFAULT (0),
    ScoreBucket50 int NOT NULL CONSTRAINT DF_SDS_SB50 DEFAULT (0),
    ScoreBucket60 int NOT NULL CONSTRAINT DF_SDS_SB60 DEFAULT (0),
    ScoreBucket70 int NOT NULL CONSTRAINT DF_SDS_SB70 DEFAULT (0),
    ScoreBucket80 int NOT NULL CONSTRAINT DF_SDS_SB80 DEFAULT (0),
    ScoreBucket90 int NOT NULL CONSTRAINT DF_SDS_SB90 DEFAULT (0),
    ScoreBucket100 int NOT NULL CONSTRAINT DF_SDS_SB100 DEFAULT (0),
    ScoreSumSq bigint NOT NULL CONSTRAINT DF_SDS_SSQ DEFAULT (0);
GO
ALTER TABLE dbo.SiteDailySummaries ADD CONSTRAINT CK_SDS_ScoreDistribution_NonNegative CHECK
    (ScoreBucket00 >= 0 AND ScoreBucket10 >= 0 AND ScoreBucket20 >= 0 AND ScoreBucket30 >= 0
     AND ScoreBucket40 >= 0 AND ScoreBucket50 >= 0 AND ScoreBucket60 >= 0 AND ScoreBucket70 >= 0
     AND ScoreBucket80 >= 0 AND ScoreBucket90 >= 0 AND ScoreBucket100 >= 0 AND ScoreSumSq >= 0);
GO
