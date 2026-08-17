---
id: REQ-02
title: Expose score histograms on admin reports
phase: 3
workstream: api
depends_on: [REQ-01]
size: XS
priority: P0
---

# REQ-02: Report score histograms

## Objective

Add `scoreHistogram` to every row returned by `/admin/reports/summary`, `/admin/reports/flagged-sources`, `/admin/reports/publishers`, and `/admin/reports/sites` without changing existing fields.

## Contract

```json
{"bucketWidth":10,"edges":[0,10,20,30,40,50,60,70,80,90,100],"counts":[0,0,0,0,0,0,0,0,0,0,0],"sumSq":0}
```

Range queries must add bucket counts and `sumSq` in SQL. Edges are lower bounds and ship in every histogram so consumers never depend on an undocumented constant.

## Acceptance criteria

- `counts.length == edges.length == 11`.
- Counts reconcile with `events`, or `flaggedCount` for flagged sources.
- Existing response fields and validation remain compatible.
- API tests assert exact seeded histogram payloads.

