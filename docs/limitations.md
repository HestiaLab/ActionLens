# Limitations

What ActionLens does not model, and why. This list exists because the tool's whole value proposition is
saying "I don't know" where it genuinely doesn't — an undeclared limitation is worse than a missing
feature, since it reads as a conclusion.

Rules the tool lives by: nothing is executed, nothing is fetched, and every `unknown` carries a reason
naming the context, the missing value and the sub-expression that forced it
([ADR 0001](adr/0001-static-preflight-no-execution.md), [ADR 0002](adr/0002-three-state-evaluation.md)).

## Not modelled at all

| Area | Behaviour | Why |
|---|---|---|
| `run:` scripts and action internals | Never executed, never parsed beyond text scanning | Executing them is the thing the product promises not to do |
| Composite actions (`action.yml`) | The call is visible; the action's own steps are not | Requires reading another repository's file |
| Reusable workflow calls (`uses: ./other.yml` or `owner/repo/…/.github/workflows/x.yml@ref`) | The call, its `with:` and whether secrets are `inherit`ed are shown; the callee's jobs are not | Needs multi-file and cross-repo analysis, out of MVP scope |
| Step `outputs` | Always unknown, including `steps.<id>.outputs.*` | Only exist after a step has run |
| Container / service images, `environment:` deployments | Presence recorded, contents ignored | Runtime and organisation state |
| Cost, duration, queue time | Never estimated | Deliberately excluded: an estimate a reader cannot check is noise |
| Multi-workflow and repo-level analysis | One workflow per session | MVP boundary; `needs:` across files is not Actions syntax anyway |

## Partially modelled

**Matrix.** Static dimensions expand exactly as Actions does — cross product, `exclude` first with
partial matching, `include` after, the filter/extra split, an empty filter applying to every combination,
unmatched filters creating a new combination, and the documented 256-combination ceiling. A dimension fed
by an expression (`os: ${{ fromJSON(needs.setup.outputs.matrix) }}`) makes the whole expansion
**unknown**: ActionLens reports which dimensions are dynamic and refuses to show a partial product, because
a subset with a confident count is a guess. Identical resulting combinations are not merged, matching the
implementations. The product of the dimension counts is computed arithmetically first: above 4,096
combinations the expansion is refused with `MTX001` rather than materialised, so a hostile file cannot
exhaust memory before the cap is applied. `strategy.fail-fast` and `strategy.max-parallel` decide
cancellation and scheduling once a run is under way; they are **not modelled** and produce `MTX005` so
the report does not look complete while silently ignoring them.

**`if:` evaluation.** Real three-state Kleene logic over a documented subset: the operators, the
function set (`contains`, `startsWith`, `endsWith`, `format`, `join`, `toJSON`, `fromJSON`, `case`, the
status functions), string/number/boolean/null coercion, case-insensitive string comparison, `.*` and `[*]`
wildcard indexing, and GitHub's rule that a condition with no status function is evaluated as
`success() && (condition)`. Excluded on purpose: `hashFiles()` (needs the checkout, so always unknown
for its own reason, regardless of its arguments) and wildcard results that depend on values only a run
produces.

**Positioning.** Every finding points at a line and column, and it is the **value node's** start position
(a field's position is where its value begins, not its key). Expression tokens inside an `if:` are offset
onto that value's position in the file, so every node of the `unknown` cause trace carries a real
location. Findings themselves still report the whole `if:` value rather than the offending sub-expression's
column. Three further compromises:
- content pulled in by a YAML merge key (`<<: *anchor`) is positioned at the **anchor definition**, not
  the use site — `SourceMap` marks those fields as inherited so the UI can say so instead of looking wrong;
- after the first line of a multi-line value, the YAML model does not record the value's own indentation,
  so those columns are the expression's own rather than the file's;
- findings inside a block scalar (`run: |`) are line-exact at column 1, because the YAML model records the
  scalar's header rather than columns of its body.

**Trigger filters.** `branches`/`tags`/`paths` and their `-ignore` counterparts, the `!`-negation ordering
rules, `pull_request` filtering on the base branch, the push rule that declaring only tag filters excludes
branch events, and the AND of `branches` with `paths`. Answers forced to `unknown` because they cannot be
decided from a file: no changed-files list in the payload; a push over 1000 commits or a diff that timed
out (GitHub then **always** runs); over 3000 changed files where matches fall outside the returned set;
anything depending on `schedule` (UTC clock) or a human pressing the run button. Whether pattern matching on
branch and path names is case-sensitive is **not documented**; ActionLens picks the conservative reading and
records it in [expression-semantics.md](expression-semantics.md).

**Runner labels.** `runs-on` accepts arbitrary user-defined labels and the GitHub-hosted label set changes
with every image addition, so AL007 is a *typo* check (a label close to a documented one, e.g.
`ubuntu-latests`) and stays silent on merely-unknown labels. `group:`/`labels:` routing and larger-runner
names assigned from the runner name are not validated. Expressions in `runs-on` are reported as unknowable.

**Lint.** Eight rules (AL001–AL008), not actionlint's full set, and no attempt at alignment with it.
AL004 and AL006 promote the engine's own findings rather than re-detecting, so each problem is reported
once.

## Verified behaviour I will not claim

- The engine is pinned by GitHub's own published expression cases
  (`tests/golden/languageservices/`, MIT-licensed, pinned commit — see its README). Of 1003 cases, **10 are
  excluded by rule**, not deleted: 6 expect a parse-time rejection of an unrecognised named value where
  ActionLens answers unknown, 2 expect an unknown-function rejection, 1 an empty expression body, 1 a runner
  memory limit. Each exclusion is listed in the test file with its reason.
- Where GitHub's documentation contradicts itself, ActionLens answers `unknown` and says so — the live case
  is `success()` after an ancestor that was **skipped** (the `needs` page implies skip propagates, the
  required-status-checks page says a skipped job reports success). This shows up in the UI as several
  `unknown` jobs under a "skipped" scenario where a "failed" scenario gives definite answers.
- Component behaviour has two layers of automated coverage, and the split matters. `ActionLens.Ui.Tests`
  (bUnit) renders `TopBar`, `GraphCanvas`, `InspectorPanel`, `SummaryPanel`, `CodeEditor` and
  `StatusStrip` through the **JS-degraded** `StubEditorModule` — it covers state binding and the three-state
  encoding, but not real CodeMirror or real JS interop. The real-browser assertions (boot without console
  errors, all assets 200, same-origin resource hosts, character order / caret / undo under CodeMirror,
  loading an example replacing the document, diagnostic jump, and repeated swaps leaving no orphan
  editor) are the Playwright suite in `tests/e2e/`. Do not read a green bUnit suite as coverage of the
  JS path, or a green engine suite as coverage of the UI. These browser assertions were only able to
  find the echo race once they typed at the *start* of the document; a test that types at the end
  passes even when the bug is present, so UI coverage is about where it looks, not just that it runs.

## Not built yet

DAG interaction beyond selection, the matrix table, URL sharing, autosave to `localStorage`, language
switching and theme switching. Language and Theme exist as **disabled** controls rather than fake ones;
URL sharing and autosave have no control at all — they are design intentions, recorded in the "Deferred"
table of [ADR 0003](adr/0003-local-first-no-backend.md), not shipped features. The centre canvas draws
**jobs, not job instances** and says so on screen; the instance count is reported separately.

The public repository, the Pages deployment and any published package do not exist. `.github/workflows/gate2.yml`
is the local CI definition; it runs once the repository is opened, and Gate 2 (`CHANGELOG.md`) must be green
before that happens.
