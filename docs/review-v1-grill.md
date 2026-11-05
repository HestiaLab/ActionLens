# ActionLens 代码与架构审查报告 v1（拷问版）

日期：2026-11-05 · 方法：`grill-me`（每个决策分支先给推荐答案，再留只有人能裁决的开放问题）
范围：`src/`（Core / Ui / App）、`tests/`、`samples/`、`scripts/`、全部文档与构建配置
原则：能从代码回答的自己查；每条结论标注证据等级；被实测推翻的假设明确写出，不藏起来。

> **执行状态（2026-11-05 更新）**：第 9 节六个决策已全部裁决，第 10 节三阶段已执行完毕。
> 修复后的实测基线见文末 **§12 修复后复测**。本文第 0 节的基线是**修复前**的历史快照，保留以便
> 对照；第 2–8 节描述的缺陷均已修复。报告本身不再新增开放问题。

证据等级：**【实测】** = 本次会话亲自跑出来的；**【核验】** = 逐行读源码确认；**【审阅】** = 三路只读探索的结论，带文件行号但未独立复现，需动手前再确认。

---

## 0. 实测基线（不看文档，自己跑）

环境：.NET SDK 10.0.403，解决方案 5 个项目（Core / Ui / App / Core.Tests / Ui.Tests）。

| 门 | 命令 | 实测结果 |
|---|---|---|
| 构建 | `dotnet build ActionLens.sln -v q --nologo` | ✅ 0 错误 / 0 警告 |
| 测试 | 两个 xUnit v3 / MTP 可执行文件 | ✅ Core **1381/1381**、Ui **24/24**，合计 **1405**，skipped 0 |
| 格式 | `dotnet format ActionLens.sln --verify-no-changes` | ❌ **退出码 2**，11 行：3 处 `IMPORTS`（using 顺序）、8 处 `IDE1006`（异步方法缺 `Async` 后缀） |
| `dotnet test` | 直接 `dotnet test ActionLens.sln` | ❌ MTP 与 VSTest 不兼容——但 `CONTRIBUTING.md:17` 已正确写了「不能用 dotnet test，要跑 exe」，属**预期行为**，不是缺陷 |

三个直接结论：

1. **文档里的测试数字全部过期**：`gate1-report.md:4` 的 1145、`w4-verification.md:4/66` 的 1377、`:82` 的 1380，实测是 **1405**。
2. **`CONTRIBUTING.md:32-42` 宣称的「四门全绿」，当前第 2 门是红的**；`gate1-report.md:119`、`w4-verification.md:81` 的「format 无输出」是历史快照。
3. **1405 全绿 ≠ 产品可信**。下面分支 A 会证明：8 个手工构造的输入里 5 个让引擎报 `INT001`（内部错误），而这些输入没有一个被任何测试覆盖。

---

## 1. 顶层决策分支（按依赖顺序）

| # | 分支 | 为什么在这个位置 |
|---|---|---|
| **A** | **可信度契约的强度**：`unknown` 不折叠、越界不抛异常、定位可追溯，是硬验收标准还是愿景 | 最基础。B/C/D/E/F 的验收标准都从它推导 |
| B | `ADR 0003` 的状态与 MVP 范围 | 决定「分享 / autosave」是补实现还是改文档 |
| C | UI 验收水位：Playwright / 真实 CodeMirror / 手工浏览器门 | 决定哪些缺陷算「已被防线覆盖」 |
| D | 文档同步机制 | 决定过期文档是人工修还是机器校验 |
| E | 发布与 CI（Gate 2 判据） | 决定前面几项什么时候算「可以开门」 |
| F | 结构债的处理时机 | 功能冻结前重构还是先修 P0 |

本报告把六个分支的证据与推荐答案都摆出来，但按 `grill-me` 的规矩，**只把分支 A 走到底，然后停下来问第一个问题**。

---

## 2. 分支 A：可信度契约（本轮走到底）

### A.1 我的推荐答案（先给）

**把契约当硬的，不降级文档。** 修复验收标准是「一条可复现的输入 → 一条正确的、带位置的诊断」，而不是「测试全绿」。

理由不是洁癖：这个产品唯一的差异化就是「算不出来就明说为什么算不出来」（`README.md:18-20`、`SECURITY.md:62-65`），而 `CONTRIBUTING.md:3-4` 自己写了「Most bugs here are not crashes; they are confident wrong answers」。把 `SECURITY.md:24` 的「never an exception」改成「best effort」，等于把预检器降格成语法校验器——那 `actionlint` 已经做了。

### A.2 契约当前被违反【实测】

在临时工程里直接引用 `ActionLens.Core.dll`，调用 `WorkflowAnalyzer.Analyze` / `ExpressionEvaluator`，8 个探针：

| # | 输入 | 契约期望 | 实测 |
|---|---|---|---|
| 1 | `if: join()` | arity 诊断 `EXPR004` | ❌ **`INT001`** `ArgumentOutOfRangeException` |
| 2 | `if: toJSON()` | 同上 | ❌ **`INT001`** |
| 3 | `if: fromJSON()` | 同上 | ❌ **`INT001`** |
| 4 | 只写 `branches-ignore:`，无 payload | `unknown` + 原因 | ❌ **`INT001`** `ArgumentOutOfRangeException` |
| 5 | `include:` 里同时写 `OS:` 与 `os:` | 诊断或归一 | ❌ **`INT001`** `ArgumentException: An item with the same key has already been added. Key: os` |
| 6 | `hashFiles(unknown)` | `RuntimeFunction` 原因 | ❌ 返回 `Propagated` 原因 |
| 7 | 嵌套 `unknown` 原因树的坐标与文案 | 精确指向子表达式 | ❌ 顶层 `6:9..6:43` 正确，**内层 causes 全为 `1:1`**（导出 JSON 中 12 处），且消息泄漏 C# record 内部表示 |
| 8 | `case(unknown, 'A', true, 'B', 'C')` | `unknown` | ✅ `unknown` —— **推翻了探索阶段的一条误判**，见 A.4 |

