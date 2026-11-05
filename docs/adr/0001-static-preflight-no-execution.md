# ADR 0001 — Static preflight, never execution

Status: accepted · Date: 2026-11-04

## Context

The obvious way to answer "what will this workflow do?" is to run it — containerise the job,
evaluate steps for real, collect outputs. That is what CI emulators do, and it is what makes them
slow, host-dependent, security-sensitive and unavailable in a browser.

ActionLens is positioned as a *reading* tool: it runs on GitHub Pages as pure WebAssembly, it must
start with no credentials, and it must be safe to point at an untrusted workflow copy-pasted from
the internet.

## Decision

ActionLens only interprets the workflow definition. Concretely, it never:

- executes a `run` script or a `uses` action;
- starts a container or touches a filesystem beyond the browser sandbox;
- calls the GitHub API, authenticates, or uploads any content;
- resolves `hashFiles()` against real files, or evaluates `env` by running a shell.

Every value that would only exist after something ran stays **unknown** and is reported as such with
a cause trace (see ADR 0002).

## Consequences

- The tool is instant, offline, and safe on hostile input — a workflow that does
  `curl | sh` is analysed, not obeyed.
- Some answers are structurally unreachable. This is a feature, not a bug: a UI that shows
  "unknown: `steps.build.outputs.version` requires a run" teaches the reader something about their
  workflow, whereas a simulated value teaches them nothing and can be wrong.
- Expressions still need a real parser and evaluator (we must reason *about* them), but the
  evaluator has no side effects and no dynamic code path. `eval`-style shortcuts are prohibited.
- Coverage of Actions is a subset by construction; the subset must be documented explicitly in
  `docs/limitations.md` rather than left implicit.
