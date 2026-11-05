# Changelog

Formatting follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project aims at
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Versioning has one caveat worth stating before it surprises anyone: ActionLens' observable output is a set
of findings and `unknown` reasons, so **adding a lint rule or a diagnostic code, or changing what a rule
reports, is a breaking change for anyone parsing the JSON export**, even at `0.x`. Those are called out
per release.

## [Unreleased]

Nothing is released yet — no published package, container image or hosted Pages deployment. The source
repository is live at `HestiaLab/ActionLens`, with `dev` as the integration branch and `main` as the
stable branch. Gate 1 passed 2026-11-04; **Gate 2** below is the condition for the first release and
Pages deployment.

### Gate 2 — the release gate

Gate 2 is all of the following, run locally first and then in `.github/workflows/gate2.yml`:

1. `dotnet build ActionLens.sln -v q --nologo` → 0 errors, 0 warnings.
2. `dotnet format ActionLens.sln --verify-no-changes` → exit code 0.
3. Both xUnit v3 / Microsoft.Testing.Platform assemblies green (run the built `.exe` files directly,
   see `CONTRIBUTING.md`; `dotnet test` is not supported on this SDK).
4. `DocFactsTests` green: corpus shape, Markdown links and ADR/deferred consistency.
5. The whole Playwright suite green: boot without console errors and all assets 200, resource hosts all
   same-origin, caret/order/undo under real CodeMirror, loading an example replacing the document,
   diagnostic jump, repeated document swaps leaving no console errors or orphan editors, typing across
   several debounce windows without remounting the editor, and recovery from a transient JS interop
   failure.
6. `ContractRegressionTests` green and the P0 list in `docs/review-v1-grill.md` cleared.
7. `CrossProcessDeterminismTests` and `WorkspaceRenderTests` green — two separate processes produce
   byte-identical exports, and session notifications that do not change the document do not re-render
   the shell.