第 7 条的实测导出片段（用户可见的 JSON）：

```json
"message": "''MemberAccessNode { Range = 1:1, Target = ContextNode { Range = 1:1, Name = github }, Name = event }' . missing' cannot be decided yet.",
"range": "1:1"
```

内部 AST 的 `record.ToString()` 直接进了给读者看的原因文本。这与 `README.md:19-20`「告诉你哪个 context、哪个值、哪个子表达式」正好相反：它给的是调试器输出，不是子表达式位置。

### A.3 根因定位【核验】

| 缺陷 | 位置 | 说明 |
|---|---|---|
| 三函数空参越界 | `ExpressionEvaluator.cs:502 / 526 / 536` | `Join` / `ToJson` / `FromJson` 无 `arguments.Count < 1` 保护；同文件 `Contains:346`、`Format:424` 都有，属不一致 |
| arity 错误不中止 | `ExpressionBodyParser.cs:651-664` | 只加 `EXPR004` 诊断，AST 仍返回；`ExpressionParser.cs:40-41` 明确保留 `Root`；`ConditionEvaluator.cs:87` 照常求值 |
| 异常被谎报 | `WorkflowAnalyzer.cs:150-164` | catch-all 包成 `INT001`，把**用户的 arity 错误**呈现成「工具内部缺陷」 |
| 触发器空列表 | `TriggerEvaluator.cs:505-516` | `hasBranches` 把 `-ignore` 计入（`:465`），但 `values[0]` 未判空；同处 `:527` 有防护，`:505` 漏了 |
| include 大小写碰撞 | `MatrixExpander.cs:321-324` | `entry` 由 `WorkflowModelBuilder.cs:1244` 用 `Ordinal` 构造，此处用 `OrdinalIgnoreCase` 重建 → 抛 |
| `hashFiles` 原因错 | `ExpressionEvaluator.cs:342` | `name.ToLowerInvariant() is not ("hashFiles" or ...)`：小写结果永远匹配不上驼峰字面量，分支恒真；`:316` 的 switch 用的是全小写，是复制遗漏 |
| 坐标塌成 1:1 | `ExpressionLexer.cs:404` | `At()` 的 clamp 上界写成 `_lineStarts[^1]`（**最后一行起点**）而非 `text.Length`；单行表达式任一 index 都 clamp 到 0 |
| 原因树未重定位 | `ConditionEvaluator.cs:68-70` | 只 re-base 了 `diagnostics`，未 re-base `reason.Causes`；`EvaluationScope.cs:67-72` 的注释声称做了 |

### A.4 被推翻 / 降级的探索结论（重要，避免误修）

- ❌ **「`case()` 把 unknown 折叠成确定值，破坏 soundness」** —— `Case()`（`ExpressionEvaluator.cs:601-635`）内部逻辑确实有这个漏洞（`:621` 在未知谓词后遇 `true` 直接返回确定值），但 `EvaluateFunction:308` 的 `IsStrictlyRequired` 早退使任何含 unknown 参数的调用**根本到不了 `Case()`**。实测返回 `unknown`。结论：降为**潜在缺陷**，正确做法是加一条断言把早退锁住，而不是当线上 P0 改 `Case()`。
- ❌ **「报告里大量 `reason.range = 1:1`」** —— 顶层 reason 已正确 re-base 到 YAML 坐标（实测 `6:9..6:43`）。问题**只在嵌套 causes**，范围要收窄，别把整条定位链重写。
- ⚠️ **「SECURITY.md 的边界全是摆设」** —— 1 MB / 128 别名 / 64 深度 / 200k tokens / 21k 表达式长度 / 深度 50 这些常量确实存在且部分生效（`WorkflowLoader.cs:34`、`YamlSafetyScanner.cs:18-27`、`ExpressionParser.cs:92`）。真正不成立的是 `SECURITY.md:24` 的「never an exception」以及两处结构性问题【审阅】：`MatrixExpander.cs:161` 先构造完整笛卡尔积才在 `:172` 截断 256（10 维×10 值会先 OOM）；`YamlSafetyScanner` 只数 `AnchorAlias` 事件数、不数别名展开后的节点数，自引用别名只需 1 个事件即可绕过。这两条我**没有做破坏性实测**，列为待验证的高风险项。

### A.5 「1405 全绿」为什么没拦住这些【审阅 + 核验】

- `GithubExpressionCorpusTests.cs` 的 1003 例全是**纯表达式**，不含 `join()` 零参、`branches-ignore` 空 payload、include 大小写碰撞这类**编排层输入**；`FixturesTests.cs` 的三个 fixture 都是合法 workflow。
- 语料排除规则（10 例）是自由字符串数组（`GithubExpressionCorpusTests.cs:80,89,100,110`），**没有任何断言固定 1003 / 10 / 993**；`:38-41` 在语料目录缺失时返回空集合，用例数会静默缩水而测试仍绿。`CONTRIBUTING.md:48-51` 只有文字约定，无自动化护栏。
- `SECURITY.md:16` 称 `LocalFirstGuardTests`「fails the build」——它其实是 xUnit 测试，不是构建门；且扫描范围只有 `*.cs/*.razor/*.ts` 两个目录（29 个文件），不扫 `*.js` / `*.mjs` / `*.css`，`wwwroot/js/dist/` 被整体排除。

