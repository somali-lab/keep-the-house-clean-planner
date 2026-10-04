# Blockers

Open questions for the maintainer, and anything else that stops the work from continuing: a missing permission, an unavailable dependency, a choice only the maintainer can make.

This file is for work that cannot ask. An agent in a live conversation asks the question directly; an agent running unattended writes it here and stops at that point instead of guessing.


## The .NET host seeds no rooms (found by slice 7.6)

Requirements §3 ("rooms") say a fresh installation includes a `virtual` room, and the Node server seeded the seven rooms Keuken, Badkamer, Toilet, Woonkamer, Slaapkamer, Hal and Hele huis (the last one `virtual`) together with the users, the settings and the default plan. The .NET host seeds users, settings and the default plan but no rooms (`apps/api/src/Huishoudplanner.Host/Rooms/RoomsComposition.cs` has no `ISeedStep`; `ForStoringRooms` has no count), so a fresh .NET installation has no room at all and no virtual room, and no task can be created until an administrator adds rooms.

Slice 7.6 works around it only in the e2e harness (`apps/web/e2e/server.ts` creates the seven rooms through `POST /api/v2/rooms`). The fix is a use case `IRoomSeedService` (empty rooms collection gets the seven rooms with sort order 10, 20, ..., audited as the system actor, one transaction), a `CountAsync` on `ForStoringRooms` and the Mongo adapter, a `RoomSeedStep` in the Host and tests like `UserSeedServiceTests`. It touches the port, the adapter and every fake room store, so it was not squeezed into the e2e slice. Question: should the default rooms stay Dutch names as in the Node seed, and should the harness workaround be removed once the seed exists? (Nothing else is needed from the maintainer.)