All seven held in the first real CI run: `gate2` on `main` at `2363021`
(run [`41977057618`](https://github.com/HestiaLab/ActionLens/actions/runs/41977057618), 2026-11-05,
both `engine` and `browser` jobs green). Subsequent runs are also green: the `dev`-gating and Q6
changes on PR #1 (runs `41981780325`, `41986150317`), and the first `push` to `dev` after that PR was
merged at `f07a096` (run `41987481649`). No release, no Pages deployment and no "verified" claim
follows from a green run alone — publishing remains an explicit act. Gate 1's engine decision was
never a release decision.

### Added

- `ActionLens.Core`, pure C# with no Blazor, JS interop or network dependency:
  - two-pass YAML reading — a token-level safety pass bounding size, documents, anchors, nesting depth and
    duplicate keys, then the object graph with 1-based line/column positions and YAML 1.1 type resolution
    (`on:` as a boolean key, `3.10` as `3.1`); merge keys (`<<: *anchor`) are resolved with
    explicit-keys-win precedence rather than delegating to the loader, whose merge parser misreports a
    legal override as a duplicate key;
  - GitHub Actions expression lexer, parser and a three-state evaluator (`true` / `false` / `unknown`,
    Kleene logic) with a cause trace behind every `unknown`, the documented operator precedence, operand
    returning `&&`/`||`, the implicit `success() && (condition)` wrap, and per-position context legality;
  - matrix expansion with Actions' `exclude`-then-`include` ordering, filter/extra key split, the
    empty-filter and unmatched-filter rules, expression-driven dimensions refused rather than guessed, and
    the 256-combination ceiling;
  - job graph: `needs` edges, missing dependencies, cycles reported with the actual path, longest-path
    layering with document-order slots, deterministic layout;
  - scenario engine: default and per-job failure/skip/cancellation, skip propagation, job-level and
    step-level context providers, and `unknown` where GitHub's own documentation does not settle
    `success()` after a skipped ancestor;
  - `on.*` trigger filtering with the documented filter, negation-ordering and base-vs-head rules, and
    forced `unknown` for the cases GitHub decides from diff size or a clock;
  - eight lint rules (AL001–AL008), each positioned; AL004 and AL006 promote the engine's own findings so a
    problem is reported once;
  - `WorkflowAnalyzer` entry point and `AnalysisReport` with deterministic JSON export (stable key order, no
    timestamps or identifiers).
- `ActionLens.Ui`, a Razor class library holding the components, presentation services and `AnalysisSession`,
  so bUnit can render them. `ActionLens.App` is the thin WASM host: `Program.cs`, `index.html`, the editor
  bundle and JS interop.
- `ActionLens.App`, Blazor WebAssembly on the published `Atlas.Blazor` 0.4.0 package: workspace shell,
  CodeMirror 6 editor (bundled from TypeScript; the build fails when the bundle is missing **or when its
  rebuild fails**), live re-analysis on edit, an Inspector showing each job's condition trace, a diagnostics
  list whose entries move the editor to the offending line, a run summary including the trigger verdict,
  and a self-drawn SVG job graph whose three states are encoded by symbol and text as well as colour.
- Tests: xUnit v3 suites covering the engine, bUnit component tests in `ActionLens.Ui.Tests`, GitHub's
  own published expression cases vendored from `actions/languageservices` under its MIT licence with
  attribution, and a source-level local-first guard covering the host, the UI library and the Node scripts.
- Documentation: `ARCHITECTURE.md`, `docs/expression-semantics.md`, `docs/limitations.md`, four ADRs,
  `docs/gate1-report.md`, `docs/w4-verification.md`, `docs/review-v1-grill.md`, `SECURITY.md`,
  `CONTRIBUTING.md`, and `docs/notes/atlas-0.4.0-api.md`.

### Changed — one public entry point (2026-11-05)

Decision F asked for a narrower public surface; §7 recorded roughly 55 of 60 Core files exposing a
public type. The boundary was determined by the compiler, not by guesswork: 41 candidate types never
named outside Core were made `internal`, and every type the compiler rejected (CS0050/51/53/60,
because a public signature references it) was restored. Three iterations converged.

- 27 types are now internal: `ScenarioEngine`, `LintEngine`, the eight lint rule implementations,
  `ScenarioEvaluation`/`JobScenarioResult`, `ConditionEvaluator`, `ExpressionFunctions`, expression
  node subtypes, `EvaluationScope` and friends.
- 15 stay public because they *are* the contract: `ExpressionNode`, `ILintRule`, the lint result
  types, `MatrixCombination`/`MatrixRun`, `JobGraphEdge`/`JobGraphAnalysisResult`, `StepDefinition`,
  `DefaultsSection`, `ConcurrencySection`, `WorkflowInput`, `TemplateSegment`.
- `PublicSurfaceTests` (11 assertions) pins the boundary against a future "just make it public" edit;
  the compiler already enforces the opposite direction.

This also fixed a real defect §7 had only flagged by inspection. `ScenarioEngine.Run` took the
workflow and the job graph as two independent arguments and indexed `_jobs[jobId]` directly, so a
caller supplying a graph built from a different document got a raw `KeyNotFoundException`. Making the
engine internal removes that path for external callers; inside, `ScenarioRunner` now filters ids it
cannot place and emits an `INT001` diagnostic per unknown id instead of throwing or inventing a
result. `ScenarioEngineMismatchTests` reproduces the mismatch and an A/B check confirms it fails
again when the guard is removed.

Splitting the long files (`WorkflowModelBuilder`, `TriggerEvaluator`, `ExpressionBodyParser`,
`AnalysisReportJson`) is mechanical and remains for a later batch. Pages deployment and the first
release are still explicit, unperformed acts.

### Changed — Q6 second batch: shared conversions and CI currency (2026-11-05)

Gate 2 went green in CI, unlocking the post-gate structural work (decision F). Reviewing the code
corrected two claims in `docs/review-v1-grill.md` §7: there are **two** Levenshtein copies, not three
(the third is a positional hint heuristic with different behaviour), and **two** JSON→value copies, not
three (the third converts YAML, which needs scalar resolution and source positions).

- `Text/EditDistance.cs` and `Values/JsonValueConverter.cs` are `internal`, not `public`: they were
  introduced as public by mistake, widening the surface decision F asks to narrow. A
  `SharedHelpers_AreNotPartOfThePublicApi` assertion guards against a regression.
- `Text/EditDistance.cs` now owns `LevenshteinIgnoreCase`; `JobGraphAnalyzer` and `RunnerLabelTypoRule`
  call it instead of carrying their own copies. The `WorkflowModelBuilder` heuristic stays separate,
  with a comment explaining why sharing it would change the suggestion a reader sees.
- `Values/JsonValueConverter.cs` now owns the JSON→`GitHubValue` mapping used by both `fromJSON()` and
  the event payload reader. The "the two must agree" comment in `AnalysisInput.cs` is now enforced by
  structure rather than by discipline. Seven new assertions in `SharedConvertersTests` pin the mapping.
- CI: runner pinned to `ubuntu-24.04`; `checkout`/`setup-dotnet`/`setup-node` → v5 and
  `upload-artifact` → v6 (first Node-24 stable majors), removing both runner annotations.
- Narrowing the public surface and splitting the ~1400-line builder are **not** in this batch: those
  change API visibility and need their own decision.

### Added — determinism, render narrowing and interop recovery (2026-11-05)

- `CrossProcessDeterminismTests`: two genuinely separate processes now export the same report bundle
  and their bytes are compared, upgrading the "cross-process variation" risk in §7 to a machine
  assertion. A companion case constructs two different `include` key orders and asserts the structured
  `runs[].values` are identical and sorted; an A/B check confirms removing the sort fails it.
- `WorkspaceRenderTests` (bUnit) reads `RenderCount` on the real `ActionLensWorkspace`: pending,
  analysis and selection notifications no longer re-render the whole Atlas shell, while an external
  document replacement still does. The browser-side DOM-identity case is explicitly documented as
  *not* being render-count evidence (an A/B check showed it passes either way).
- The editor bundle's `.catch()` on `invokeMethodAsync` now swallows a transient rejection without
  closing the editor; only `dispose()` closes it. A Playwright case injects a .NET target that rejects
  once and asserts the next keystroke still reaches it.

### Fixed — UI interop race, example switching and lifecycle (2026-11-05)

All three were found by driving a real Chromium, not by the engine suite. `docs/review-v1-grill.md`
§13 has the reproduction output and the A/B evidence.

- **Echo race**: typing `MARK` at the start of the document produced `KRAM` — `CodeEditor` did not
  record text it reported outward, so every keystroke was treated as an external value and pushed
  back through `setText`, resetting the caret and flattening undo. Fixed in `CodeEditor` (synchronise
  `_text`) and in `ClientScripts/editor.ts` (a no-op `setText` no longer dispatches). The old
  browser assertion typed at the end, where a whole-document replacement happens to leave the caret
  in the right place; the new one types at the start.
- **Stale editor after switching examples**: `ActionLensWorkspace` did not subscribe to
  `Session.Changed`, so the `Text="@Session.WorkflowText"` fragment never re-evaluated — the report
  described the new workflow while both editors still showed the old one. Now subscribed and unsubscribed
  with the component.
- **JS interop lifecycle**: `invokeMethodAsync` in the editor bundle had no `.catch()`, and
  `EditorModule.DisposeAsync` left `_loading` cached and could hand out a disposed JS reference,
  skipping the bundle's `dispose()` and leaking a `ResizeObserver`. The bundle now closes callbacks on
  disposal; the module marks itself disposed, drops the cached import task, and refuses later callers.
- **Fixture form**: the three reproduced malformed shapes and the trigger/matrix cases now live in
  `tests/ActionLens.Core.Tests/ContractFixtures/regressions.yml`, consumed by an end-to-end assertion,
  in addition to the isolated cases.

### Fixed — contract review (2026-11-05)

`docs/review-v1-grill.md` records the decisions behind this batch. Every item below was reproducible as
`INT001` ("ActionLens stopped partway through this analysis") or as a confidently wrong answer while the
whole existing suite was green.

- **P0 / wrong answers and crashes**: `join()`, `toJSON()` and `fromJSON()` called with no arguments now
  yield `EXPR004` and an `unknown` reason instead of an unhandled `ArgumentOutOfRangeException`; a push
  declaring only `branches-ignore:` with no payload no longer indexes an empty list; an `include` entry
  with keys differing only by case now produces `MTX003` instead of throwing; `hashFiles(unknown)` keeps
  its `RuntimeFunction` reason; expression token positions are offset onto the file instead of collapsing
  to `1:1`, and reason messages name a readable path rather than dumping C# record internals.
- **Determinism**: `run.values` keys and declared trigger order are now sorted / positioned explicitly;
  `GitHubValue.GetHashCode()` is order-independent for objects, matching `Equals`.
- **Bounds**: the matrix product is computed arithmetically and refused above 4096 combinations before a
  single combination object is allocated; an expanded-node budget (`YAML009`) rejects alias bombs and
  cyclic anchors before any recursive walker sees them; merge-key depth is actually decremented;
  `RawText` has a depth bound; the event payload has a 1 MB ceiling and payload derivation can no longer
  escape the public `Analyze` as an exception; `fail-fast` / `max-parallel` are reported as unmodelled
  (`MTX005`) instead of silently dropped.
- **Build and format**: the editor bundle's `npm` step no longer swallows a failed rebuild; the four
  `dotnet format` violations are fixed and the format gate is green again.
- **Guard rails**: the vendored corpus is pinned by assertions on file count, 1003 cases, 10 by-rule
  exclusions and 993 strong assertions, and its exclusion lists are content-locked; `LocalFirstGuardTests`
  now scans `*.js`/`*.mjs`/`*.css`/`*.html`, the `scripts/` directory, more network construction sites,
  and has a self-test for its external-asset half; `DocFactsTests` checks Markdown links and ADR/deferred
  consistency.
- **Documentation**: `ADR 0003` narrowed to the no-backend decision with an explicit "Deferred — not
  implemented" table; `ARCHITECTURE.md` gained the missing `Ui` layer, a roadmap and an honest Playwright
  status; the Chinese README note was corrected; fixture tests now assert the `MTX005` finding rather
  than excluding it.

**Breaking change to the JSON export**: two diagnostic codes were added — `MTX005`
(`MatrixSchedulingNotModelled`) and `YAML009` (`YamlExpansionLimit`). Both are emitted only for inputs
that previously either crashed (`INT001`) or silently dropped information.

### Known gaps

No URL sharing, autosave, language switching or theme switching yet — Language and Theme exist as disabled
controls rather than fake ones; the other two have no control at all. No matrix table, no reusable-workflow
or composite-action expansion, no step-output propagation, no cost or duration estimates (see
`docs/limitations.md`). bUnit component tests run on the JS-degraded path via `StubEditorModule`; real
CodeMirror, real interop and the Atlas host workspace still need the Gate 2 Playwright suite, and the
manual browser gate remains in force until that suite is green.

[Unreleased]: https://github.com/HestiaLab/ActionLens
