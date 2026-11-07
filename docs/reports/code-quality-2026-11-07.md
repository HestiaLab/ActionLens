# ActionLens 代码质量审查报告

- **生成日期**: 2026-11-07
- **审查范围**: `src/`（ActionLens.Core / ActionLens.Ui / ActionLens.App）与 `tests/` 全部手写源码，排除 `obj/`、`bin/`、`node_modules/`、`wwwroot/js/dist/` 生成物
- **代码规模**: src 共 90 个 `.cs` 文件 / 16,399 行，另有 17 个 `.razor` / 1,773 行、1 个 `.ts` / 332 行、1 个 `.css` / 1,027 行、1 个 `.html` / 40 行；tests 共 32 个 `.cs` / 8,234 行 + 5 个 e2e `.ts` / 336 行
- **方法**: 自动化静态扫描（复杂度估算、5 行滑窗重复检测、安全正则、死代码启发式、测试映射）→ 热点文件人工深读核实 → 本地构建与测试取证 → 3 个独立子代理复核（附录 C）
- **取证结果**: `dotnet build` 0 警告 0 错误；Core 测试 **1430 通过 / 0 失败**（含 1003 条 GitHub 表达式语料），Ui bUnit 测试 **25 通过 / 0 失败**；Playwright 8 个浏览器用例在 Gate 2 CI 为绿（本次未本地重跑浏览器套件）

> 环境说明：本仓库使用 .NET 10 SDK + xUnit v3 / Microsoft.Testing.Platform，`dotnet test`（VSTest 模式）不受支持，CI 与本地均直接运行测试程序集（见 `.github/workflows/gate2.yml:64-68`）。这不是缺陷。

## 整体评分

| 维度 | 评级 | 一句话结论 |
|---|---|---|
| 1. 复杂度 | **B** | 少数方法偏长（最大 195 行），但全部有文档说明且复杂度源于问题域本身 |
| 2. 重复代码 | **B+** | 仅轻度重复：FilterDecision 构造与 unknown 传播样板，无大规模克隆 |
| 3. 死代码 | **A** | 启发式命中经逐一核实全部为误报，未发现确认的死代码 |
| 4. 安全 | **A** | 无硬编码秘密、无 XSS 面、敌意输入有界且全部转化为带定位的诊断 |
| 5. 依赖 | **A** | Core 仅依赖 YamlDotNet；中央包管理、版本钉死、分层单向由项目图强制 |
| 6. 测试覆盖 | **A-** | 1455 个单元/组件测试全绿 + 语料回归 + 确定性测试；App 宿主层仅靠 e2e 间接覆盖 |
| 7. 编码规范 | **A** | 0 个公共可变字段；XML 文档 pervasive；TreatWarningsAsErrors + format 硬门禁 |

**综合：A-**。这是一个工程质量显著高于平均水平的项目：架构边界（Core 纯引擎 / Ui 可 bUnit / App 薄宿主）由项目引用强制，"绝不抛出、绝不联网、unknown 必带理由"三条产品契约都有对应的可执行测试。剩余问题集中在少数长方法、轻度样板重复和一处过时文档。

---

## 1. 复杂度 — B

