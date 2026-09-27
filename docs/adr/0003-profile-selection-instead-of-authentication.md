# ADR-0003 — Profile selection instead of authentication

Status: Accepted

## Context

The application runs on a private network behind a reverse proxy and is not publicly reachable. Its users are the members of one household. Passwords would add friction to the one interaction that has to be effortless — ticking something off while holding a phone in one hand — without protecting against any threat that is actually present.

At the same time, every change has to be attributable, or the audit log is worthless.

## Decision

There is no authentication. On first use the user picks a profile; that choice is stored client-side and persists across sessions. Every request carries the selected profile, and the server records it as the actor.

Profile selection is **attribution, not authorisation**. It answers "who did this", never "may this happen".

Authorisation is separate and server-side: roles decide which operations are permitted, and the server enforces them regardless of what the client sends. Read operations do not require a profile; anything that writes does.

The identity layer is a single replaceable module. Routes ask for an actor and a role; they do not know how either was established.

## Consequences

- The deployment requirement "trusted network only" is part of the security model and must stay documented wherever deployment is described. Exposing the application publicly breaks it.
- Anyone on that network can act as anyone. This is accepted for a household and is why the audit log matters more here than authorisation does.
- Adding real authentication later means replacing one module, not touching every route, because routes already consume an actor and a role rather than a header.
- A stored profile that no longer matches an active user is discarded and the user is asked to choose again, so a restored backup or an import cannot leave the client acting as a person who no longer exists.
