# ADR-0015 — Reward meter progress

Status: Proposed

## Context

The household wants a tab that shows how far a person is towards a goal for the current week or cycle: the points earned, what they are worth in money, a basket that fills with eggs and a chicken that walks along the meter. When the meter is full a short animation plays, once. People who asked their system for reduced motion get the same information as text, and the earned badges appear on the same tab.

The points ledger (ADR-0011), the bonuses (ADR-0012), the conversion to money and the redemptions (ADR-0013) and the badges (ADR-0014) already hold every fact the meter needs. Several constraints shape the design.

**Progress is a read.** Nothing about "how full the meter is" has to be stored: it follows from the ledger and the occurrences. A stored percentage would drift after an undo, a correction or an import, exactly like the counters ADR-0014 rejected.

**A period is the week or the cycle of today.** Weeks run Monday to Sunday and cycles are 28 days from the Monday anchor. Both are computed with the shared day-key helpers (`weekOf`, `cycleOf`), so a week across a DST change is 167 or 169 hours long and the period still starts and ends on local midnight.

**Spending points must not move the meter back.** A redemption is a negative ledger entry (ADR-0013). If it reduced progress, a person who cashed in half of their points on Tuesday would see their week get worse.

**A goal needs an owner of the work.** ADR-0012 already decides who a planned occurrence belongs to for a period: the frozen `periodOwnerId`, else the assignee, placed by the day it was planned for. A goal that is "the work planned for me" must use the same rule, or the meter and the bonus would disagree about whose work it is.

**The animation is a one-time event per person and period.** There is no server-side fact for "the person saw the celebration", and inventing one would need a write path, an audit entry and an export for something that is purely cosmetic.

## Decision

### A read endpoint

`GET /api/points/progress?personId&period=week|cycle` needs no profile, like the other points reads. It answers:

```ts
{
  personId, period, start, end,   // the week or cycle of today in the household timezone, both days included
  earnedPoints,                    // executions and bonuses dated in [start, end]; redemptions are not subtracted
  goalPoints,                      // null when there is no goal
  goalSource,                      // 'explicit' | 'automatic'
  percent,                         // 0..100, capped; 0 without a goal
  currencyCode, centsPerPoint,
  money: { earned, goal } | null   // whole cents at the factor in force now; null while a point is worth nothing
}
```

Earned points are the sum of the person's ledger entries of kind `execution` and the four bonus kinds whose date lies in the period, the same range rule as the balances (local midnight of the first day up to local midnight after the last day). The bonuses of ended weeks therefore count for the cycle they ended in, and a bonus is dated on the last day of its period, so it shows when the period has ended and never before (ADR-0012). A redemption has another kind and is not read.

`percent` is `floor(earned * 100 / goal)`, capped at 100: it only reaches 100 when the goal is met. The first egg is for 10%, the tenth for 100%.

### The goal

Settings gain `rewardGoals: { weekPoints: number | null, cyclePoints: number | null }`. Each value is an integer from 0 to 100000 or `null`. A missing value means both are `null`, and `GET /api/settings` always returns it. `PATCH /api/settings` accepts `rewardGoals` from administrators (the existing guard), as a whole object; a value equal to the one in force, where missing means `{ null, null }`, writes and audits nothing, and a change is one settings `update`.

- **An explicit goal** is used as it is, for everybody, including a person nothing is planned for. `0` means no goal for that period.
- **`null` is the automatic goal:** the sum of the points of the person's planned occurrences in the period, at least 1. Planned means every occurrence that was not recorded as done (so generated work and work put on a day by hand), placed in a period by the day it was planned for (`plannedDate`) and with the person it was planned for as owner, by the rule of ADR-0012. Done work that somebody else did stays in the owner's goal and is earned by the person who did it, so a person who helps does not get a bigger goal, and the owner's meter stays honest. Skipped work cannot be earned and is left out; recorded extra work was never planned and is left out. The points of an occurrence are its snapshot when it is done, otherwise what its task is worth now, otherwise the duration rule (ADR-0011).
- **Nothing planned** gives `goalPoints: null` and `percent: 0`, and the meter says "no goal this period" instead of 0 of 0.

