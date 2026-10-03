# ADR-0021 — MongoDB replica set and transactional writes

Status: Accepted

## Context

ADR-0004 requires an audit entry for every state change, and ADR-0008 accepts that activation is not serialisable because the supported deployment is a standalone MongoDB. A crash between an entity write and its audit write leaves a change without history, and the nightly reconciliation only repairs the points ledger.

## Decision

MongoDB runs as a single-node replica set, which is enough for multi-document transactions. Every write that changes state runs in one transaction together with its audit entry, through a driven port that wraps the client session. Plan activation runs in one transaction after the preview token is rechecked inside it. Transactions use snapshot isolation, which does not by itself prevent write skew (two transactions that read the same state and write different documents both commit). Activation therefore also writes one shared guard document, for example a version field on the plan or settings document, so two concurrent activations write the same document, one hits a write conflict and is retried or reported as a conflict; with that guard activation is serialisable. A write that changes nothing still writes nothing.

Existing installations convert the standalone instance with a documented, backed-up `rs.initiate()` step. The Node application is verified on the replica set before the .NET application touches production data.

## Alternatives considered

- Stay standalone and keep reconciliation as the only safety net: no operational change, but the audit guarantee remains best effort.
- Compensating writes without transactions: needs recovery code in every use case.
- A multi-node replica set: more availability than a household needs.

## Consequences

- The audit guarantee of ADR-0004 becomes atomic instead of conventional.
- Integration tests run against a replica set so they behave like production.
- The compose file and the deployment guide gain a one-time conversion step.
- A transaction can abort under write conflict; the port reports that as a conflict rather than an exception.
