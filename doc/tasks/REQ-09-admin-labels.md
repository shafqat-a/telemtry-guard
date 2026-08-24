---
id: REQ-09
title: Accept idempotent reviewer labels
phase: 3
workstream: training
depends_on: [REQ-04, RSK-08]
size: S
priority: P2
---

# REQ-09: Admin labels

## Objective

Add `POST /admin/labels` accepting `fraud` or `legit`, `source`, and `weight` for an existing tenant-owned session, using the existing analytics label pipeline without coupling the operation to the whitelist.

## Contract

```json
{"sessionId":"...","label":"fraud","source":"marketiq_review","weight":1.0}
```

- Unknown and cross-tenant sessions return `404`.
- Idempotency key is `(tenant, session, source)`; resubmission updates the logical label rather than appending a duplicate.
- Weight must be finite, positive, and within a documented maximum.
- Existing review-screen whitelist behavior remains unchanged.
- Durable submission state bridges analytics-provider outages; delivery uses `ILabelSink`.

## Acceptance criteria

- Fraud and legit labels are visible to training within one delivery cycle.
- Repeated submissions yield one effective label with the latest value and weight.
- Posting a label never creates or removes a whitelist entry.
- Tenant isolation and label validation have endpoint and integration tests.

