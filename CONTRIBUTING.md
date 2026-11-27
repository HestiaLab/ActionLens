# Contributing

ActionLens explains what a GitHub Actions workflow will do, without executing it. Most bugs here are not
crashes; they are confident wrong answers. Keep that distinction in mind while reading this.

## Branches and CI

**`dev` is a protected integration branch** — do not push to it directly. Branch from `dev`, push the
topic branch, and open a pull request with `dev` as its base; a maintainer merges it. `main` is the
stable branch, reached from `dev` through its own reviewed pull request.

`.github/workflows/gate2.yml` runs on pushes to `dev` and `main` and on every pull request, so a PR
is checked before merge and the merge commit is checked again. Both jobs — build/format/xUnit v3 and
Playwright against a real Chromium — must be green before a PR is merged. See `CHANGELOG.md` for the
full Gate 2 definition; a green local run is not a substitute for a green CI run.

## Setup

- .NET 10 SDK (`net10.0` throughout).
- Node.js 20 or later (`package.json` declares `>=20`; CI uses 22) — the CodeMirror editor is bundled from
  TypeScript and the Playwright browser suite runs on Node. A failed bundle rebuild fails the build, so run
  `npm ci` (or `npm install`) and `npm run build:editor` before expecting the editor to appear. The
  generated bundle under `wwwroot/js/dist/` is **not** committed; `.gitignore` excludes it.
- `dotnet build ActionLens.sln` should end at 0 errors / 0 warnings. `ActionLens.Core` does not reference
  Blazor; keep it that way.

## Running tests

`dotnet test` does not work in this repository: the suites use xUnit v3 on Microsoft.Testing.Platform. Run
the built executables instead, from the repository root:

```bash
DOTNET_CLI_UI_LANGUAGE=en ./tests/<project>/bin/Debug/net10.0/<project>.exe
```

`DOTNET_CLI_UI_LANGUAGE=en` matters: without it the console output is written in a code page that arrives
as mojibake through a Unix shell, and failing test names become unreadable — which is how a real defect
here got misdiagnosed once. To exclude the large expression corpus while iterating:

```bash
… <assembly>.exe --filter "FullyQualifiedName!~GithubExpressionCorpusTests"
```

The four gates that must all hold before a change is done (all four are also in
`.github/workflows/gate2.yml`, together with the Playwright suite — see `CHANGELOG.md` for the full
Gate 2 definition):

1. `dotnet build ActionLens.sln -v q --nologo` → 0 errors, 0 warnings.
2. `dotnet format ActionLens.sln --verify-no-changes` → exit code 0 (no output).
3. Every test assembly green, including `ContractRegressionTests` and `DocFactsTests`.
4. `npm run test:e2e` green, **and** for anything user-visible, load the app in a browser. Serve it on a
   fresh port (`dotnet run --project src/ActionLens.App --urls http://localhost:<port>`) and confirm the
   page boots without a console exception and that every referenced asset returns 200. This is a gate,
   not advice: every UI defect found so far — a boot crash from an empty resource glob, three `<select>`s
   that changed the DOM but not app state, a summary that said "default" for a modified scenario — passed
   gates 1–3. The automated Playwright cases cover those four shapes; the manual pass remains a spot check
   for anything a new change makes visible that no assertion names yet.
   Notes on the method are in [docs/w4-verification.md](docs/w4-verification.md).

## What not to do

**Do not make a test pass by weakening it.** If a test and the implementation disagree, decide which one is
wrong from the source of truth, and if it is the test, say so in a comment next to the corrected assertion.
Blanket-relaxing assertions, deleting cases, or growing the expression corpus's exclusion list
(`DeferredFunctions` and friends in `GithubExpressionCorpusTests`) to get to green is not acceptable — the
current exclusions are 10 of 1003 cases, each justified in a comment, and a guard with no self-test is not
evidence.

Do not disable an analyzer rule or add a `NoWarn` to silence a failure. `Directory.Build.props` currently
suppresses exactly three (`CA1707`, `CA1720`, `CS1591`) and `.editorconfig` sets one more rule
(`CA1863`) to `none`; each is scoped and documented. Everything else in `latest-Recommended` stays an
error.

Do not present a guess as a result. A value that only exists after something ran stays `unknown`, with a
reason that names the missing input. The same applies to matrix expansion: an expression-driven dimension
yields no combinations rather than a partial product with a confident count.

Do not add network access, telemetry, a backend, or AI. See [SECURITY.md](SECURITY.md).

## Where semantics come from

[docs/expression-semantics.md](docs/expression-semantics.md) records every implemented rule with its
source: GitHub documentation, the C# evaluator in `actions/runner`, the TypeScript reference in
`actions/languageservices`, and GitHub's own published test cases. When sources disagree, the file says so
and says which one ActionLens follows. When nothing settles a question, ActionLens answers `unknown` and the
ambiguity is listed there rather than being resolved silently.

If you change a rule, change the file. An undocumented behaviour is a behaviour nobody can verify.

## Layout

`ActionLens.Core` — YAML reading, expression lexing/parsing/evaluation, matrix, job graph, scenarios,
triggers, lint, reporting. `ActionLens.Ui` — Razor class library: components, presentation services,
`AnalysisSession`. `ActionLens.App` — the WebAssembly host: entry point, `index.html`, the editor
bundle and interop. The local-first guard scans the host, the UI library and `scripts/`, so adding a UI
project means adding it to that guard's scope.

## Fixtures

Workflows under `samples/workflows/` are pinned, not curated. Real files are copied **verbatim** (a header
comment above the YAML is the only permitted addition, never inside a `run: |` block), with the source URL
and commit SHA they were taken at, so the input cannot drift silently. Synthetic files are labelled as such
in the header. When a test expects a number — combination counts, job instances, findings per rule — derive
it by hand from the YAML and write it as a literal with the arithmetic in a comment. Expectations computed
from the code under test prove nothing.

## Reading order

[ARCHITECTURE.md](ARCHITECTURE.md) → [docs/limitations.md](docs/limitations.md) →
[docs/adr/](docs/adr/) → [docs/expression-semantics.md](docs/expression-semantics.md).
