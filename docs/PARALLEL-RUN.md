# Parallel run of the .NET application

The checklist for slice 8.2 of [the rewrite plan](plans/dotnet-rewrite.md) (section 10): the .NET application (`app-next`) runs next to the Node application for a few days, against a copy of the production database. The household keeps using the Node application. The maintainer walks this document and gives the go for the switch by hand; no agent runs it.

Everything here runs on the Docker host of the installation, in the directory with `docker-compose.yml` and `.env`, in a checkout of the `next` branch (the .NET image is built from `docker/Dockerfile.dotnet`, which only exists there). Commands are `sh`.

**What is safe.** `app-next` has its own database (`huishoudplanner_next`) on the same MongoDB instance. The copy script only reads the production database. The parity script only sends `GET` requests. `app-next` sends no notification and makes no AI call unless you opt in below.

## 0. Settings of `app-next`

Add to `.env` only what you want to change; all of these are optional.

| Variable                                                  | Default                                                         | Meaning                                                                                                                                                                                                                                                                                           |
| --------------------------------------------------------- | --------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `APP_NEXT_PORT`                                           | `3001`                                                          | Host port of `app-next` (the Node application stays on `APP_PORT`).                                                                                                                                                                                                                               |
| `NEXT_MONGO_URL`                                          | `mongodb://mongo:27017/huishoudplanner_next?replicaSet=rs0`     | Connection string of the copy. Keep the database name `huishoudplanner_next`.                                                                                                                                                                                                                     |
| `NEXT_NOTIFY_TYPE`, `NEXT_NOTIFY_URL`, `NEXT_NOTIFY_TOKEN` | `none`, empty, empty                                            | **The opt-in for the morning message.** The `NOTIFY_*` values of `.env` never reach `app-next`. With type `none` the 07:30 job is not even scheduled. Use a separate test topic or webhook, otherwise the household gets every morning message twice (once from each application).                  |
| `NEXT_AI_API_KEY`                                         | empty                                                           | **The opt-in for AI calls.** The `AI_API_KEY` of `.env` never reaches `app-next`. The provider type, endpoint and model are in the copied settings, so with a key set the copy really calls the provider (costs). Providers that need no key (`mock`, a local `ollama`) still work without one. |
| `NEXT_DISABLE_SCHEDULER`                                  | `false`                                                         | The scheduler runs on the copy, so the nightly generation (03:00) can be verified. Set `true` to switch the jobs off.                                                                                                                                                                             |
| `NEXT_OTEL_SERVICE_NAME`, `NEXT_OTEL_RESOURCE_ATTRIBUTES` | `app-next`, `deployment.environment.name=parallel`              | Telemetry identity. The `OTEL_EXPORTER_OTLP_*` values of `.env` pass through to `app-next` unchanged; see [OBSERVABILITY.md](OBSERVABILITY.md).                                                                                                                                                   |
| `APP_NEXT_IMAGE_TAG`                                      | `latest`                                                        | Tag of the locally built image `huishoudplanner-app-next`.                                                                                                                                                                                                                                        |

`TZ_APP`, `SEED_USERS`, `AUDIT_RETENTION_DAYS` and `LOG_LEVEL` come from `.env` for both applications. Mind `AUDIT_RETENTION_DAYS`: the 03:45 retention job deletes audit entries of the **copy** too, which is harmless.

## 1. Prepare the installation

Back up first, and keep a copy of the archive outside the backup directory:

```sh
docker compose run --rm backup once
```