### A.6 分支 A 的开放问题

见第 9 节 **Q1**。这是整棵决策树的根，其他分支的验收标准都从它推导，所以本轮只问这一个。

---

## 3. 分支 B：`ADR 0003` 与 MVP 范围

**推荐答案：把 ADR 0003 改标为 `proposed / partially implemented`，把「已实现」的描述移到 roadmap；不要为了对齐 ADR 去补功能。**

证据【核验】：`docs/adr/0003-local-first-no-backend.md:3` 是 `Status: accepted`，但 `:19-24` 写的 `localStorage` 键 `actionlens:session:v1`、500 ms autosave、LZ-string 压缩进 URL fragment、8 KB 阈值、`:30-31`「automated test asserts that no request leaves the page」——全仓库 `src/` 无 `localStorage` / `LZString` / `fragment` / `session:v1`，`package.json:15-27` 无 `lz-string`，无 URL fragment 代码，无分享/导出代码。唯一的 debounce 是 `AnalysisSession.cs:46` 的 250 ms，且是**重新分析**而非 autosave。

这四份文件反而写对了现状：`CHANGELOG.md:57-58`、`docs/limitations.md:84-86`、`SECURITY.md:13-14`、`README.md:30,44`。**一个 accepted 的 ADR 与其余全部文档冲突**，是全仓最严重的文档失真。`w4-verification.md:59` 也明说运行期自动断言「未做」，只有人工看了一次网络面板。

同源问题：`TopBar.razor:49-52` 只有 Language / Theme 两个 disabled 按钮，`CHANGELOG.md:57` 却把「URL sharing / autosave / language / theme」四项并列称「controls exist but are disabled」——前两项连控件都没有。

---

## 4. 分支 C：UI 验收水位

**推荐答案：承认「当前只有降级路径的组件测试」，把 Playwright 从 ARCHITECTURE 的既成事实改为规划；但不要因为有了 bUnit 就取消浏览器门——反过来，浏览器门要保留并自动化。**

证据【核验 + 审阅】：

- ✅ `ActionLens.Ui` RCL 与 `ActionLens.Ui.Tests` **真实存在**，7 个文件 24 个 `[Fact]`，bUnit 2.13.3。但 `ComponentHarness.cs:14-21` 的 `StubEditorModule` 恒返回 null，全部 24 例跑在 **JS 降级模式**，真实 CodeMirror / 真实 interop / Atlas 宿主工作区零覆盖。
- ❌ 四份文档**否认**它们的存在：`CHANGELOG.md:59-61`、`docs/limitations.md:78-80`、`w4-verification.md:76-77`、`gate1-report.md:93-96`（「RCL 提取仍在进行中」「UI 行为仅手工浏览器验证」）。
- ❌ `ARCHITECTURE.md:126` 把 Playwright 写成既成测试方法；全仓库无 playwright 包 / 配置 / 测试，`package.json:10-14` 无 test 脚本，`w4-verification.md:60` 说它属 W7。
- ⚠️ 两条高影响 UI 缺陷【审阅，未复现】：
  - `CodeEditor.razor.cs:62-76` `SetParametersAsync` 没有记录「自己刚报出去的文本」，防抖重渲染会把自身上报值当外部值，`PushTextAsync:137` → `editor.ts:196-213` 整篇替换；与类注释 `:12-14` 承诺的方向性相反，会污染 undo/caret。
  - `editor.ts:177-181` `void dotNet.invokeMethodAsync(...)` 无 `.catch()`；`EditorModule.cs:86-103` `DisposeAsync` 只置空 `_module`、未重置 `_loading`，可向外发放已释放的 JS 引用，进而使 `editor.ts:285-292` 的 `dispose` 不执行 → `ResizeObserver` 泄漏。
- ⚠️ 可访问性做得扎实（三态靠形状+字符+文字，非纯颜色，`ReportPresentation.cs:11-16`）；但 `README.md:90`「界面语言默认中文、可切换英文」与实现**直接矛盾**：`src/` 无任何中文，`index.html:2` `lang="en"`，`ActionLens.App.csproj:13` 关闭全球化数据。

---

## 5. 分支 D：文档同步机制

**推荐答案：先做一次性「文档对账」，再决定要不要上机器校验；不建议现在引入 CI 里的文档链接检查器（分支 E 尚未落地）。**

过期/矛盾清单（全部【核验】）：

| 文档 | 声称 | 实测 |
|---|---|---|
| `w4-verification.md:4/66` vs `:82` | 1377 / 1380 | 同一文档两个数字；实测 1405 |
| `w4-verification.md:45` | 扫描 App 的 24 个源文件 | 实际 2 个项目 29 个文件 |
| `gate1-report.md:154` | 三个 fixture 端到端「仍未完成」 | `FixturesTests.cs` 28 例已跑通 |
| `gate1-report.md:90` | 表达式定位差一次偏移换算 | 根因是 `ExpressionLexer.cs:404` 的 clamp 上界错误，连绝对坐标都没算对 |
| `tests/golden/languageservices/README.md:29` | 28 个 JSON | **27 个**，缺 `join.json`；`docs/expression-semantics.md:89` 引用了这个不存在的文件 |
| `ARCHITECTURE.md:11-24` | 层次图 `App → Core` | 实际 `App → Ui → Core`，**缺 Ui 层** |
| `ARCHITECTURE.md:126` | Playwright 已用 | 不存在 |
| `README.md:44` | 「see roadmap in ARCHITECTURE.md」 | 该文件无 roadmap 章节 |
| `CHANGELOG.md:46-49` | 测试描述 | 完全未提 bUnit 24 例 |
| `CONTRIBUTING.md:53-55` | 「Two narrow exceptions」 | `Directory.Build.props:19` 有 3 项 `NoWarn`，`.editorconfig:71` 另有一处抑制 |
| `CONTRIBUTING.md:9` | Node.js 24 | `package.json:8` `>=20` |
| `docs/limitations.md:33-38` | 函数集不含 `case`、排除 `.*` 通配 | 两者都已实现（`gate1-report.md:133` 与代码一致） |

