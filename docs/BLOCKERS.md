# Blockers

Open questions for the maintainer, and anything else that stops the work from continuing: a missing permission, an unavailable dependency, a choice only the maintainer can make.

This file is for work that cannot ask. An agent in a live conversation asks the question directly; an agent running unattended writes it here and stops at that point instead of guessing.

An entry names what is blocked, what is needed to unblock it, and who can supply that. The answer is written to its permanent home, the entry is removed, and this file is empty again.

## Phase 7 cannot pass the end-to-end CI job slice by slice

Blocked: PR for slice 7.1 (and every later phase 7 slice). The web app moves to `/api/v2` one feature per slice (plan §8 phase 7), but the Playwright e2e job runs the real Node server, which only serves `/api`. From 7.1 on, `today` and `week` talk v2, so the e2e job fails, and the plan only makes the journeys green at 7.6. The other CI jobs (typecheck, lint, 1478 web unit tests, .NET, OpenAPI drift, web client drift) are green.

Needed: a decision on how phase 7 reaches `next`. Options: (a) run phase 7 on a long-lived branch `next-web` where e2e is expected red until 7.6, and merge it into `next` as a whole; (b) mark the e2e job as non-blocking while phase 7 runs; (c) add a v1-to-v2 bridge in the web app so both servers work (more code, deleted again in 7.7); (d) something else.

Who: the maintainer.
