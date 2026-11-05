# ADR 0003 — Local-first, no backend

Status: accepted (scope narrowed 2026-11-05) · Date: 2026-11-04

## Context

Workflows under analysis routinely contain organisation-internal details: self-hosted runner labels,
secret *names*, deploy targets, private repository paths, vendor URLs in `uses:`. Any design that
round-trips the input through a server turns a documentation tool into a data leak, and adds an
availability and cost dependency the project does not need.

Sharing is the one feature that most obviously tempts a backend: "send me a link to your analysis".

## Decision

There is no backend. Nothing is sent anywhere.

- Analysis runs entirely in the browser (Blazor WebAssembly + pure C# core).
- No telemetry, no analytics, no font, script or stylesheet CDN. Loading the page is the only network
  activity the app performs: same-origin Blazor framework assets, same-origin `_content` stylesheets,
  and one same-origin dynamic `import` of the editor bundle. The analysis afterwards makes no request.
- `LocalFirstGuardTests` scans `*.cs`, `*.razor`, `*.ts`, `*.js`, `*.mjs`, `*.css` and `*.html` across
  the host, the UI class library and the Node build scripts, and checks every page and stylesheet for
  external hosts. It is a source-level guard; the runtime assertion belongs to the Playwright suite.

This ADR deliberately says nothing about persistence, sharing or UI language. Those are product features,
not architecture decisions. An earlier revision folded them into an `accepted` ADR as though they were
already in force, which made the repository claim features that were never built.

## Consequences

- The privacy posture has two pieces of evidence, not one: the source guard proves no network client
  was constructed, and a browser-level assertion is needed to prove no request left the page. Neither
  alone is sufficient.
- Because there is no backend and no service worker, "works offline" means the already-loaded tab keeps
  working; reloading while disconnected will not work.
- State currently lives in memory only. Cross-device continuation does not exist, and the product must
  not pretend otherwise.
- Every feature request that implies storage, accounts or a "short link" is out of scope by this ADR,
  and must reopen it explicitly rather than sneak in a service.

## Deferred — not implemented

The following were written into an earlier revision of this ADR as if they were decisions already in
force. They are design intentions, not shipped behaviour, and are listed here so the ADR cannot be read
as evidence for them. See `docs/limitations.md` for the product-facing statement.

| Feature | Status | Tracked in |
|---|---|---|
| Autosave to `localStorage` under `actionlens:session:v1` | not implemented; no `localStorage` use anywhere in `src/` | `docs/limitations.md` |
| URL-fragment sharing compressed with LZ-string | not implemented; no `lz-string` dependency | `docs/limitations.md` |
| 8 KB link threshold and JSON-export fallback | not implemented | `docs/limitations.md` |
| Language and theme switching | disabled controls, not fake ones | `src/ActionLens.Ui/Components/TopBar.razor` |
| Runtime "no outbound request" assertion | Gate 2 work | `docs/review-v1-grill.md` decision Q3 |

None of the above may be described as present until a test asserts it. The first three change where
session state lives, so implementing them requires reopening this ADR.