另外 `SECURITY.md:73-75` 让用户去 `HestiaLab/ActionLens` 提私密漏洞，`README.md:25` 给出 `git clone` 地址，而 `CHANGELOG.md:12-14` 说公开仓库尚未创建。

---

## 6. 分支 E：发布与 CI

**推荐答案：Gate 2 的判据里必须加「四门实测全绿 + 契约回归用例」，且在没有 CI 之前不要宣布 Gate 2 通过。**

证据【核验】：全仓库**无 `.github/`、无任何 CI 配置、无 Pages 部署**；三个 `.yml` 全是 `samples/workflows/` 下被分析的 fixture（`atlas-ci.yml` 是 Atlas 项目的 CI，不是本仓库的）。`CONTRIBUTING.md:32-42` 的四门全靠本地手工执行，而其中格式门当前是红的（第 0 节）。

`gate1-report.md:3,158` 已宣告 Gate 1 通过，但 `CHANGELOG.md:12-14` 的「公开仓库待 Gate 1 可信后创建」条件已满足却仍写「Nothing is released yet」——发布判据本身没定义。

---

## 7. 分支 F：结构债

**推荐答案：先修 P0，功能冻结后再做一次「收窄 public 面 + 抽重复实现」的重构；不要现在动 1423 行的 `WorkflowModelBuilder.cs`。**

证据【审阅】：

- 超长文件：`WorkflowModelBuilder.cs` 1423、`TriggerEvaluator.cs` 886、`ExpressionEvaluator.cs` 815、`ExpressionBodyParser.cs` 757、`AnalysisReportJson.cs` 557（手写序列化）。
- `WorkflowAnalyzer.cs:15` 自称 "The one entry point"，但约 55/60 个文件含 public 类型，`WorkflowLoader` / `ExpressionEvaluator` / `ScenarioEngine` / `MatrixExpander` / `JobGraph.Build` / `TriggerEvaluator` 全可独立实例化；`ScenarioEngine.Run`（`:141, :214`）用 `_jobs[jobId]` 直接索引，外部传入不一致的 graph 即 `KeyNotFoundException`。
- 重复实现：Levenshtein `Distance` 三份（`JobGraphAnalyzer.cs:354-381`、`RunnerLabelTypoRule.cs:126-153`、`WorkflowModelBuilder.cs:1394`）；JSON/YAML → `GitHubValue` 三份（`ExpressionEvaluator.cs:663-687`、`AnalysisInput.cs:296-307`、`WorkflowModelBuilder.cs:1327-1348`），而 `AnalysisInput.cs:276-277` 的注释声称「两者必须一致」——靠拷贝维持一致。
- 确定性【审阅】：`AnalysisReportJson.cs:354-358` 的 `run.values`、`WorkflowAnalyzer.cs:353` 的 `declaredEvents` 直接遍历 Dictionary 未排序，与同文件 `:311`、`:361-384` 特意排序的做法矛盾；`GitHubValue.cs:336-351` 的 `GetHashCode` 依赖插入顺序，违反 Equals 契约。**未实测跨进程字节差异**，列为风险而非结论。

---

## 8. 修复优先级（Q1 裁决：硬契约，以下为定案清单）

**P0 — 违反核心承诺，且已有可复现输入**

1. `ExpressionEvaluator.cs:502/526/536` 补空参保护；`ExpressionBodyParser.cs:651-664` 决定「arity 错误是否中止求值」，让 `EXPR004` 不再变成 `INT001`。
2. `TriggerEvaluator.cs:505-516` `values[0]` 判空。
3. `MatrixExpander.cs:321-324` 统一比较器 / 显式报诊断。
4. `ExpressionEvaluator.cs:342` 改为全小写集合；`hashFiles(unknown)` 应保留 `RuntimeFunction` 原因。
5. `ExpressionLexer.cs:404` clamp 上界改 `text.Length`；`ConditionEvaluator.cs` 递归 re-base 整棵 reason 树。
6. 消除原因消息里的 `record.ToString()` 泄漏，改为「路径 + 缺失值」的人类可读描述。
7. **为上述 7 条各加一条回归用例**（当前 1405 例一条都覆盖不到），并把 `join()` / `branches-ignore` / include 大小写碰撞加进 fixture。

**P1 — 安全边界与防线**

8. `MatrixExpander.cs:161` 在 `CrossProduct` 内 early cap 256；`YamlSafetyScanner` 统计别名展开后的节点数；`AnalysisInput.cs:105` 给 event payload 加字节上限（`WorkflowAnalyzer.cs:71` 目前在 try 之外）。
9. `GithubExpressionCorpusTests.cs` 加「1003 / 10 / 993」硬断言与目录存在断言；排除规则改为不可静默放宽。
10. `LocalFirstGuardTests` 扩到 `*.js/*.mjs/*.css`，给 `ExternalAsset` 加自测。
11. 恢复格式门：3 处 using 顺序 + 8 处 `Async` 后缀。

