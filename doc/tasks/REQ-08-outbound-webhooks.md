---
id: REQ-08
title: Add HMAC-signed at-least-once outbound webhooks
phase: 3
workstream: integrations
depends_on: [REQ-01, REQ-07]
size: M
priority: P2
---

# REQ-08: Outbound webhooks

## Objective

Deliver tenant events to configured subscribers with HMAC authentication and true at-least-once semantics. `rollup.completed` is the primary event; initial optional events are `exclusion.queued`, `verdict.blocked`, and `site.integration_changed`.

## Contract

- Persist an outbox before dispatch; an in-memory-only queue cannot satisfy at-least-once delivery.
- Delivery headers include event type, stable delivery UUID, timestamp, and `sha256` HMAC over timestamp plus exact body bytes.
- Lease rows, retry with bounded exponential backoff and jitter, and dead-letter after the configured ceiling.
- Disabled or absent subscribers create no delivery work.
- Validate destinations against SSRF, DNS-rebinding, loopback, link-local, and private-network targets.
- Emit `rollup.completed` only after all rollup families for a tenant succeed, using a deterministic logical idempotency key.

## Acceptance criteria

- A dead subscriber does not affect ingestion latency.
- Retries reuse the same delivery UUID.
- A second implementation verifies signatures.
- Worker restart resumes outstanding deliveries.
- Metrics cover queued, delivered, retried, failed, and dead-lettered deliveries.

