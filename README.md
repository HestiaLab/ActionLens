# ActionLens

> Static preflight for GitHub Actions — see what your workflow will do before it runs.

ActionLens takes a workflow YAML, a trigger event payload and a scenario (success / failure) and
answers three questions: **what will happen on this trigger, why, and what is risky about it**.

It **explains, it does not execute**. No step is run, no container is started, no GitHub API is
called, nothing is uploaded, there is no backend and no AI. Everything happens in your browser.

## Why

`actionlint` and YAML schema validators tell you whether a workflow is *well-formed*. They do not
tell you what a specific push to `release/**` with that payload will actually *do*: which jobs are
skipped, which matrix expands to 48 runners, which `${{ }}` can only be resolved at runtime, and
which `if:` silently depends on a `secrets.` value that is never available there.

ActionLens is a dry-run *explanation* layer. It resolves what it can statically, and where it cannot,
it says **unknown** and tells you exactly which context, which value and which sub-expression forced
that answer — instead of guessing.

## Quick start

```bash
git clone https://github.com/HestiaLab/ActionLens.git
cd ActionLens
npm install
npm run build:editor
dotnet run --project src/ActionLens.App   # Blazor WebAssembly, served locally
```

Work lands on the `dev` integration branch through pull requests — `dev` is protected, so branch from
it and open a PR rather than pushing directly. `main` is the stable branch, reached from `dev` through
its own reviewed PR. Both are wired to the Gate 2 workflow on push (which first went green in CI on
`main` at `2363021`). The Pages demo is not deployed yet — see `CHANGELOG.md` for the Gate 2 criteria
and the release conditions.

## What it does today

| Area | Status |
|---|---|
| YAML load with line/column diagnostics, anchors, `on` key handling | implemented |
| GitHub Actions expression lexer / parser | implemented |
| Three-state (true / false / unknown) evaluation with unknown cause trace | implemented |
| Matrix expansion with `include` / `exclude` and combination cap | implemented |
| Job DAG, cycle detection, layered layout, skip propagation | implemented |
| Scenario engine (success / failure / skipped) | see `docs/limitations.md` |
| `on.*` trigger filters | see `docs/limitations.md` |
| Lint rules AL001–AL008 | see `docs/limitations.md` |
| Blazor UI, URL sharing, i18n | see roadmap in `ARCHITECTURE.md` |

## Limitations

ActionLens deliberately refuses to model the parts of Actions that need a runtime. Dynamic matrix
values, reusable workflow expansion, composite action internals, step `outputs` propagation and
anything reading `steps.*` or `secrets.*` stay **unknown** by design. See
[`docs/limitations.md`](docs/limitations.md) for the full list and the reasoning.

## Documentation

- [`ARCHITECTURE.md`](ARCHITECTURE.md) — layers, data flow, three-state semantics, supported subset
- [`docs/adr/`](docs/adr) — accepted decisions
- [`docs/limitations.md`](docs/limitations.md) — what is not modelled, and why
- [`CONTRIBUTING.md`](CONTRIBUTING.md) · [`SECURITY.md`](SECURITY.md) · [`CHANGELOG.md`](CHANGELOG.md)

## Security

No network at runtime, no telemetry, no credentials. Workflow text and event payloads stay in your
browser's memory; there is no autosave and no URL sharing yet — both are listed in
[`docs/limitations.md`](docs/limitations.md) and in the "Deferred" table of
[`ADR 0003`](docs/adr/0003-local-first-no-backend.md). See [`SECURITY.md`](SECURITY.md).

## License

Apache-2.0 © HestiaLab.

---

## 中文说明

**ActionLens 是 GitHub Actions 的静态预检器**：输入 workflow YAML、触发事件负载和场景（成功 /
失败），输出「这次触发会发生什么、为什么、有什么风险」。

核心承诺是**解释而非执行**：不运行任何 step、不启动容器、不连接 GitHub、无后端、无 AI，全部在
浏览器本地完成。

与 `actionlint` 这类语法校验工具的区别在于：语法校验回答“这个 workflow 是否合法”，ActionLens
回答“这次具体触发实际会走到哪里”。它能算出来的部分静态算出来；算不出来的部分明确标记
**unknown**，并给出原因轨迹（用了哪个 context、缺哪个值、哪个子表达式导致未知），而不是猜测。

用法：

```bash
dotnet run --project src/ActionLens.App
```

当前实现范围与限制见上方表格与 [`docs/limitations.md`](docs/limitations.md)。技术文档以英文为主，
界面当前**全英文**且语言切换尚未实现；早期版本这里写「界面默认中文、可切换英文」，与代码不符，已更正。
`ActionLens.Ui` 是 Razor 类库，UI 组件测试由 `ActionLens.Ui.Tests`（bUnit）承担；真实浏览器的自动化
验收属 Gate 2 范围，见 [`ARCHITECTURE.md`](ARCHITECTURE.md) 的 Roadmap。