**P2 — 文档对账与工程化**

12. 一次性修第 5 节表格；`ADR 0003` 改状态；`ARCHITECTURE.md` 补 Ui 层与 roadmap；`README.md:90` 改中文说明。
13. `ActionLens.App.csproj:57-61` 的 esbuild `IgnoreExitCode + WarnAndContinue` 改成真失败（bundle 已存在但重建失败会静默沿用陈旧产物）；`.gitignore:19` 与 csproj 注释「committed bundle」冲突，干净克隆按 `README.md:24-28` 起不来（缺 `npm install` 步骤）。
14. UI 两条竞态/泄漏（分支 C）在浏览器里复现后修。

---

## 9. 决策记录（grill-me 六分支全部裁决，2026-11-05）

| 分支 | 裁决 | 直接后果 |
|---|---|---|
| **A 可信度契约** | **硬契约，修实现** | `SECURITY.md:24 / :62-65` 不改；7 处 P0 修复 + 每条补回归用例；Gate 2 判据加入「契约用例集」。不改文档降级 |
| **B ADR 0003** | **拆分 ADR，收窄到无后端** | 0003 保留 `accepted`，只覆盖「无后端 / 无对外请求 / 无遥测」（已被代码支撑）；autosave、LZ-string 分享、8 KB、自动网络断言移入 `deferred` 并标注未实现；同步 `README.md:63`、`SECURITY.md:13-14`、`CHANGELOG.md:57-58`。不新增 `lz-string` 依赖 |
| **C UI 验收** | **最小 Playwright 集 + 保留人工门** | 补四处自动化：启动无异常/资源 200、资源主机集合 == 同源、真实 CodeMirror 输入不丢 caret/undo、诊断跳转行号；`ARCHITECTURE.md:126` 改为待办直到落地；人工门降为抽查、不取消；bUnit 继续跑 stub 但明确标注覆盖边界 |
| **D 文档同步** | **一次性对账 + 事实固化为测试** | 修第 5 节 ~15 处；新增 `DocFactsTests`（语料文件数 / 1003 / 10 / 993、Markdown 相对链接、ADR 状态与 deferred 一致）；**文档不再手写测试总数**，从源头消灭漂移面。不引入独立文档 CI 门 |
| **E 发布与 CI** | **定义 Gate 2 并落成 CI，再开源** | Gate 2 = build 0/0 · format 0 · Core+Ui 测试全绿 · DocFacts · Playwright 四处 · 契约回归 7 条 · 三 fixture 端到端 · **P0 清零**；写 `.github/workflows/ci.yml`，本地跑通后才创建公开仓库与 Pages |
| **F 结构债** | **分类：契约相关提前，其余冻结后** | Gate 2 前收五项：`AnalysisReportJson.cs:354-358` / `WorkflowAnalyzer.cs:353` 未排序遍历、`GitHubValue.cs:336-351` `GetHashCode` 违约、`WorkflowModelBuilder.cs:216` 合并深度死预算、`MatrixExpander.cs:118` `_ = strategy` 静默丢弃、`AnalysisSession.cs:25-26` `DiagnosticJump.Token` 假实现。拆文件 / 收窄 public 面 / 去重三份实现等 Gate 2 绿灯后逐项动 |

**决策间的依赖**（拷问时按此顺序推进，不可倒过来）：A 定义验收强度 → B 决定 MVP 范围 → C 决定哪些缺陷算「已被防线覆盖」 → D 决定文档数字由谁担保 → A/C/D 各自产出的断言共同要求 E 提供 CI 载体 → F 依 E 的时间点切分两批。

**一致性检查**：Q1 选硬契约后，Q6 不能选「一律推迟」——那会让 `ARCHITECTURE.md:47-49` 的 byte-identical 承诺继续悬着两处未排序遍历；Q2 拆分 ADR 后，被移走的「自动网络断言」由 Q3 的「资源主机集合 == 同源」补上，没有留下无证据的隐私声明；Q5 的 CI 正是 Q1/Q3/Q4 三组断言的载体，不是独立新增项。六条裁决之间无「it depends」残留。

---

## 10. 据裁决重排的执行顺序

**阶段 1 — 契约回归（Gate 2 前置，最高优先级）**

1. 修 P0 七项（第 8 节 1–7），每项配一条可复现回归用例；把 `join()` 零参、`branches-ignore` 空 payload、include 大小写碰撞加进 fixture。
2. 移入 Q6 的五项契约相关结构债，同样带断言。
3. 恢复格式门（3 处 using 顺序 + 8 处 `Async` 后缀）——否则 Gate 2 第 2 门现在是红的。

**阶段 2 — 防线与证据**

4. `DocFactsTests`（Q4）；语料 1003/10/993 硬断言与目录存在断言；`LocalFirstGuardTests` 补 `*.js/*.mjs/*.css` 与 `ExternalAsset` 自测。
5. Playwright 最小集四处（Q3）；`ARCHITECTURE.md:126` 改待办。
6. P1 安全边界：`CrossProduct` early cap、别名展开计数、event payload 字节上限、`ActionLens.App.csproj:57-61` 构建门改真失败。

**阶段 3 — 文档与发布**

