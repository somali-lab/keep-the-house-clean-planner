# ADR-0006 — Server-side PDF rendering

Status: Accepted

## Context

The printed sheet on the fridge is a real output device, not a fallback. It has to look the same no matter who produced it. Browser printing fails that requirement: margins, fonts, and page breaks differ per browser, per platform, and per printer dialog.

## Decision

PDFs are rendered on the server from an HTML and CSS template, using a headless browser. A dedicated PDF library would be more work for a document that is essentially a styled table.

The browser instance is shared and started lazily on the first export, and all network access is blocked while a page renders.

The output is black-and-white safe: no information is carried by colour alone. Weight, borders, and strikethrough carry meaning instead.

Exporting is a read operation. It requires no profile and changes nothing.

## Consequences

- Two people exporting the same week get byte-comparable sheets.
- The runtime image must contain a browser. Only the headless shell of one engine is installed, which is what keeps that cost bounded.
- The first export after a restart is slower than the ones after it.
- Only periods that have actually been generated can be exported, and the interface says so rather than producing an empty sheet.
