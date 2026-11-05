# W4 验收记录（浏览器取证）

Gate 1 的结论在 [gate1-report.md](gate1-report.md)。这份文件只记录**只有真实浏览器才能给出的证据**，
以及由此发现并修复的缺陷。原因很直接：本轮所有产品级缺陷都发生在「测试全绿」的前提下，
构建门、测试门、格式门无一能拦住它们。

> **状态说明（2026-11-05）**：本文是 W4 时点的历史记录，其中的测试总数、源文件数和「将来时」描述
> 均不再代表当前状态。当前状态见 `ARCHITECTURE.md`、`CHANGELOG.md` 的 Gate 2 定义，以及
> `tests/e2e/` 下已落地的 Playwright 套件。下面的数字保留为过程证据，不作为现状断言。

## 环境

`dotnet run --project src/ActionLens.App --urls http://localhost:<port>`，Chrome DevTools 驱动。
每次改动后换新端口启动，避免旧进程仍占端口导致测到过期构建（本轮真实踩过：git-bash 无 `pkill`，
旧 dev server 占着 5199，探针读到的是上一版行为）。

## A1 点击诊断 → 编辑器定位：**通过**

| 证据 | 值 |
|---|---|
| 被点击的发现 | `AL005 info 39:3`（job `build` 缺 `timeout-minutes`） |
| 点击前活动行 | 文件头注释（第 1 行） |
| 点击后活动行 | `build:` |
| `grep -n "^  build:" samples/workflows/atlas-ci.yml` | **39:  `  build:`** —— 用源文件独立核对，不采信 UI 自述 |
| 焦点 | 移入编辑器（`activeElement = DIV.cm-content`，`.cm-editor.cm-focused` 为真） |

结论：fixture 行号 → 引擎 `SourceRange` → 编辑器跳转三者闭合，字段级定位可用。
Gate 1 §6.4 记的限制仍然成立，但范围更精确：**只有表达式子项**定位不到列，字段级没问题。

## A2 场景切换是否重算：**通过**（修完两处绑定之后）

在 `matrix-build.yml`（prettier 真实文件，5 处 `needs: [build]`，18 个作业实例）上读取 Run summary：

| 场景 | 三态计数 |
|---|---|
| 默认（每作业成功） | `+5 will run · −1 will not · ?0 unknown` |
| `build is skipped` | `+0 will run · −2 will not · ?4 unknown` |
| `build fails` | `+1 will run · −5 will not · ?0 unknown` |

两行的**对比**是关键证据：祖先**失败**时文档是明确的，于是 unknown 归零、下游确定跳过；祖先被**跳过**时
文档自相矛盾（见 `expression-semantics.md` 未决项 #1），于是 4 个下游变成 unknown。这说明三态语义真的跑在
界面上，而不是写在文档里。任何时刻都没有下游静默变成「会运行」——这正是 A2 的判据。

`INSTANCES 18` 与 fixture 头部手算的 `build 1 + lint 1 + test 11 + browser 3 + cli 1 + preview 1 = 18`
一致，用于确认示例下拉真的生效。

## A4 零外网：**源码层守卫已自动化，运行期取证已由 Gate 2 补上**

`LocalFirstGuardTests`：扫描 `src/ActionLens.App`、`src/ActionLens.Ui` 和 `scripts/` 下的
`*.cs`/`*.razor`/`*.ts`/`*.js`/`*.mjs`/`*.css`/`*.html`，匹配网络 API 的**构造点**
（`HttpClient` 及其工厂/handler、`WebSocket`、`fetch(`、`XMLHttpRequest`、`sendBeacon(`、
`new EventSource`、`importScripts(`、指向外部主机的动态 `import(`），并禁止所有页面与样式表引用外部
主机资源（`_framework` 模板占位除外）。W4 时点它只扫描 App 的 24 个源文件；范围与数量均已扩展。

两点防伪绿设计：
- **匹配前剥离注释**。`Program.cs` 里恰好有一句 "There is deliberately no HttpClient"，不剥离就会误报；
  注释不可能发请求，匹配注释只会训练人去不信任这条守卫。
