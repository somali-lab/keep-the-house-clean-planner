# ADR-0010 — Browser notifications while the planner is open

Status: Proposed

## Context

The server can already send each person a morning summary through ntfy or Home Assistant. That channel is configured by the administrator in the environment, targets everyone at one fixed time, and needs infrastructure outside the application.

Household members also keep the planner open in a browser tab on a Windows computer, and want a notification on moments they choose themselves, per person. The decision is how to deliver such notifications without adding a delivery channel that cannot work in this deployment.

Constraints that shape the options:

- The application has no authentication and runs on a trusted network, often behind a reverse proxy (ADR-0003). A web push subscription needs a push service, VAPID keys and a stored subscription per device, and none of that exists here.
- The browser only shows notifications after the person grants permission, and the permission belongs to the browser on one device, not to a person.
- Several tabs of the same browser can be open, and each of them runs the same code.
- Calendar days and times are reasoned about in the household timezone through the shared helpers, never with local `Date` arithmetic (ADR-0002).

## Decision

**Notifications are shown by the page itself, while it is open in a browser tab.** A tab that is not active counts as open. There is no service worker push and no delivery to a closed browser; that channel stays with ntfy and Home Assistant, which remain a separate setting that the administrator manages under Settings, Jobs.

**Moments are stored on the server, on the person.** A user holds `browserNotifications: { enabled, times }`, where `times` are up to six unique `HH:mm` values in the household timezone, stored sorted. A document that predates the field reads as disabled without times. A person changes their own moments and an administrator can change anyone's, through one write route that follows the same actor rules as other user changes and is audited like them; a change that changes nothing writes and audits nothing (ADR-0004). Storing them server-side keeps a person's choice identical on every device and survives a cleared browser; only the permission is device-local.

**Permission is requested from an explicit button** on the notifications page, which also shows whether permission is not yet asked, granted, blocked or unsupported, and offers a test notification.

**Delivery is computed by a pure model.** A hook mounted once at app level, for the active profile only, asks the model for the moments that are due and the next one to come. A moment is delivered when the page is open at that moment or within a ten-minute grace period after it; older moments are never caught up. One timer is armed for the next moment, capped at one minute so a sleeping computer or a changed clock is noticed, and it is re-armed when the tab becomes visible or is restored, and when the profile, the person's moments or the household timezone change.

**At most one summary per person, day and moment, however many tabs are open.** Inside `navigator.locks.request('khc-browser-notify', …)` the tab checks and sets a claim key `khc.notified.<personId>.<dayKey>.<HH:mm>` in `localStorage`. Where the Web Locks API is not available, the tab writes a random token, reads it back after a short delay and only the tab whose token survived delivers. A claim is written before the lookup of the person's tasks, so it is pending until its tab has shown the notification, or found nothing to show, and marked it done. A failed lookup or display gives the claim back, and a pending claim older than 60 seconds is stale: the next check of any tab inside the grace period takes it over, and the earlier holder verifies it still owns the claim before showing anything. A done claim is never taken over. Storage that throws falls back to a claim held in memory by the tab. Claims older than seven days are pruned.

**Content is the person's own open tasks for today and their overdue ones**, looked up through the existing occurrence API at the moment of delivery. When nothing is open, no notification is shown. The title and body come from the message catalogues; the body lists up to five task names and summarises the rest as "+N meer".

## Alternatives considered

- **Web Push with a service worker.** Delivers to a closed browser, but needs VAPID keys, a push service, stored subscriptions per device and a server-side scheduler per person. That is a new channel with its own secrets in an installation that is deliberately simple, and a person who wants it has ntfy.
- **Extending the ntfy and Home Assistant morning job with per-person times.** Reuses the existing channel, but still needs those services and would blur two different settings: one is environment configuration for the household, the other is a personal choice.
- **Storing the moments in the browser.** No server change, but a person would configure every device again and a cleared browser would silently stop the notifications. An administrator could also not help someone set them up.
- **Server-sent events or polling so that the server decides when to notify.** Moves the clock to the server, but the server cannot know which tabs are open or whether permission was granted, and it would add a long-lived connection per tab.
- **Delivering from one tab elected as leader.** Avoids duplicates without a lock, but needs leader election and failover when that tab closes. The claim in shared storage needs neither.
- **Catching up on every missed moment when the tab is restored.** A notification hours late reads as a bug, and a person who opened the planner at lunch would be greeted by the morning summary. The grace period keeps notifications current.

## Consequences

- A person who never has the planner open at their configured time gets nothing. This is intended and stated in the interface; the ntfy and Home Assistant channel is the answer for that need.
- Permission must be granted once on every device and browser. Notifications only work on a secure origin: HTTPS, for example through the reverse proxy, or localhost. An installation that is only reached over a plain-HTTP LAN address (`http://192.168.x.x:3000`, `http://huis.local`) cannot show browser notifications at all; the notifications page detects this, says so and disables its buttons, and the README states the requirement. Such installations keep ntfy and Home Assistant.
- A tab that hangs for more than a minute between claiming and showing can lose the moment to another tab, and in a very narrow race both could show it. The claim is checked again just before showing to keep that window small.
- Which tab delivers is not defined, only that exactly one does. The Web Locks path is exact; the fallback is a short race window that a lost write resolves, and a storage failure can at worst cause one duplicate.
- Timers in a hidden tab can be throttled by the browser. The grace period absorbs that, and a moment that is late by more than the grace period is dropped on purpose.
- A configured time that a daylight-saving day skips is shown after the gap, and an ambiguous time is shown at its first occurrence.
- The summary is a snapshot of the occurrence API at that moment. Anything that changes afterwards is not corrected.
