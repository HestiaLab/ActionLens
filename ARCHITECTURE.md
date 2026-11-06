# Architecture

ActionLens reads a GitHub Actions workflow and explains what a given trigger would do. It never runs
anything ([ADR 0001](docs/adr/0001-static-preflight-no-execution.md)), it answers in three states
([ADR 0002](docs/adr/0002-three-state-evaluation.md)), and it does all of it in the browser
([ADR 0003](docs/adr/0003-local-first-no-backend.md)) inside an Atlas-provided shell
([ADR 0004](docs/adr/0004-consume-atlas-blazor-0-4-0.md)).

## Layers

```text
ActionLens.App (Blazor WASM)
  │  host only: Program.cs, index.html, CodeMirror interop, asset placeholders
  ▼
ActionLens.Ui (Razor class library)
  │  components, presentation services, AnalysisSession, SVG graph, CSS
  ▼
ActionLens.Core (pure C#, no Blazor, no JS, no network)
  Yaml        →  Workflows     read text, keep positions, check schema
  Expressions →  Values         parse ${{ }} and evaluate in three states
  Matrix                       expand strategy.matrix
  Triggers                     match on: filters against the event payload, in three states
  Graph                          build the job DAG and lay it out in layers
  Scenarios                      decide each job's outcome for a given run
  Linting                        AL001–AL008, always positioned
  Reporting                      one AnalysisReport, JSON-exportable
```

The dependency rule is one-directional and enforced by the project graph: `Core` does not know Blazor
exists, and `Ui` is a Razor class library rather than part of the WASM host so its components can be
rendered by bUnit. That makes the whole engine testable — and is tested — without a browser. The host
registers the JS-backed `IEditorModule` implementation behind the interface `Ui` defines.

## Data flow for one analysis

```text
YAML text ─┬─▶ YamlSafetyScanner (token pass)      size · documents · aliases · depth · duplicate keys
           └─▶ WorkflowModelBuilder (node pass)  merge keys · schema · positions
                        │
                        ▼
                 WorkflowDocument ── MatrixExpander ──▶ combinations
                        │                                   │
                        ├───▶ JobGraphAnalyzer ──▶ layers   │
                        ├───▶ TriggerEvaluator ──▶ verdict  │
                        │                                   │
                        └──────────▶ ScenarioEngine ◀───────┘
                                        │  ExpressionEvaluator (three states)
                                        ▼
                                  AnalysisReport · Diagnostics
```

Nothing in this path opens a socket, reads a file, reads the clock, or uses a random number. The same
input produces byte-identical output on every machine, which is what makes the golden corpus in
`tests/golden/languageservices` usable as a test and what makes a shared URL honest.

## Positions are part of the model

`SourcePosition` / `SourceRange` are 1-based line and column, matching the editor. Every field that a
finding can point at records where it came from (`SourceMap`), including whether the value arrived
through a YAML merge key (`<<: *anchor`), in which case the position is the anchor's definition rather
than the use site and the model says so instead of looking wrong.

The convention is settled: **`SourceMap.Get(field)` is the field's value node, from its start**, so
`Get("runs-on")` on `    runs-on: ubuntu-latest` is the `u` at column 14 — that is the thing a finding
about a value has to highlight, and it is what the loader records everywhere. A finding about a *name*
GitHub did not accept (an unknown key, or `matrix:` written on the job instead of under `strategy`)
points at the **key node** instead, because the word is what the author has to edit.

Two passes are deliberate: reading the object graph first would mean a hostile file is already
half-built before anything checks it. The token pass rejects alias bombs, runaway depth, multi-document
files and duplicate keys while the input is still a stream of events.

## Three-state expression semantics

`unknown` is not a placeholder for "false". Kleene logic keeps the definite answers definite:

| | `&& false` | `&& true` | `|| true` | `|| false` | `!x` |
|---|---|---|---|---|---|
| `unknown` | `false` | `unknown` | `true` | `unknown` | `unknown` |

Any comparison with an unknown operand is unknown, and every unknown carries an `UnknownReason` tree
naming the context, the missing value and the sub-expression that forced it. A job is reported as
`WillNotRun` only when the condition is definitely false, so the set of jobs shown as running is never
smaller than the set GitHub would run.

Conditions are evaluated with the semantics in [docs/expression-semantics.md](docs/expression-semantics.md),
which records each rule with its source: the GitHub documentation, the C# evaluator in
`actions/runner`, the TypeScript reference in `actions/languageservices`, and the shared corpus under
`expressions/testdata` that the latter declares every implementation should pass.

### Contexts are restricted before they are restricted by knowledge