Convert `mongo` to a single-node replica set and verify that the Node application still works on it, as described in [README, MongoDB as a single-node replica set](../README.md#mongodb-as-a-single-node-replica-set). Skip this if `docker compose exec mongo mongosh --quiet --eval "rs.status().myState"` already prints `1`. The Node application needs no change for a replica set. Do not go on before the household's application is healthy on it.

## 2. Build, copy

```sh
docker compose --profile parallel build app-next
node scripts/parallel-copy-db.ts --dry-run          # prints what it would do, runs nothing
node scripts/parallel-copy-db.ts                    # asks for confirmation; --yes skips the question
```

The copy runs `mongodump` of `huishoudplanner` and `mongorestore` as `huishoudplanner_next` inside the `mongo` service, replacing the target completely. It refuses to run when the target is not `huishoudplanner_next` (unless `--force`), when the target is the live database or the source (never possible), and while `app-next` is running. It prints the document counts of both and ends by comparing them. A copy made while the household works shows small count differences that are not an error; run it again at a quiet moment for an exact copy.

Run the copy **before** the first start of `app-next`: on an empty database the .NET application seeds default rooms and profiles, and the copy would then replace them anyway.

## 3. Start and compare

```sh
docker compose --profile parallel up -d app-next
docker compose --profile parallel ps                # app-next healthy after about 20 seconds
curl -s http://localhost:3001/api/v2/health          # {"status":"ok","version":"...","database":"ok"}
node scripts/parallel-parity.ts --v1 http://localhost:3000 --v2 http://localhost:3001
```

Never start `app-next` with a plain `docker compose --profile parallel up -d` before the copy exists; name the service.

The parity script reads the same data through both APIs and compares: health, users, rooms, tasks, cycle plans (all members including the slots), cycles, the settings both share, the ranked due list (and its summary), every occurrence of the cycles (open, done, skipped, one-off), the points balances per person (all time and for the range), the badges and the badge awards per person. It exits `0` when everything is equal and `1` otherwise, and prints every difference as `[record] member: v1 ... v2 ...`. Options: `--from/--to` for another occurrence range, `--only users,due` for some checks, `--profile-id` to send a profile id, `--max-diffs`, `--json`; `--help` lists them.

Ignored on purpose, because they are the intended differences between the contracts: the id member name (`_id` against `id`), the instant notation (`Z` against `+00:00`), paging, `version` (ADR-0022), members only v2 has (`periodOwnerId`, `cycleIndex`, `weekIndex` on occurrences, `startsInFuture` on bonus rows), an absent member against `null` and an absent against an empty list in the settings, the order of lists whose order is not part of the contract, and the id and moment of a badge award (the question is who holds which badge).

**Read differences with the drift in mind.** The household keeps changing the live data and `app-next` sees only the copy, so every change since the copy is a real difference between the two databases, not a bug. Run the parity script right after the copy (it must be all equal) and again whenever you re-copy. To judge the nightly generation on identical data, re-copy shortly before 03:00 (see the checklist).

## 4. Verify for a few days

| Check                                       | How                                                                                                                                                                                                                                                                                                                                                                                      |
| ------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Health                                      | `curl -s http://localhost:3001/api/v2/health` answers `ok` for status and database, `docker compose --profile parallel ps` shows `healthy`, and `docker compose logs --since 1h app-next` shows no error.                                                                                                                                                                                |
| A nightly generation produced the same occurrences | Re-copy at about 02:45 (stop `app-next` first: `docker compose --profile parallel stop app-next`, then copy, then `up -d app-next`) so both applications start from the same data. After 03:05 run the parity script: the occurrence check covers what both generations made. `curl -s "http://localhost:3001/api/v2/audit?source=system&limit=20"` lists the system entries of the nightly run on the copy. When no cycle boundary falls in the window both sides legitimately generate nothing; the frozen-clock parity of plan section 7.3 is what proves the boundaries. |
| Due list equal                              | The `due` check of the parity script (ranked order and the due/overdue counts).                                                                                                                                                                                                                                                                                                          |
| Points balances equal                       | The `balances` check, all time and for the cycle range.                                                                                                                                                                                                                                                                                                                                  |
| Badges equal                                | The `badges` and `awards` checks.                                                                                                                                                                                                                                                                                                                                                        |
| The four PDF sheets, reviewed by you        | Download each sheet from both applications and put them side by side (the content rules of requirements section 6, the layout may differ): see below.                                                                                                                                                                                                                                   |
| Morning message received once               | Only with the opt-in of section 0, preferably on a separate test topic. At 07:30 exactly one message per person with something to do arrives from `app-next`; or send it now as a planner: `curl -s -X POST -H "X-Profile-Id: <planner id>" http://localhost:3001/api/v2/jobs/morning-notify` answers `{status, date, sent, failed, quiet}`. Remove the `NEXT_NOTIFY_*` values afterwards. |
| Kibana shows traces, metrics and logs       | In Kibana (Observability, Services) open the service `app-next` (resource `deployment.environment.name=parallel`): traces for the requests you made, runtime and `Huishoudplanner` metrics, and the JSON logs. Needs `OTEL_EXPORTER_OTLP_ENDPOINT` (and headers) in `.env`; this is the step plan slice 0.5 still lacks.                                                                |

The four sheets, with `$P` the profile id of any person and the current ISO week in `fromWeek`:

```sh
W=$(date +%G-W%V); D=$(date +%F); P=<profile id>
mkdir -p /tmp/sheets && cd /tmp/sheets
for host in 3000:node:/api/export/pdf 3001:next:/api/v2/export/pdf/schedule; do
  port=${host%%:*}; rest=${host#*:}; name=${rest%%:*}; path=${rest#*:}
  curl -fsS -H "X-Profile-Id: $P" -o "$name-schedule.pdf" "http://localhost:$port$path?fromWeek=$W&weeks=2&orientation=landscape&totals=true"
done
for sheet in day due tasks; do
  q=""; [ "$sheet" = day ] && q="?date=$D"
  curl -fsS -H "X-Profile-Id: $P" -o "node-$sheet.pdf" "http://localhost:3000/api/export/pdf/$sheet$q"
  curl -fsS -H "X-Profile-Id: $P" -o "next-$sheet.pdf" "http://localhost:3001/api/v2/export/pdf/$sheet$q"
done
ls -l
```

Findings go to the maintainer's issue list; fix them on `next`, rebuild (`docker compose --profile parallel build app-next`), and repeat the copy.

## 5. Switch (by the maintainer)

The outline from plan section 10; slice 8.3 replaces `docker/Dockerfile` with the .NET image and turns it into exact commands, so the commands below are completed there.

1. Back up: `docker compose run --rm backup once`, and keep a copy outside the host directory.
2. Stop both applications: `docker compose --profile parallel stop app app-next`.
3. Run the .NET image against the production database `huishoudplanner` (its startup migrations are idempotent) and start it.
4. Walk the checklist of section 4 again on the live data (health, due list, balances, a PDF sheet, the morning message).
5. Remove the copy: `docker compose exec mongo mongosh --quiet --eval 'db.getSiblingDB("huishoudplanner_next").dropDatabase()'`, and remove the `parallel` containers: `docker compose --profile parallel rm -sf app-next`.

## 6. Roll back

Within the first days after the switch: stop the .NET application and start the last Node image against the same database.

```sh
docker compose stop app                        # the .NET application
# set APP_IMAGE_TAG in .env to the last Node release (for example 1.7.0) and start it:
docker compose up -d app
```

The rollback is safe because the Node application reads and edits everything the .NET application writes; the exact finding is in the next section. Two things to know:

- **Version numbers stand still.** The Node application does not raise `version` when it edits a document. After a rollback and a later return to .NET, an ETag that a browser tab or script still holds can match a document that Node changed in between (ADR-0022). Reload the web app after any switch.
- **Restoring an older image is a different matter.** A release before ADR-0009 (extra executions and one-off tasks) cannot read today's data at all; that case needs a database backup first, see [RELEASING.md](RELEASING.md#rolling-back-across-the-occurrence-index-change).

## Compatibility of the Node app with documents written by the .NET app

Question: is a rollback to Node safe against a database that the .NET application has written to? **Yes.** Read-only analysis of `apps/server` and `packages/shared`, then confirmed with a running Node server on a database written by the .NET host.

What the .NET application writes that the Node application does not:

| What                                                                           | Where                                                                                                 | How Node treats it                                                                                                                                                                                                                                                                                                  |
| ------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| An integer `version` (ADR-0022)                                                | users, rooms, tasks, cyclePlans, badges and the settings document                                    | Documents are read as plain Mongo documents (`findOne`/`find`, no schema parse on read) and edited with `$set` of the changed members only, so the member is carried along untouched. No Zod schema in `apps/server` or `packages/shared` is strict (`strict()`, `strictObject` and `passthrough` do not occur), and Zod strips unknown members anyway. |
| An integer `activationVersion`                                                 | the settings document (guard of the plan activation)                                                  | Same as above. It leaks into the JSON of `GET /api/settings` next to `version`, which no client reads.                                                                                                                                                                                                              |
| The collections `pointGuards` and `migrations`                                 | next to the eleven collections of `apps/server/src/data/db.ts`                                        | Never read, written, indexed or exported: the data layer works from the fixed list `COLLECTIONS`. `ensureIndexes` at startup only touches that list.                                                                                                                                                                  |
| Indexes                                                                        | all collections                                                                                       | The .NET index catalogue is the Node list, with the same keys, names, uniqueness and partial filters, so `createIndexes` at Node startup finds identical indexes and never conflicts.                                                                                                                                |
| Anything else                                                                  | every member name the Mongo adapter writes was checked against `apps/server/src` and `packages/shared/src` | None found beyond `version`, `activationVersion` and `appliedAt` (in `migrations`).                                                                                                                                                                                                                                |

Behaviour that depends on it, checked:

- An edit changes nothing in the audit because of the version: the audit diff is computed from the edited members only (`diffFields` over the document before and after the patch), so `version` is in neither the `before` nor the `after` of an entry, and an edit that changes nothing still writes and audits nothing.
- Export and import: the JSON export carries `version` and `activationVersion`; the import keeps only the members its schema knows, so an export written by either application imports into Node (both directions were run).
- Because of the above no change in `apps/server` is needed, and none was made. The regression test `apps/server/test/dotnet-written-documents.test.ts` writes these members and collections into a database and checks that Node starts on it, serves every list, accepts a PATCH on a user, room, task, plan, badge and the settings, audits only what changed, and exports and imports. It passes today; it fails when somebody makes a Node schema strict or lets the data layer iterate over the collections it finds.

The property to protect: until the switch is final, a change to the .NET adapter that adds a member or collection must keep Node able to read it. Extend that test in the same change.

**Dry run of this document's tooling.** The compose profile, the copy script and the parity script were run on a throwaway stack (own compose project name, random ports, removed afterwards) with a household built through the Node API (people, rooms, tasks, a four-week plan, completed, skipped and one-off work, points, a redemption, bonuses, badges and awards, inactive records, a draft and a discarded plan): the copy matched the source document for document, the parity script found everything equal and found every difference that was introduced by hand, and the Node application then ran on a database that the .NET application had written to (edits through the v2 API, a plan activation, a completion and a redemption). Not covered by that run: a cycle boundary or a day with due and overdue tasks, delivery to ntfy or Home Assistant, a real AI provider and Kibana. Those are the maintainer's checks above.
