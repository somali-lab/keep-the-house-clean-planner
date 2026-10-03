# ADR-0020 — QuestPDF for the PDF sheets

Status: Accepted

## Context

ADR-0006 renders sheets through a headless browser because that was the cheapest route in Node. In a .NET image that choice means installing a browser for four fixed layouts, with the start-up delay and attack surface that brings.

## Decision

The four sheets are rendered from view models with QuestPDF; the image contains no browser. The sheet rules stay as in ADR-0006: black-and-white safe, exporting is a read, only generated periods can be exported. The Community licence is set explicitly in code with a comment that names the revenue threshold.

The image needs `fontconfig` and one font package, which rules out chiseled and distroless base images.

## Alternatives considered

- Keep a headless browser: layouts stay HTML and CSS, but the image grows and the first export is slow.
- A different PDF library: comparable effort, but QuestPDF's fluent layout and bundled native dependencies fit the fixed sheets.
- An HTML-to-PDF service: no local cost, but a household application should not need a second service.

## Consequences

- Layout is code, not CSS; the rules are asserted on the view model and on text extracted from the output.
- Rendering twice produces byte-identical files, which the tests assert.
- The licence terms must be rechecked if the use of the application ever changes.
- The rendered sheets differ visually from the HTML-based ones; the maintainer reviews them during the parallel run.