- **守卫自身有测试**。`GuardIgnoresProseButStillCatchesCode` 断言散文被忽略、代码里的
  `new HttpClient();` / `await fetch('/api')` / `new WebSocket(url)` / `IHttpClientFactory` / 外部
  `import()` 必须被抓到；`AssetGuardCatchesExternalHostsButNotLocalOnes` 对外部资产正则做同样的
  自测（双引号、单引号、无引号属性与 CSS `@import` 必须抓到，`_content/`、`_framework/`、`data:` URI
  必须放过）。一个匹配不到任何东西的扫描会伪装成通过的安全测试。

帮助链接（`https://docs.github.com/...`）是数据不是请求，因此不受影响。禁止外部资源比 SRI 更严格：
本项目直接不允许外部 `src`/`href`。

W4 时点**未做**的运行期自动断言，现在由 `tests/e2e/boot-and-network.spec.ts` 承担：它在真实 Chromium
里读取 `performance.getEntriesByType('resource')`，断言主机集合恰好等于 `location.host`，并断言没有
控制台错误、没有 >= 400 的响应。当时我人工看过一次网络面板：219 条请求全部来自 `localhost`，
无一外部主机——那是人工证据，现在已有机器断言。

## 本轮发现并修复的缺陷

| 缺陷 | 根因 | 判据来源 |
|---|---|---|
| 应用启动即崩，页面打不开 | `ActionLens.App.csproj` 嵌入资源 glob 写成 `..\samples\...`（应为 `..\..\samples\...`）；通配符匹配为空时 MSBuild **不报错**，于是 fixture 一个都没嵌入，`Workflows[0]` 越界 | 控制台 `WebAssemblyRenderer` 未处理异常。构建 0/0、1377 测试、资产 200 全部无法发现 |
| 场景/示例/触发事件三个下拉**改了 DOM 不改状态** | 三个 `<select>` 都是非受控的：只挂 `@on:change` 并在 `<option>` 上手写 `selected=`。Blazor 不从 option 的 `selected` 读回值 | 可信输入（DevTools `fill`）复现，排除了「探针用合成事件」这一解释 |
| 摘要恒称 `default`，即使场景已被修改 | `WorkflowAnalyzer` 取 `Label = scenario.Name ?? "default"`，而顶栏从不命名场景 | 摘要显示 `default — build is skipped`，自相矛盾；修法是只渲染 `Description`，并把「非默认场景不得出现 default」列入待补断言 |

## 仍然打开的问题

1. `Atlas reported ContentNotRegistered.` 出现在状态区，未查。
2. 默认场景下 `matrix-build.yml` 有 **1 个作业 will not**：最可能是该作业自带 `if:` 依赖事件类型（负载为
   push），但**未验证**，不作为结论。
3. ~~AL004 的提升逻辑部分依赖引擎消息前缀（`'secrets' is not available`）匹配，消息一改就静默失效~~
   —— 已修复：`SecretsInConditionRule` 改为按 `finding.Symbol` 匹配，不读消息文本。
4. ~~系统性盲区：组件绑定/资源装配类缺陷只有浏览器能看见~~ —— 已部分闭合：`ActionLens.Ui` 已下沉为
   Razor 类库，`ActionLens.Ui.Tests` 有 24 个 bUnit 用例；真实 CodeMirror / interop / Atlas 宿主路径由
   `tests/e2e/` 的 Playwright 套件覆盖（启动、同源、输入与 undo、诊断跳转）。人工浏览器门仍保留为抽查。

## 交付门（每次改动后必须同时成立）

`dotnet build ActionLens.sln` 0 错误 0 警告 · `dotnet format --verify-no-changes` 无输出 ·
两个测试程序集全绿 · `npm run test:e2e` 全绿 · **以上四门由 `.github/workflows/gate2.yml` 承载**，
再加 `CHANGELOG.md` 里的 DocFacts / 契约回归判据合称 Gate 2。**UI 类改动必须另有浏览器证据，构建绿不构成验收。**