扫描估算 CC>10 的方法 23 个、>50 行的方法 25 个、>300 行的文件 18 个。**注意**：脚本的方法边界提取存在误判——头号命中 `WorkflowAnalyzer.TryLoad()`（CC=48 / 399 行）经深读核实实际只有 24 行（[WorkflowAnalyzer.cs:130](../../src/ActionLens.Core/WorkflowAnalyzer.cs#L130)），真正的长方法是同文件的 `Build()`（82 行）。以下仅列人工核实后的真实热点：

| # | 位置 | 情况 | 严重度 |
|---|---|---|---|
| 1.1 | [YamlSafetyScanner.cs:31](../../src/ActionLens.Core/Yaml/YamlSafetyScanner.cs#L31) `Scan()` | 195 行、CC≈28。token 流安全校验，已用 3 个局部函数分解；184–224 行的"扫描后裁决"段可再提取 | 🟡 中 |
| 1.2 | [MatrixExpander.cs:119](../../src/ActionLens.Core/Matrix/MatrixExpander.cs#L119) `Expand()` | 126 行、CC≈21。顺序守卫 clauses（动态维度→空值→乘积上限→展开→截断），每段有文档；可按守卫拆私有方法 | 🟡 中 |
| 1.3 | [ExpressionLexer.cs:102](../../src/ActionLens.Core/Expressions/ExpressionLexer.cs#L102) `ReadToken()` | 84 行、CC≈35。词法分析器 switch 分发，属该问题域的常规形态 | 🟢 低 |
| 1.4 | [WorkflowAnalyzer.cs:199](../../src/ActionLens.Core/WorkflowAnalyzer.cs#L199) `Build()` | 82 行流水线编排，各阶段已委托给命名良好的私有方法 | 🟢 低 |
| 1.5 | [ExpressionBodyParser.Primaries.cs:14](../../src/ActionLens.Core/Expressions/ExpressionBodyParser.Primaries.cs#L14) `ParsePrimaryWithPostfix()` | 136 行、嵌套深度 7。 Pratt 解析的主入口，后缀链（`.` `[]` `.*` `()`）交织 | 🟡 中 |
| 1.6 | 18 个 >300 行文件 | 其中 4 个最长 Core 文件已在 commit 9eecd18 拆为 partial；剩余大文件（ExpressionEvaluator 789 行、ScenarioEngine 629 行）内部按职责分段清晰 | 🟢 低 |

**建议**: 1.1 / 1.2 / 1.5 值得在下次触碰时顺手拆分；不建议为拆而拆——这些方法的注释密度足以支撑阅读，且全部有测试钉住行为。

## 2. 重复代码 — B+

5 行滑窗检出 35 组重复块，人工核实后绝大多数是对象初始化器风格的自然结果（`new Diagnostic { Code = …, Severity = … }`），真正可收敛的：

| # | 位置 | 情况 | 严重度 |
|---|---|---|---|
| 2.1 | [TriggerEvaluator.Verdict.cs](../../src/ActionLens.Core/Triggers/TriggerEvaluator.Verdict.cs)（147、173、274、388、469、508 行，共 6 处） | "unknown 决策"构造重复：`new FilterDecision { …, Note = reason.Message, Reason = reason }` 出现 6 次，可提取 `UnknownDecision(filter, reason, pattern, range)` 辅助方法 | 🟢 低 |
| 2.2 | [ExpressionEvaluator.cs:70](../../src/ActionLens.Core/Expressions/ExpressionEvaluator.cs#L70)、98 等 3 处 | `Evaluate(target)` → `!IsKnown` → `Unknown(Propagating(...))` 传播样板在 Member/Index/Wildcard 三个路径重复 | 🟢 低 |
| 2.3 | [TriggerEvaluator.Verdict.cs:30](../../src/ActionLens.Core/Triggers/TriggerEvaluator.Verdict.cs#L30) 等 3 处 | `new TriggerVerdict { Event = …, PatternRange = … }` 构造重复 | 🟢 低 |

**建议**: 均为低优先级。仓库已在 commit 9eecd18 折叠过 Levenshtein / JSON 转换重复，当前剩余重复量不构成技术债。

## 3. 死代码 — A

启发式扫描（私有方法文件内无调用）命中约 50 项，**逐一抽查核实后全部为误报**，来源有三：

- partial 类跨文件调用（如 `FailHere` 定义于 ExpressionBodyParser.cs、调用于 .Primaries.cs / .Expressions.cs）；
- 方法组引用（`new Lazy<…>(LoadWorkflows)`，[WorkflowExamples.cs:39](../../src/ActionLens.Ui/Services/WorkflowExamples.cs#L39)）；
- 事件订阅 `+=` / `-=`（如 `Session.Changed += OnSessionChanged`，[ActionLensWorkspace.razor.cs:71](../../src/ActionLens.Ui/Components/ActionLensWorkspace.razor.cs#L71)）。

未发现确认的死代码、未订阅的事件处理器或遗留 fallback 路径。公共表面已收窄至 15 个契约类型（见 ARCHITECTURE.md Roadmap §4）。

## 4. 安全 — A

对全部 `.cs` / `.ts` / `.razor` / `.html` / `.json` / `.csproj` / `.css` 两轮扫描：

| 检查项 | 结果 |
|---|---|
| 硬编码秘密（password/secret/apikey/token/connectionstring 字面量） | 0 命中 |
| XSS 面（`MarkupString` / `InnerHtml` / `Html.Raw` / `innerHTML` / `document.write` / `eval` / `new Function`） | 0 命中 |
| 不安全反序列化（BinaryFormatter 等） | 0 命中 |
| 弱加密（MD5/SHA1/DES/RC4） | 0 真实命中（初扫 15 条均为 "Nodes/decides/sides" 子串误报） |
| 空 catch | 0 命中 |
| `catch (Exception)` | 16 处，**全部经深读核实为刻意设计**（见下） |

**16 处 catch 的核实结论**：

- [WorkflowAnalyzer.cs:182](../../src/ActionLens.Core/WorkflowAnalyzer.cs#L182) 的无过滤 catch-all 是产品契约"分析绝不抛出"的最后一道闸，产生 `INT001` 诊断，且有 `ContractRegressionTests`（430 行）专门钉住该行为——**by design，不是缺陷**。
- 其余 15 处全部带 `when` 异常类型过滤（JSException / ObjectDisposedException / OperationCanceledException 等），是文档化的降级路径（编辑器 fallback、工作区已释放等）。
- [AnalysisSession.cs:298](../../src/ActionLens.Ui/Services/AnalysisSession.cs#L298) 的 catch-all 同样是契约兜底，注释明确说明"WorkflowAnalyzer is contracted never to throw. If it does…"。

**敌意输入防护**（SECURITY.md 承诺 + 代码核实）：YAML 1 MB / 事件数 200k / 别名 128 / 深度 64 / 矩阵乘积 4096 上限，超限全部转化为带定位诊断而非异常；`LocalFirstGuardTests` 以源码扫描强制无网络客户端，Playwright 同源断言在浏览器层兜底。

| # | 位置 | 情况 | 严重度 |
|---|---|---|---|
| 4.1 | [SECURITY.md:20](../../SECURITY.md#L20) | 文档过时：称浏览器级网络断言 "still outstanding"，但 [boot-and-network.spec.ts:27](../../tests/e2e/boot-and-network.spec.ts#L27) 的同源断言已存在且 Gate 2 为绿 | 🟡 中（文档一致性） |

## 5. 依赖 — A

- **Core**: 仅 `YamlDotNet 18.1.0`，无 Blazor / JS / 网络依赖（csproj 注释明确声明）。
- **Ui**: `Atlas.Blazor 0.4.0`（钉死已发布版）+ `Microsoft.AspNetCore.Components.Web 10.0.14`。
- **App**: WASM 10.0.14 三件套；Node 侧 8 个 CodeMirror 包全部精确钉版，esbuild / Playwright 为 devDependencies。
- 中央包管理（`Directory.Packages.props`，含传递钉死）；分层单向 App→Ui→Core 由项目引用强制。
- 文件级"循环依赖"扫描命中 162 对，核实为共享数据类型（`Diagnostic`、`Evaluated`、`SourceRange` 被全引擎引用）造成的启发式误报；抽查 `ConditionEvaluator ↔ ExpressionEvaluator` 仅为 XML doc `<see cref>` 引用。**无真实循环**。

## 6. 测试覆盖 — A-

**实测（2026-11-07 本地）**: Core **1430 通过**（296 个 `[Fact]/[Theory]` 方法，Theory 展开含 1003 条 GitHub 官方表达式语料），Ui bUnit **25 通过**，0 失败。另有 Playwright 8 用例（boot/网络 3、诊断跳转 1、编辑器互操作 1、键入 3）在 Gate 2 CI 绿。

覆盖亮点：错误断言"定位+码"而非消息文本；跨进程确定性测试；契约回归（INT001 输入集）；LocalFirstGuard 带自检防止扫描空转。

启发式映射（测试文本引用源类名）显示 48/90 源文件被直接引用，未引用文件中绝大多数是 partial 分片、简单数据记录或经集成路径间接覆盖（如 `JobGraphAnalyzer` 经 `JobGraph.Build` 被 [JobGraphTests.cs](../../tests/ActionLens.Core.Tests/Graph/JobGraphTests.cs) 覆盖）。真实缺口：

| # | 位置 | 情况 | 严重度 |
|---|---|---|---|
| 6.1 | [EditorModule.cs](../../src/ActionLens.App/Services/EditorModule.cs)、[Program.cs](../../src/ActionLens.App/Program.cs) | App 宿主无单元测试，仅靠 Playwright 间接覆盖；`GetAsync` 的并发共享导入与 disposed 竞态分支（36–66 行）无直接断言 | 🟡 中 |
| 6.2 | [GraphGeometry.cs](../../src/ActionLens.Ui/Services/GraphGeometry.cs)、[ReportPresentation.cs](../../src/ActionLens.Ui/Services/ReportPresentation.cs) | 纯函数服务无直接单元测试（部分经 GraphCanvasStateEncodingTests 间接覆盖） | 🟢 低 |

## 7. 编码规范 — A

- 公共可变字段：**0**。
- XML 文档注释覆盖公共成员近乎完整，且注释解释 WHY（契约、历史缺陷、语义歧义）而非复述代码——密度与质量显著高于常态。
- `TreatWarningsAsErrors` + `dotnet format --verify-no-changes` 为 CI 硬门禁；本次本地构建 0 警告。
- 命名一致（私有字段 `_` 前缀、ordinal 字符串比较 everywhere、1-based 位置约定全引擎统一）。
- 唯一小瑕疵：[editor.ts:171](../../src/ActionLens.App/ClientScripts/editor.ts#L171) 用 `null as unknown as EditorView` 双重断言初始化 `Held`，由外层 try/catch 兜底；可改为先构造 view 再填记录，🟢 低。
- 另：[seed-offline-node-modules.mjs:22](../../scripts/seed-offline-node-modules.mjs#L22) 硬编码了开发机绝对路径作为离线回退源（有 `ACTIONLENS_JS_SOURCE` 环境变量覆盖，脚本注释已声明其机器特定用途）；可移植性小瑕疵，🟢 低。

---

## 改进优先级

| 优先级 | # | 事项 | 建议动作 |
|---|---|---|---|
| 🔴 P0 | — | 无 | 未发现崩溃、数据丢失或安全漏洞级问题 |
| 🟠 P1 | 6.1 | EditorModule 并发/处置分支无直接测试 | 为 `GetAsync` 共享导入、disposed 后返回 null 补单元测试（可 stub IJSRuntime） |
| 🟠 P1 | 4.1 | SECURITY.md 过时断言 | 将 "still outstanding" 更新为指向 boot-and-network.spec.ts 的已完成状态 |
| 🟡 P2 | 1.1 | YamlSafetyScanner.Scan 195 行 | 提取 184–224 行裁决段为 `BuildVerdict(...)` |
| 🟡 P2 | 1.2 | MatrixExpander.Expand 126 行 | 按守卫拆私有方法（动态/空值/上限各一段） |
| 🟡 P2 | 1.5 | ParsePrimaryWithPostfix 136 行 | 后缀链分发拆分 |
| 🟡 P2 | 6.2 | GraphGeometry / ReportPresentation | 补纯函数单元测试 |
| 🟢 P3 | 2.1–2.3 | FilterDecision / unknown 传播样板 | 触碰时顺手提取辅助方法 |
| 🟢 P3 | 7.1 | editor.ts 双重断言 | 重构 Held 初始化顺序 |
| 🟢 P3 | 7.2 | seed 脚本硬编码本机路径 | 已有环境变量覆盖；可将回退源改为相对探测或纯环境变量驱动 |

## 附录 A：文件统计（wc -l 实测）

### src/ .cs（90 文件 / 16,399 行）

| 目录 | 文件数 | 行数 |
|---|---|---|
| ActionLens.App | 1 | 23 |
| ActionLens.App/Services | 1 | 122 |
| ActionLens.Core（根） | 1 | 528 |
| ActionLens.Core/Diagnostics | 3 | 160 |
| ActionLens.Core/Expressions | 15 | 3,367 |
| ActionLens.Core/Graph | 4 | 740 |
| ActionLens.Core/Linting | 8 | 680 |
| ActionLens.Core/Linting/Rules | 8 | 565 |
| ActionLens.Core/Matrix | 1 | 511 |
| ActionLens.Core/Reporting | 4 | 845 |
| ActionLens.Core/Scenarios | 9 | 1,941 |
| ActionLens.Core/Text | 4 | 131 |
| ActionLens.Core/Triggers | 7 | 1,822 |
| ActionLens.Core/Values | 3 | 584 |
| ActionLens.Core/Workflows | 5 | 329 |
| ActionLens.Core/Yaml | 7 | 2,114 |
| ActionLens.Ui/Components | 2 | 669 |
| ActionLens.Ui/Services | 6 | 1,031 |
| ActionLens.Ui/Workspace | 1 | 237 |
| **合计** | **90** | **16,399** |

### 其他源码

| 范围 | 文件数 | 行数 |
|---|---|---|
| src `.razor` | 17 | 1,773 |
| src `.ts`（editor.ts） | 1 | 332 |
| src `.css` | 1 | 1,027 |
| src `.html` | 1 | 40 |
| tests `.cs` | 32 | 8,234 |
| tests/e2e `.ts` | 5 | 336 |

## 附录 B：术语

| 术语 | 含义 |
|---|---|
| CC | 圈复杂度（分支关键字计数估算，含 &&/||/三元） |
| 三态 / Kleene | 表达式的 true / false / unknown 三值逻辑，unknown 必带 UnknownReason |
| INT001 | 引擎内部缺陷诊断码——"绝不抛出"契约的兜底产物 |
| Gate 2 | CI 发布门禁：构建 0 警告 + format + 两套测试程序集 + Playwright |
| partial 分片 | 同一类拆到多个文件（commit 9eecd18 的结构清理） |
| bUnit | Blazor 组件级测试框架，Ui 做成 RCL 正是为了可被它渲染 |

## 附录 C：复核记录

**复核日期**: 2026-11-07
**复核方式**: 3 个独立子代理并行复核（只读），主代理修正偏差

### C.1 问题真实性验证（Agent 1 — 抽样 6 项）

| # | 问题 | 结论 | 证据 |
|---|---|---|---|
| 1.1 | YamlSafetyScanner.Scan 195 行 | ✅ 确认 | 31–225 行恰 195 行；184–224 为裁决段 |
| 1.2 | MatrixExpander.Expand 126 行守卫结构 | ✅ 确认 | 119–244 行；守卫顺序与报告一致 |
| 2.1 | FilterDecision unknown 构造重复 | ⚠️ 部分正确 | 实为 6 次（147/173/274/388/469/508），初稿写"约 5 次"，已修正 |
| 4.1 | SECURITY.md 过时断言 | ✅ 确认 | SECURITY.md:20 vs boot-and-network.spec.ts:32 |
| 6.1 | EditorModule 无直接单测 | ✅ 确认 | tests 中仅 IEditorModule/StubEditorModule 引用，无本体测试 |
| 非缺陷 | WorkflowAnalyzer catch-all 为刻意契约 | ✅ 确认 | INT001 诊断 + ContractRegressionTests 8 处断言恶意输入不触发该 catch |

### C.2 安全漏洞遗漏扫描（Agent 2）

| 检查项 | 扫描范围 | 结果 |
|---|---|---|
| XSS / 动态执行 | src+tests+scripts 全部目标文件 | 0 真实命中 |
| 硬编码秘密（含 ghp_/AKIA/Bearer/PRIVATE KEY 补充模式） | 同上 | 0 命中（src 中 `secrets` 均为 Actions 语义） |
| 弱加密（词边界） | 同上 | 0 真实命中 |
| 路径遍历 | src | 0 命中 |
| 不安全反序列化 | src+tests+scripts | 0 真实命中 |
| 网络外联 | src 严格扫描 | 0 命中（tests 命中为守卫测试自身的模式字符串，预期） |
| 构建脚本命令注入 / 远程下载 | scripts/*.mjs 全文精读 | 0 命中 |
| 依赖 / postinstall | 5 个 csproj + package.json | 干净，版本精确锁定 |

结论：**未发现遗漏**。附带发现 seed 脚本本机路径问题（已列入 7.2 / P3）。

### C.3 统计自洽性验证（Agent 3）

| 验证项 | 结论 | 详情 |
|---|---|---|
| src .cs 总数 | ✅ | 90 文件 / 16,399 行，重算一致 |
| 附录 A 19 个目录明细 + 合计 | ✅ | 38 个数值点全部吻合，明细求和自洽 |
| razor/ts/css/html 统计 | ✅ | 17/1,773、1/332、1/1,027、1/40 |
| tests 统计 | ✅ | 32/8,234；e2e 5/336 |
| 测试方法数 | ✅ | Core 296、Ui 25、e2e test( 8 |
| 3 个文件行数抽查 | ✅ | 789 / 629 / 259 全部一致 |

### C.4 复核总结

| 维度 | 结果 |
|---|---|
| 真实性 | 6 项抽样：5 项完全确认，1 项计数偏差（已修正），无捏造 |
| 安全完整性 | 第二遍独立扫描未发现遗漏 |
| 统计自洽 | 误差率 0% |
| 处置 | 偏差 <5%，直接修正，无需第二轮复核 |