The planned occurrences are only read when the goal is automatic. A new index on `occurrences.plannedDate` finds them.

### Not stored: the celebration

The completion animation is remembered in the browser, in `localStorage` under `khc.rewardCelebrated.<personId>.<period>.<startDayKey>`. A new week or cycle has a new start day and therefore plays again; another person on the same device has their own key. The animation plays when a person first sees a full meter in a period, then never again for that period, also after a reload. Storage that is unavailable means it is not remembered: it may play again after a reload, which is harmless.

With `prefers-reduced-motion: reduce` the animation never plays and nothing is remembered; the meter shows the static text "Doel gehaald!" instead. That text is also shown, without motion, whenever the goal is met, so the state is never only an animation. The chicken stands in place at once and no CSS transition or keyframe runs.

### The tab

A fifth overview tab, "Beloning" ("Reward"), shows the active profile: a week/cycle toggle (remembered per profile like the other filters), an inline SVG scene with the chicken and the basket (`role="img"` with a text alternative), a progress bar (`role="progressbar"` with `aria-valuenow`, `aria-valuemin`, `aria-valuemax` and `aria-valuetext`), the line "{earned} van {goal} punten ({percent}%)", the money, the number of eggs as text and, when the goal is met, the message. Missing eggs are dashed outlines, not only a missing colour. The earned badges of the person are shown below, with the components of ADR-0014. The layout is deliberately simple and accessible; the maintainer will fine-tune it later.

### Settings and audit

A card "Beloningsdoelen" next to the bonuses and the conversion lets administrators set both goals; an empty field means automatic. Changing the goals is a settings `update` in the history. Reading the progress writes and audits nothing. The goals travel in the export as part of the settings (an optional field, so an older file imports with automatic goals and the export version stays 6).

## Alternatives considered

- **A stored progress document per person and period**, updated at every check-off. It drifts after an undo, a correction, an import or a changed goal and needs its own reconciliation. The read is a sum over an indexed range and a short list of planned occurrences.
- **Counting redemptions against progress** (progress = balance). Spending would move the chicken backwards, and a payout would erase an achieved week.
- **A goal as the sum of the whole household's planned work, or a fixed default.** A person who has little planned would never reach it, and a person who has a lot could not miss. The owner's own planned work is what the bonus already measures.
- **Counting skipped work in the automatic goal.** The week bonus treats skipped work as not done, so it blocks the bonus; for a meter that is meant to motivate, a goal that can no longer be reached by anything the person can still do is unfair, so it leaves the goal.
- **Server-side memory of the celebration** (a ledger or a settings field). It would add a write, an audit entry and an export field for something cosmetic, and the household devices are shared by profile, not by account.
- **A GIF or video for the animation**, or a canvas. A CSS transform on an inline SVG needs no asset, no library and can be switched off with one media query.
- **Showing a partly filled egg.** Ten whole eggs for ten steps are easy to read and to describe in text.

## Consequences

- **A week bonus never shows in the week meter of the same week.** It is dated on the last day and only written after that day, so it counts in the cycle meter and not in a week meter. This follows ADR-0012.
- **Earned points can exceed the goal.** Bonuses and work for others add to what is earned but not to the automatic goal; the percentage is capped at 100 and the points stay exact.
- **The automatic goal follows the plan.** It changes when work is planned, rescheduled to another week or reassigned inside the week, and a take-over inside the week moves the goal to the person who took it over, like the bonus. After the week ended the owner is frozen.
- **A device remembers, a person does not.** Another browser plays the animation again for the same period, and clearing site data does too.
- **A new index** on `occurrences.plannedDate` costs a little write time and some memory at household size.
- **Open product questions,** answered here with defaults: an egg per 10% with no partly filled egg; skipped work leaves the automatic goal; an explicit goal of 0 switches the goal off; recorded extra work counts as earned but never as planned; a goal applies to every person alike; the animation plays once per person and period on a device and never with reduced motion; the week meter never shows the bonus of its own week.
