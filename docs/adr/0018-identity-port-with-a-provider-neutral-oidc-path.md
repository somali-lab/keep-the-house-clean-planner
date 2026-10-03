# ADR-0018 — Identity port with a provider-neutral OIDC path

Status: Accepted

## Context

ADR-0003 keeps profile selection as attribution instead of authentication, and that stays in force. A later move to real sign-in is plausible. If endpoints read the profile header themselves, that move rewrites every endpoint.

## Decision

Identity is a driven port, `ForResolvingActors`, that turns a request into an actor with an id, a role and a source. The first adapter reads the profile header exactly as the Node server does. Authorisation is expressed as three ASP.NET Core policies, `RequireActor`, `RequirePlanner` and `RequireAdmin`, evaluated against the resolved actor, so endpoint code never reads a header.

The next adapter is designed here and not built. It is provider-neutral: the web application signs in with Authorization Code and PKCE; the server validates the JWT bearer token against the issuer's discovery document; the token's `sub` maps to a user through a new `users.externalId`. A request carrying a valid token uses the mapped user and ignores the profile header; a request without a token keeps working through the header adapter until the household turns that off.

## Alternatives considered

- Read the header in each endpoint: no new concept, but it spreads the identity decision over every route.
- Build OIDC now: it solves a problem the household does not have and adds an external dependency to the one container.
- Bind to one identity provider's SDK: less code, but it ties a household application to a vendor.

## Consequences

- Endpoints and policies do not change when OIDC is added; only a second adapter and the `externalId` field do.
- The port is shaped for a design that does not run yet, so its fit is only confirmed when that adapter is written.
- Until then the security properties of ADR-0003 are unchanged: attribution, not authentication.
