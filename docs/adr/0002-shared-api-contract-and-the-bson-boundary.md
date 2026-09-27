# ADR-0002 — Shared API contract and the BSON boundary

Status: Accepted

## Context

The client and the server must agree on the shape of every request and response. The obvious way to guarantee that is to share the schema. The obvious way to share the schema is to describe the stored documents — but stored documents contain database types. Shipping those types to the browser would pull a database driver into the web bundle and would make the wire format an accident of the storage engine.

A second problem sits next to the first. Almost every value this system reasons about is a calendar day, not a moment: which week of the cycle a task falls in, how many days ago something was done, when a vacation starts. Subtracting instants answers those questions incorrectly twice a year, because two adjacent calendar days can be 23 or 25 hours apart under daylight saving.

## Decision

The shared package defines the **API shape**, not the storage shape. In that shape:

- identifiers are 24-character hexadecimal strings,
- calendar dates are day keys in `YYYY-MM-DD` form,
- points in time are ISO instant strings.

Database-native identifier and date types exist only on the server. The server translates in both directions at its boundary; nothing outside the server ever sees them.

The same schemas validate incoming requests on the server and describe responses to the client, so a contract change that is not made in both directions fails to type-check.

All calendar reasoning — cycle index, week index, weekday, ranges, day counts — operates on those day keys as strings, in the household's configured timezone, through helpers shared by both sides. Conversion to an instant happens only where a timestamp is genuinely required, such as the moment a task was completed.

## Consequences

- The web bundle has no database dependency and cannot accidentally depend on storage details.
- Adding a field means changing one schema, and both sides are forced to acknowledge it.
- Two representations of the same value exist inside the server, and every route must convert. Conversion lives in one helper per resource so it cannot drift per route.
- Fields that look identical in the API shape may differ in storage type. Data arriving from outside — an import file, for example — must therefore be validated against both shapes before it is written.
- Cycle and week calculations are independent of daylight saving and of the process timezone, and the server and the browser always agree on which day it is.
- Tests must compare day keys, not instants. An expected value written as a fixed UTC timestamp is wrong for half the year. Dates stored as instants at local midnight must be read back through the day-key helpers rather than compared directly.