GitHub allows different contexts in different positions. A name that is not allowed in a position is a
schema error GitHub would reject the workflow for, which is a different finding from "this value only
exists once something has run":

| Position | Contexts | Extra functions |
|---|---|---|
| `jobs.<id>.if` | `github`, `needs`, `vars`, `inputs` | status functions |
| `jobs.<id>.steps.<n>.if` | `github`, `needs`, `strategy`, `matrix`, `job`, `runner`, `env`, `vars`, `steps`, `inputs` | status functions, `hashFiles()` |

`secrets` appears in neither row, which is why referencing it in an `if:` is an error rather than an
unknown (lint AL004). `hashFiles()` never resolves: it reads the checkout, and ActionLens has no
checkout ([ADR 0001](docs/adr/0001-static-preflight-no-execution.md)).

## Where the hard cases live

- **Merge keys.** `YamlDotNet`'s `MergingParser` reports a mapping that merges an anchor and then
  overrides one of its keys as a *duplicate key*, which is a very common and perfectly legal workflow
  shape. `WorkflowModelBuilder` therefore resolves `<<` itself with explicit-keys-win precedence.
- **YAML 1.1 types.** GitHub parses workflow files as YAML 1.1, so `on:` is a boolean unless quoted and
  a matrix entry of `3.10` is the number `3.1`. `YamlScalarResolver` reproduces that, and the model
  keeps the author's raw text beside the resolved value so the UI can show both. The one deliberate
  deviation — bare `y`/`n` stay strings — is documented at the point it is made.
- **`if:` without a status function.** GitHub evaluates it as `success() && (condition)`. Missing that
  wrap reports jobs as running that GitHub would skip, so `ConditionEvaluator` applies it and the
  report says it was implicit.
- **Skipped ancestors.** Whether a dependent job's `success()` is true after an ancestor was *skipped*
  is genuinely unresolved in the documentation (see the ambiguities section of
  `docs/expression-semantics.md`). ActionLens answers `unknown` there instead of picking a reading.

## Testing strategy

| Layer | Method |
|---|---|
| Expression semantics | the vendored GitHub corpus (1003 cases) plus a hand-written golden table |
| YAML reading | positioned diagnostics asserted as exact `line:column` pairs |
| Matrix | one test per documented `include`/`exclude` rule, including the counter-intuitive ones |
| Graph | cycles, missing `needs`, longest-path layering, determinism of the layout |
| Scenarios | default versus modified scenario, with reasons asserted, not just values |
| UI (component level) | bUnit in `ActionLens.Ui.Tests`, on the JS-degraded path via `StubEditorModule` |
| UI (real browser) | Playwright against real Chromium — boot without console errors and all assets 200, same-origin resources, caret/order/undo under CodeMirror, diagnostic jump, example swaps, editor remounting and interop recovery; green in Gate 2 CI |

Errors are asserted as *positions and codes*, never as message text alone, so a reworded message cannot
break a test that was really checking location.

The contract cases in `tests/ActionLens.Core.Tests/ContractRegressionTests.cs` are the other half of
that rule: each one is an input that produced `INT001` (an internal failure reported to the reader) in
a run where the entire existing engine suite was green. They pin the promise in `SECURITY.md` that malformed
input yields a positioned finding rather than an exception, and that `unknown` is never collapsed into
a definite answer.

## Roadmap

The order here is a decision record, not a promise of dates. Gate 1 passed 2026-11-04; Gate 2 is
defined in `CHANGELOG.md` and is green in real CI on `main`, `dev` and pull requests. Publishing
remains a separate, explicit act.

1. **Gate 2 (done)** — the contract regression cases, `DocFactsTests`, the Playwright suite and
   `.github/workflows/gate2.yml` all green; both CI jobs pass on every `dev` push and pull request.
   See `CHANGELOG.md` for the run IDs.
2. **Publishing (not done)** — the first Pages deployment and the first release. A green Gate 2 does
   not publish anything by itself.
3. **Deferred, and recorded as such in `docs/limitations.md`** — URL sharing, autosave, language and
   theme switching, the matrix table, richer DAG interaction. These are not in `ADR 0003` any more;
   see that ADR's "Deferred" table.
4. **Structural cleanup, after the functional freeze** — done in three batches: the duplicated
   Levenshtein / JSON-to-value conversions were folded into shared helpers, the public surface was
   narrowed to the 15 contract types, and the four longest Core files were split into partials
   (`docs/review-v1-grill.md` §§16–18). What remains is not structural: the first Pages deployment
   and the first release are still explicit, unperformed acts.