7. 第 5 节一次性对账；拆分 `ADR 0003`；`ARCHITECTURE.md` 补 Ui 层与 roadmap；`README.md:90` 中文说明改对；`CHANGELOG.md:12-14` 补 Gate 2 定义。
8. 写 `.github/workflows/ci.yml` 承载阶段 1–2 的全部断言，本地跑通整条链。
9. Gate 2 绿灯后创建公开仓库与 Pages；此后才排 Q6 的第二批结构重构。

**不在本轮范围**：按 ADR 补 autosave / URL 分享（Q2 已否决）、Gate 2 前集中重构超长文件（Q6 已否决）、先开 Pages 再补 CI（Q5 已否决）。

---

## 11. 复现方式

- 构建/测试/格式：`CONTRIBUTING.md` 的四门命令；测试程序集按该文件的说明直接跑 `.exe` 或
  `dotnet <assembly>.dll`（两种方式都已验证可用）。
- 契约用例：`tests/ActionLens.Core.Tests/ContractRegressionTests.cs`；语料护栏：
  `GithubExpressionCorpusTests.TheVendoredCorpusKeepsItsPinnedShape`；文档事实：`DocFactsTests`。
- 浏览器证据：`npm run test:e2e`（`tests/e2e/` 三个 spec 文件共 6 条用例，真实 Chromium）。
- 修复前的 P0 探针：临时工程 `C:\Users\World\AppData\Local\Temp\actionlens-review-90\`
  （`Probe.csproj` 引用 `ActionLens.Core.csproj`）。该目录在系统临时区，未改动仓库任何文件。

---

## 12. 修复后复测（2026-11-05）

| 门 | 命令 | 修复前 | 修复后 |
|---|---|---|---|
| 构建 | `dotnet build ActionLens.sln -v q --nologo` | 0/0 | ✅ **0 错误 / 0 警告** |
| 格式 | `dotnet format ActionLens.sln --verify-no-changes` | ❌ 退出码 2（11 行） | ✅ **退出码 0** |
| Core 测试 | `dotnet tests/ActionLens.Core.Tests/bin/Debug/net10.0/ActionLens.Core.Tests.dll` | 1381 | ✅ **1406 / 1406**（当时值） |
| Ui 测试 | `dotnet tests/ActionLens.Ui.Tests/bin/Debug/net10.0/ActionLens.Ui.Tests.dll` | 24 | ✅ **24 / 24**（当时值） |
| 浏览器 | `npm run test:e2e` | 不存在 | ✅ **6 / 6 通过**（当时值，真实 Chromium） |

> 本表是 2026-11-05 第一轮的复测快照。后续两轮又补了跨进程确定性、渲染次数与 interop
> 可恢复断言，**当前数字以 §15 为准**。

两处命名口径说明：Q5/§10 写的 `.github/workflows/ci.yml` 实际落地为
`.github/workflows/gate2.yml`（workflow 内部 name 也是 `gate2`），`ARCHITECTURE.md` 与 `CHANGELOG.md`
已按实际文件名对齐；决策表保留原措辞作为过程记录。§8 第 14 条的两条 UI 竞态已于本轮在浏览器
复现并修复，详见下文「补记」。

新增用例共 **25 条**（Core 1381 → 1406）：`ContractRegressionTests` 18（15 个 `[Fact]` + 3 个
`[InlineData]`，其中 1 条消费契约 fixture）、`DocFactsTests` 5、语料形状护栏 1、
`LocalFirstGuardTests` 的资产自测 1。
既有两个 fixture 测试随 `MTX005` 收紧了断言（由「无非 Lint 发现」改为「只允许 Lint 与 MTX005」，
并正断言 MTX005 存在），不是放宽。

**第 0 节的三个结论现在全部翻转**：文档数字不再手写（由 `DocFactsTests` 与语料护栏锁住）；格式门已绿；
第 2 节那 5 个 `INT001` 输入现在分别产出 `EXPR004` / `MTX003` / 正常的 `unknown` 判定，且各有一条
回归用例。第 9 节六个决策均已落地，按 Q5 的定义，**Gate 2 的本地判据已全部满足**；尚待的是把
`.github/workflows/gate2.yml` 在真实 CI 上跑一次——这一步本身需要公开仓库，属于「开门」动作，
按 Q5 的顺序留给人工执行，不在本轮伪造为已完成。

**本轮修改了源码、测试、文档与构建配置**；唯一未做的是创建公开仓库 / Pages 部署，以及 Q6 第二批的
超长文件重构（按设计等 Gate 2 绿灯后）。

---

## 13. 补记：分支 C 的三条 UI 缺陷（第 8 节第 14 条，2026-11-05 续）

上一轮复测时把第 8 节第 14 条漏在任务清单之外，本节记录其复现与修复。原清单标注的两条
【审阅，未复现】竞态**本轮已在真实 Chromium 中复现**，并额外发现第三条（13.2）。

### 13.1 编辑器回显竞态：`MARK` 变成 `KRAM`

在文档**开头**输入 `MARK`，实测得到 `KRAM`；caret 被弹回 0；一次 `Ctrl+Z` 抹掉全部 4 个字符，
之后 7 次 undo 毫无变化。上一轮的 `editor-typing` 用例在文档**末尾**输入，整篇替换恰好把 caret
放在输入位置，因此掩盖了缺陷。

根因：`CodeEditor.NotifyOwnerAsync` 不更新 `_text`（只有 textarea 降级的 `OnFallbackInput` 更新），
于是 `SetParametersAsync` 把父组件回显的自身上报值判为「外部新值」，每次击键都调用
`PushTextAsync` → `editor.ts:setText` 整篇 dispatch，把 caret 重置到 0、把历史压成一步。

修法（两层，A/B 验证过各自的作用）：
- `CodeEditor.razor.cs`：在 `NotifyOwnerAsync` 里同步 `_text = text`，并删掉 `OnFallbackInput`
  的重复赋值——让方向性判断与类注释一致，消除每字符一次无谓的 JS 往返。
- `ClientScripts/editor.ts`：`setText` 在 `doc.toString() === body` 时直接返回，不 dispatch。
  这是纵深防御：即使将来 .NET 侧再次误判，空替换也不会破坏 caret/undo。

**A/B 证据**（同一份 Playwright 用例）：只回退 .NET 那行，两条用例仍通过（JS 短路独自拦住）；
把 JS 短路也去掉，用例立刻失败并报 `character order was scrambled: KRAM#...`。
新的 `editor-typing.spec.ts` 在开头输入、跨过 250 ms 防抖、断言字符顺序 / caret=4 / undo 后文档完好。

