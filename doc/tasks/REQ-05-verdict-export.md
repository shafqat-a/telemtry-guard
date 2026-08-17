---
id: REQ-05
title: Add cursor-paginated projected verdict export
phase: 3
workstream: api
depends_on: [REQ-04]
size: M
priority: P1
---

# REQ-05: Bulk verdict export

## Objective

Add `GET /admin/verdicts?from=&to=&cursor=&limit=` using the exact evidence projection from REQ-04.

## Contract

- Keyset order is `(timestamp, session_id)`; the opaque, versioned cursor encodes both.
- Clamp `limit` to 1–1000 and bound the date range.
- Invalid cursors return `400`; they never restart enumeration.
- `nextCursor` is null only when the window is exhausted.
- Apply endpoint rate limiting independently of ingestion endpoints.

## Acceptance criteria

- Paging under concurrent ingest has no gaps or duplicates inside the fixed window.
- Cross-tenant rows cannot appear.
- The same strict PII-exclusion test as REQ-04 applies.
- Both analytics providers pass shared cursor-contract tests.

