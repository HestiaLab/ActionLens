# ADR 0002 — Three-state expression evaluation

Status: accepted · Date: 2026-11-04

## Context

GitHub evaluates `if:` against a live context: `steps.*`, `secrets.*`, `inputs.*` from an actual run.
A static analyzer sees a subset of those values. The tempting shortcut — substitute `null`, `""`, or
`false` for whatever is missing — produces confident wrong answers: a job reported as
`WillNotRun` when GitHub would in fact have run it.

Boolean-only logic cannot express "this depends on a value that does not exist yet", and that state
is a large part of what ActionLens has to say about real workflows.

## Decision

Evaluation is three-valued Kleene logic over `true | false | unknown`.

- `unknown && false` = `false` — a falsified conjunct short-circuits to a definite answer.
- `unknown && true` = `unknown`.
- `unknown || true` = `true` — a satisfied disjunct short-circuits.
- `unknown || false` = `unknown`.
- `!unknown` = `unknown`.
- Any comparison (`==`, `!=`, `<`, `<=`, `>`, `>=`) or arithmetic with an unknown operand is
  `unknown`; unknown propagates through functions, so `fromJSON(unknown)`, `format(unknown)` and
  `hashFiles()` are `unknown`.

Contexts have declared knowledge states rather than fake values: `github`, `env`, `vars`, `inputs`,
`matrix` are known or partially known; `needs` is supplied by the scenario engine; `secrets`,
`steps` and `job` are unknown; `runner` is derived from `runs-on` and partially known.

Every `unknown` carries a **cause trace**: the context that was consulted, the specific value that
was missing, and the sub-expression that forced the result unknown. An `unknown` without a reason is
a bug.

## Consequences

- Job states are `WillRun` / `WillNotRun` / `Unknown`, and the UI must never render `Unknown` as a
  checkbox tick or cross.
- Golden tests must assert the reason string, not only the value, otherwise a wrong-but-lucky
  `unknown` passes.
- Kleene logic diverges from GitHub's two-valued runtime: GitHub would evaluate the same expression
  against concrete values. ActionLens therefore reports "definitely skips" only when Kleene says
  `false`, which is sound — the set of jobs reported as running is an over-approximation, never an
  under-approximation.
- The coercion and comparison rules that decide `true` vs `false` come from the GitHub expression
  specification and the `actions/languageservices` reference implementation, not from convention in
  this codebase; each rule is pinned by a named test case.