### 13.2 第三条：切换示例后编辑器仍是旧文档

修 13.1 时新加的「加载示例仍然替换文档」用例失败。追踪 `SetParametersAsync` 的调用日志发现：
切换示例时它**根本没有被调用**——`ActionLensWorkspace` 只订阅 `_workspace.Changed`，七个面板各自
订阅 `Session.Changed`，但 shell 自己没有订阅，所以 `Text="@Session.WorkflowText"` 所在的
render fragment 不重新求值。结果：报告已变成新示例（6 jobs），编辑器仍显示旧全文——**旧文本配新报告**，
正是「误导读者」级别的缺陷。修法：`ActionLensWorkspace` 订阅并在 `DisposeAsync` 退订 `Session.Changed`。

### 13.3 JS interop 生命周期

`editor.ts:179` 的 `void dotNet.invokeMethodAsync(...)` 无 `.catch()`，`EditorModule.DisposeAsync`
只置空 `_module`、未重置 `_loading` / 未标记已释放，可能向外发放已释放的 JS 引用，使
`editor.ts:dispose` 不执行 → `ResizeObserver` 泄漏。修法：`Held` 增加 `closed` 标志、
`dispose()` 先置 `closed`、回调用 `.catch()` 吞掉 rejection 但**不置 `closed`**（一次瞬时抖动后下一次
击键仍应尝试回调；见 §14.3 的修正）；`EditorModule` 增加 `_disposed`、
释放时清空 `_loading`、导入期间若已释放则立即释放刚拿到的引用、`GetAsync` 在释放后返回 null。

新增浏览器断言：输入与三次连续示例切换交错后，无 console 错误、无失败请求、DOM 中恰好一个
`.cm-editor`（`attach()` 先 `dispose()`）。

### 13.4 口径补齐

- `tests/ActionLens.Core.Tests/ContractFixtures/regressions.yml`：把 `join()` / `toJSON()` /
  `fromJSON()` 零参、`branches-ignore` 空 payload、include 大小写碰撞固化成真正的 fixture 文件
  （第 8 节第 7 条），`ContractRegressionTests` 新增一条端到端断言消费它。放在 `ContractFixtures/`
  而非 `samples/workflows/`，因为后者是从真实仓库逐字拷贝、带手算期望数字的示例。
- CI 文件名以实际落地的 `.github/workflows/gate2.yml` 为准，`ARCHITECTURE.md` / `CHANGELOG.md` 已对齐。

**当时复测**：构建 0/0 · 格式 0 · Core 1406 · Ui 24 · Playwright 6 / 6。
第 8 节 1–14 条至此全部完成；后续增量见 §15。

---

## 14. 补记：从「风险/未决」升级为机器断言（2026-11-05 续二）

上一轮列出的待办里，有三项可以在本地闭环、不需要公开仓库，本轮全部完成。每一条都做了
**A/B 验证**（临时回退修复，确认新断言确实失败），避免再出现「写了断言但覆盖不到」的假绿。

### 14.1 §7 的跨进程字节差异：从风险变为结论

`ReportDeterminismTests` 原本只证明**同一进程**两次导出字节相同；§7 明确把「跨进程差异」
列为未实测的风险。新增 `tests/ActionLens.Core.Tests/Scenarios/CrossProcessDeterminismTests.cs`：
- `TwoSeparateProcesses_ExportIdenticalBytes` 启动**两个真正独立的进程**，各自导出一份包含
  三个 fixture、多种 payload / scenario、多键 matrix 的报告 bundle，父进程逐字节比较；
  并与父进程自己的导出比较（三方一致）。
- 实现上没有新增项目、没改 `ActionLens.sln`、没动 CI：探针就是本测试程序集里的一条 `[Fact]`，
  通过 MTP 的 `--filter-method` 单独运行，由环境变量 `ACTIONLENS_EXPORT_PATH` 指定输出文件。
  普通套件运行时它也是一条有效断言（同进程自比），不是空壳。
- `IncludeKeyInsertionOrder_DoesNotChangeTheExportedValues` 是确定性的补充：把同一组
  `include` 键按两种顺序写，断言结构化 `runs[].values` 相同且键已排序。
  `combinationLabels` 故意**不**纳入——它按 YAML 自身键顺序拼接，对给定输入是稳定的，
  要求两个不同输入产生相同标签属于测试设计错误（本轮第一次尝试就是这样，已修正）。

**A/B**：临时删掉 `AnalysisReportJson.WriteRun` 的 `OrderBy`，断言立刻失败并指出具体的键顺序差异
（`os,node,shard,region` vs `os,region,shard,node`）；恢复后通过。

