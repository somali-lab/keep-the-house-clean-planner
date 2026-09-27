# ADR-0005 — One application container, separate backup container

Status: Accepted

## Context

The target is a self-hosted machine maintained by its own users. Every extra moving part is maintenance they did not ask for. Two candidates compete for "extra moving part": a separate web server for the client assets, and database tooling for backups.

Serving the built client from the application removes a whole component. Backup tooling does not fit the same argument: it needs the database vendor's dump utilities, which would enlarge the application image for a job that never handles an HTTP request.

## Decision

One application container serves both the API and the built client assets, with a single-page fallback for client-side routes. A deployment is that container plus the database.

Backups run in a separate container built from the database image, which already contains the required tooling. It writes archives to a mounted host directory, removes expired archives only after a dump has succeeded, and can be run on demand.

The application runs as a non-root user. All configuration comes from environment variables; no configuration file is baked into the image. A health endpoint reports database reachability.

## Consequences

- A deployment is one compose file, and the client cannot end up on a different version from the API it talks to.
- The application image contains no database credentials tooling and no backup logic; a compromised application cannot read or delete the archives.
- Restoring a backup is a manual, deliberate operation outside the application. That is intentional: there is no interface path that can overwrite the database from an archive.
- A failed dump leaves previous archives untouched, so a broken backup run never destroys the last good one.
