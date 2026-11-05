# ADR 0004 — Consume Atlas.Blazor 0.4.0 as a NuGet package

Status: accepted · Date: 2026-11-04

## Context

ActionLens needs a workspace-class UI: resizable and dockable panels, tabs, an editor surface,
theming, and keyboard-navigable focus. Building that well is a project in itself and is not the
product. Atlas already ships exactly this as `Atlas.Blazor`, and the same team maintains it.

The two ways to get Atlas into the app that are *not* acceptable are vendoring its source into this
repository and depending on its unreleased `HEAD`, because both make ActionLens' build a function of
someone else's working tree.

## Decision

`src/ActionLens.App` consumes `Atlas.Blazor` **0.4.0** through a `PackageReference`, and only 0.4.0.

- No source is copied into this repository, and the Atlas checkout is read-only reference material.
- Features present only on Atlas `HEAD` (floating windows, Schema V3) are not used. If one becomes
  necessary, it waits for a release or reopens this ADR.
- Atlas bugs found from ActionLens are filed upstream and recorded in
  `docs/limitations.md`. They are not worked around by forking or by patching a copy locally.
- The API surface is taken from `samples/Atlas.Showcase`, `docs/zh/SRS.md` and `docs/zh/theming.md`
  in the Atlas repository — not from memory or from inference.
- `ActionLens.Core` must not gain any Blazor or JS-interop dependency in order to serve the UI. The
  analysis engine is usable, and tested, without the app.

## Consequences

- Panel, tab, theme and focus behaviour is Atlas' responsibility, which keeps ActionLens' UI code
  about workflow analysis.
- Version bumps are explicit and reviewable; a regression in Atlas shows up as a pinned-version
  change rather than a silent drift.
- The 0.4.0 API is the ceiling of what the UI may do until a new Atlas release lands, so some
  layout ideas will have to wait.
- The analysis core stays portable: it can be reused from a CLI or a test harness without pulling
  the web stack with it.
