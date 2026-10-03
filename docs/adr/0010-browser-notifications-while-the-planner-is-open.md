# ADR-0010 — Browser notifications while the planner is open

Status: Accepted

## Context

The server can already send a morning summary through ntfy or Home Assistant. Household members also want a notification from the browser at moments they choose themselves. The application has no authentication and runs on a trusted network, often behind a reverse proxy (ADR-0003), so a delivery channel must not need infrastructure that this deployment does not have. Several tabs of the same browser can be open at once, and each runs the same code.

## Decision

Notifications are shown by the page itself, from a timer in the open tab. When several tabs are open, they claim each delivery through a lock shared between them: `navigator.locks` where the Web Locks API exists, and a claim key in `localStorage` that the tab confirms by reading back a random token where it does not. At most one tab delivers a given notification.

## Alternatives considered

- **Web Push with a service worker** reaches a closed browser, but needs VAPID keys, a push service, a stored subscription per device and a server-side scheduler per person. That is a new channel with its own secrets for an installation that is deliberately simple, and ntfy and Home Assistant already serve that need.
- **Server-side delivery** (extending the ntfy and Home Assistant job with per-person times, or server-sent events) moves the clock to the server, which cannot know which tabs are open or whether permission was granted, and it blurs household configuration with a personal choice.
- **Electing one tab as leader** avoids duplicates without a lock, but needs election and failover when that tab closes. The claim needs neither.

## Consequences

- A person who does not have the planner open at the chosen time gets nothing. The server channels remain for that need.
- The Notifications API only exists on a secure origin: HTTPS, for example through the reverse proxy, or localhost. An installation reached over a plain-HTTP LAN address cannot show browser notifications and keeps ntfy and Home Assistant.
- Exactly-one delivery is exact on the Web Locks path. The storage fallback has a short race window, and a storage failure can at worst cause one duplicate.
- Timers in a hidden tab can be throttled by the browser, so delivery timing is best effort.