### 14.2 `Session.Changed` 订阅的重渲染代价：收窄 + 真正可观测的断言

§13.2 为修「旧文本配新报告」给 `ActionLensWorkspace` 加了 `Session.Changed` 订阅，代价是
`NotifyChanged()` 的四个调用点（含每次击键的 `ScheduleAnalysis`）都会让整个 Atlas shell
`StateHasChanged`。本轮改为**只在文档文本真变化时**刷新：缓存 `_renderedWorkflowText` /
`_renderedPayloadText`，`OnSessionChanged` 比较后才刷新；编辑器自身上报走
`OnWorkflowTextChanged` 时先同步快照，避免回显引发整树重渲染；初始示例在订阅建立前加载，
避免首帧多余渲染。其余面板各自订阅 `Changed`，`_announcement` / `_revision` 的变更处也都自带
`StateHasChanged`，不受影响。

观测方式费了一周折，记录以免后来者重复踩坑：
- 先试浏览器侧给 `.cm-editor` 打 JS 身份标记、连续击键跨多个防抖窗口。该用例在
  `editor-typing.spec.ts` 保留，但 **A/B 显示回退收窄后它仍然通过**——Blazor 复用 DOM 节点，
  节点身份看不见重渲染。它的注释已如实改写：只保证「编辑器不被卸载重建 / 文本不丢 / 无控制台错误」，
  **不能**作为重渲染次数的证据。
- 真正有效的是 `ActionLens.Ui.Tests/WorkspaceRenderTests.cs`：用 bUnit 渲染
  `ActionLensWorkspace`（注册真实 `AtlasWorkspaceFactory`，并用 `ComponentFactories.AddStub`
  桩掉 `AtlasWorkspaceHost` 以避开 Atlas 的内容目录与 JS 运行时依赖），直接读 `RenderCount`。
  `SelectJob` / `SelectScenario` 这类纯通知不增加渲染次数；外部 `ReplaceText` 与加载示例会增加。
- **A/B**：回退收窄 → `RenderCount` 1→5、断言失败；收窄后 1→1、通过。
- 过程中还纠正了一个测试假设：直接调 public `ReplaceText` 属于**外部**文本变更，理应刷新；
  真实击键走的是会预同步快照的 `OnWorkflowTextChanged`。把前者误判为「不该重渲染」是错的。

### 14.3 `closed` 语义：一次瞬时失败不应永久静默

§13.3 的 `.catch(() => { held.closed = true; })` 把一次瞬时 interop 抖动当作永久关闭——
此后所有击键都被静默丢弃、再也到不了分析会话。改为 `.catch()` 只吞掉 rejection（仍然防止
未处理的 Promise 错误出现在控制台），**只有 `dispose()` 才置 `closed`**，下一次击键可以重试。

新增 `tests/e2e/editor-interop.spec.ts`：用模块导出的 `attach()` 挂一个临时编辑器，注入一个
第一次调用 reject、第二次 resolve 的假 .NET 目标，连续输入两个字符，断言回调被尝试两次。
**A/B**：恢复旧语义 → 只尝试 1 次、断言失败；新语义 → 2 次、通过。

### 14.4 口径

§13 标题由「两条 UI 缺陷」改为「三条」（13.1 竞态、13.2 示例切换、13.3 生命周期）；
§13.3 里「静默关闭」的措辞按 14.3 修正。

**复测**：构建 0/0 · 格式 0 · Core 1409 · Ui 25 · Playwright **8 / 8**。本轮新增 6 条断言
（Core 3、Ui 1、Playwright 2），浏览器套件由 6 条增至 8 条。详见 §15。

---

## 15. 全量回归（2026-11-05 续二收尾）

| 门 | 命令 | 结果 |
|---|---|---|
| 构建 | `dotnet build ActionLens.sln -v q --nologo` | ✅ 0 错误 / 0 警告 |
| 格式 | `dotnet format ActionLens.sln --verify-no-changes --no-restore` | ✅ 退出码 0 |
| Core | `dotnet tests/ActionLens.Core.Tests/bin/Debug/net10.0/ActionLens.Core.Tests.dll` | ✅ **1409 / 1409** |
| Ui | `dotnet tests/ActionLens.Ui.Tests/bin/Debug/net10.0/ActionLens.Ui.Tests.dll` | ✅ **25 / 25** |
| 浏览器 | `npm run test:e2e`（真实 Chromium） | ✅ **8 / 8** |

变化量：Core 1406 → 1409（跨进程 bundle 导出探针、双进程字节比较、include 键顺序）；
Ui 24 → 25（`WorkspaceRenderTests`）；Playwright 6 → 8（DOM 身份保护、interop 可恢复）。

三条 A/B 验证结论（回退修复后新断言均失败）：
1. 删掉 `WriteRun` 的 `OrderBy` → `IncludeKeyInsertionOrder...` 失败，指出 `os,node,shard,region`
   与 `os,region,shard,node` 的顺序差异。
2. 回退 workspace 收窄 → `RenderCount` 由 1 变 5，`WorkspaceRenderTests` 失败；收窄后恒为 1。
3. 恢复「失败即关闭」的 `.catch()` → interop 探针只尝试 1 次回调而非 2 次，浏览器用例失败。

**仍未执行**：`.github/workflows/gate2.yml` 在真实 GitHub Actions 上跑一次（需要公开仓库，
工作副本无 `.git`），以及由此解锁的公开仓库 / Pages 部署、Q6 第二批结构重构。按 Q5/F 的裁决，
这些都不应在本地伪造为已完成。
