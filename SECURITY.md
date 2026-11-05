# Security

## The property that matters

ActionLens reads **untrusted input**: a workflow pasted from the internet, possibly crafted to make a
parser work too hard or to smuggle a payload. It is not a sandbox for GitHub Actions — it never runs a
`run:` script, never starts a container, never resolves `hashFiles()` against your files, and never
evaluates expressions as code. A workflow containing `curl … | sh` is *analysed*, not obeyed
([ADR 0001](docs/adr/0001-static-preflight-no-execution.md)).

Nothing leaves the browser. There is no backend, no telemetry, no analytics, no CDN font, script or
stylesheet, and no GitHub API call ([ADR 0003](docs/adr/0003-local-first-no-backend.md)). Sharing and
autosave are not implemented; if they are built, they will need this ADR reopened first (see its
"Deferred" table). At present workflow text and event payloads live only in memory.

The local-first promise is enforced rather than asserted: `LocalFirstGuardTests` is a test in
`ActionLens.Core.Tests` (not a build step) that fails when the UI sources, the Node scripts or a
stylesheet construct a network client or reference an asset on another host. It matches code, not prose,
and both halves of it have their own self-test so a scan that silently matches nothing cannot pass.
The browser-level assertion is a Gate 2 Playwright case, still outstanding; the source guard alone
cannot prove that no request left the page.

## Hostile-input bounds

Exceeding a bound produces a positioned diagnostic, never an exception:

| Bound | Value | Diagnostic |
|---|---|---|
| Workflow size | 1 MB | `YAML006` |
| Event payload size | 1 MB | `YAML006` (Scenario stage) |
| Anchor alias references | 128 | `YAML004` |
| Expanded YAML nodes (aliases followed) | 200,000, max depth 64 | `YAML009` |
| Nesting depth (token pass) | 64 | `YAML005` |
| YAML tokens | 200,000 | `YAML006` |
| Documents per file | 1 | `YAML002` |
| Expression body length | 21,000 | `EXPR002` |
| Expression parse depth | 50 | `EXPR002` |
| Matrix combinations | 256 (truncated) | `MTX001` |
| Matrix combinations materialised | 4,096 (refused above) | `MTX001` |

The alias-reference and depth limits are checked on the raw event stream **before** an object graph is
built. A separate bounded walk of the loaded graph then counts every node visit with aliases followed —
counting distinct node objects would miss a bomb entirely, because aliases share one object — and rejects
the file with `YAML009` before any recursive model walker sees it. The matrix product is likewise computed
arithmetically and refused before a single combination object is allocated; the 256 ceiling is applied
after `exclude`/`include` to the combinations that remain. Duplicate mapping keys are collected in the
token pass, so all of them are reported rather than only the first one a loader trips over.

Length and depth limits on expressions are not published by GitHub; they are taken from GitHub's own
reference implementations, which agree, because a workflow GitHub would refuse must not be analysed as if
it were fine.

## Secret material

ActionLens does not handle secrets, but you may paste them: a workflow's `secrets.MY_TOKEN` is a *name*,
and names are not sensitive. The risk is your own pasted text — event payloads and workflow bodies stay
in your browser's memory only; there is no persistence yet. Closing the tab clears them. Do not paste a
real secret value into the event payload editor to see what a condition would do; it is not needed,
because `secrets.*` is unknown by design and unavailable in `if:` regardless (rule AL004).

## Known limitations of the security posture

- **Static analysis is not a sandbox.** The engine is ordinary managed code: a crafted input that causes
  an unhandled exception or excessive memory is a bug to report, and the bounds above are the mitigation.
  One such bug was found and fixed during development — a resource-embedding glob that silently matched
  nothing and crashed the app on boot. That class is only visible in a browser, which is why a UI change is
  not considered verified until it has been loaded in one (see
  [w4-verification.md](docs/w4-verification.md)).
- **A wrong answer is a security problem here.** A tool that reports "this job will not run" when GitHub
  would run it teaches people to trust the wrong thing. Findings that cannot be established are therefore
  reported as `unknown` with a reason, and the product promise is that `unknown` is never collapsed into a
  definite result.
- **Third-party code is vendored as a package, not as source.** `Atlas.Blazor` is pinned to the published
  `0.4.0` ([ADR 0004](docs/adr/0004-consume-atlas-blazor-0-4-0.md)); the project does not track that library's
  development branch. GitHub's expression test data is vendored under its MIT licence with attribution in
  `tests/golden/languageservices/`.

## Reporting

Report vulnerabilities privately through the repository's security advisories
(`HestiaLab/ActionLens` → Security). Do not open a public issue for a reportable problem. Expect acknowledgement within five working days; fixes ship in a patch release with a
`CHANGELOG.md` entry that describes the impact without disclosing the exploit path.

Anything that would make the tool *mislead* rather than crash — a silent `unknown` turned into a definite
answer, a finding pointing at the wrong line, a network request the guard failed to catch — counts as a
security-relevant defect here, because misleading the reader is the failure mode this product exists to
avoid.
