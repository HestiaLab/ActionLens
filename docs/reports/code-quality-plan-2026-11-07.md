# ActionLens 质量修复计划

- **日期**: 2026-11-07
- **依据**: [code-quality-2026-11-07.md](code-quality-2026-11-07.md)（综合 A-，无 P0）
- **范围**: 2 项 P1 + 4 项 P2；P3（样板重复、editor.ts 断言、seed 脚本路径）不在本计划内，留待触碰时顺手处理
- **总原则**: 每个 Phase 独立可提交、独立可回滚；重构 Phase 全部行为保持（behavior-preserving），由现有 1430 + 25 个测试钉住；先补测试安全网，再动结构

## 实施顺序

```text
B (文档修正, 5min) ──────────────────────────────┐
A (App.Tests 新项目 + EditorModule 测试) ────────┤
F (GraphGeometry / ReportPresentation 测试) ─────┼─▶ C → D → E (Core 重构, 风险递增)
                                                 │
        A/F 与 B/C/D/E 互不依赖，可并行；C/D/E 建议串行以便定位回归
```

统一验收命令（与 CI 一致，`dotnet test` 在本 SDK 不受支持）：

```bash
dotnet build ActionLens.sln -v q --nologo          # 0 警告 0 错误
dotnet format ActionLens.sln --verify-no-changes --no-restore
dotnet tests/ActionLens.Core.Tests/bin/Debug/net10.0/ActionLens.Core.Tests.dll
dotnet tests/ActionLens.Ui.Tests/bin/Debug/net10.0/ActionLens.Ui.Tests.dll
```

---

## Phase B — 修正 SECURITY.md 过时断言（P1 / 4.1）

- **文件**: `SECURITY.md:20-21`
- **问题**: 称浏览器级网络断言 "still outstanding"，但 `tests/e2e/boot-and-network.spec.ts:32` 的同源断言已存在且 Gate 2 为绿
- **方案**: 将该句改为已完成状态并指向证据：

  ```markdown
  The browser-level assertion is the Gate 2 Playwright case "every resource request stays on the
  same origin" (tests/e2e/boot-and-network.spec.ts), green in CI; the source guard alone cannot
  prove that no request left the page, which is why both halves exist.
  ```

- **影响范围**: 仅文档。注意 `DocFactsTests` 一类文档事实测试可能引用该段措辞——改后先跑 Core 套件确认
- **验证**: 统一验收命令（重点 Core 套件中的文档测试）
- **预估耗时**: 10 分钟
- **风险/回滚**: 无风险；`git revert` 单提交

## Phase A — EditorModule 单元测试（P1 / 6.1）

- **文件**: 新建 `tests/ActionLens.App.Tests/`（csproj + `EditorModuleTests.cs` + `Supporting/StubJSRuntime.cs`）；修改 `ActionLens.sln`、`.github/workflows/gate2.yml`
- **问题**: [EditorModule.cs:36-66](../../src/ActionLens.App/Services/EditorModule.cs#L36) 的并发共享导入、disposed 竞态、加载失败缓存三条分支无直接测试，仅靠 Playwright 间接覆盖
- **方案**:
  1. **新建 App.Tests 而非扩展 Ui.Tests**：EditorModule 在 App 宿主层，让 Ui.Tests 引用 App 会颠倒测试图的依赖方向，违背仓库"Ui 可脱离宿主测试"的分层原则。csproj 复制 Ui.Tests 的骨架（xunit.v3 + MTP runner + `TestingPlatformDotnetTestSupport`），ProjectReference 指向 App
  2. **Stub IJSRuntime**：手写 `StubJSRuntime : IJSRuntime`（两个 `InvokeAsync` 重载），可编程返回 module 引用 / 抛 JSException / 延迟完成；`StubJSObjectReference : IJSObjectReference` 记录 `DisposeAsync` 调用次数。不引入 Moq 等新依赖
  3. **测试用例**（约 8 个）：
     - 并发两个 `GetAsync()` 只触发一次 `import`（共享 `_loading`）
     - 加载成功后 `IsLoaded` 为 true、第二次调用不再 import
     - import 抛 JSException → 返回 null、`FailureReason` 有值、后续调用不再重试
     - `isAvailable` 返回 false → module 被 Dispose、返回 null
     - 加载途中 `DisposeAsync` → `GetAsync` 返回 null，不泄漏引用（38-44 行注释所述历史缺陷的回归钉）
     - Dispose 后 `GetAsync` 返回 null；重复 Dispose 幂等
     - Dispose 时 module 释放抛 JSException 被吞（页面消亡路径）
  4. **CI**: gate2.yml 的 engine job 增加一步 `dotnet tests/ActionLens.App.Tests/bin/Debug/net10.0/ActionLens.App.Tests.dll`
- **影响范围**: 新增测试项目 + sln + CI 一步；不改任何产品代码。App.csproj 的 BuildEditorBundle target 会在测试构建时跑 npm——CI 已在 build 前执行 `npm run build:editor`，本地亦然，无新增前置条件
- **验证**: 统一验收命令 + 新程序集运行；确认 gate2.yml 两个 job 绿
- **预估耗时**: 2–3 小时（含 CI 一轮）
- **风险/回滚**: 低。唯一风险是 WASM SDK 项目被测试项目引用时的构建行为——若遇到，退回方案是在 App.Tests 中直接链接编译 `EditorModule.cs`（`<Compile Include="../../src/.../EditorModule.cs" />`）加 `IEditorModule` 接口文件，不动产品代码

## Phase F — GraphGeometry / ReportPresentation 纯函数测试（P2 / 6.2）

- **文件**: 新建 `tests/ActionLens.Ui.Tests/GraphGeometryTests.cs`、`ReportPresentationTests.cs`
- **问题**: 两个纯函数服务无直接单元测试；GraphGeometry 的确定性是产品需求（"两个读者看到同一张图"），目前仅靠组件测试间接覆盖
- **方案**: 放入现有 Ui.Tests（无需新 ProjectReference，两者都在 Ui 层）：
  - **GraphGeometry**（约 6 个）：空节点列表 → 零尺寸画布；单层多节点 slot 纵向递增；短层对最高层垂直居中；`Number()` 在 `CultureInfo` 为 de-DE/fr-FR 时仍输出不变小数点（用 `CultureInfo.CurrentCulture` 切换 + try/finally 还原）；同一输入两次 Build 输出逐字节相同；`BoxFor` 对未放置 job 返回 null
  - **ReportPresentation**（约 5 个）：每个 `RunState`/`DiagnosticSeverity`/`TriKind` 枚举值都有非空 label/class（穷举 switch 防新增枚举漏配）；`InstanceSummary` 对 null/动态/截断矩阵的措辞；`Position` 输出 `line:column` 格式
- **影响范围**: 仅新增测试文件
- **验证**: 统一验收命令（Ui 套件新增约 11 个）
- **预估耗时**: 1.5 小时
- **风险/回滚**: 无产品代码风险；测试若揭示真实缺陷，单独开 issue 不夹带修复

## Phase C — YamlSafetyScanner.Scan 提取裁决段（P2 / 1.1）

- **文件**: `src/ActionLens.Core/Yaml/YamlSafetyScanner.cs`
- **问题**: `Scan()` 195 行（31–225），其中 184–224 行的"扫描后裁决"（documents/aliases/tooDeep 三个诊断 + canContinue 汇总）与 token 循环无关
- **方案**: 提取为私有静态方法，行为逐字保持：

  ```csharp
  // Scan() 尾部（原 184–224 行）替换为：
  return BuildVerdict(diagnostics, documents, aliases, tooDeep, stoppedEarly, duplicates);

  private static ScanResult BuildVerdict(
      List<Diagnostic> diagnostics, int documents, int aliases,
      bool tooDeep, bool stoppedEarly, int duplicates)
  {
      // 原 184–224 行原样搬入，仅作纯移动，不改任何条件与消息
  }
  ```

  三个局部函数（HandleLeaf/EnterContainer/ExitContainer）保留在 Scan 内——它们闭包捕获 frames/diagnostics，移动反而引入参数噪音
- **影响范围**: 单文件；`Scan` 降至约 155 行。internal 类，无公共 API 变化
- **验证**: 统一验收命令；重点 `WorkflowLoaderTests`（580 行定位断言）与 `ContractRegressionTests` 必须全绿
- **预估耗时**: 30 分钟
- **风险/回滚**: 低（纯移动）；`git revert` 单提交

## Phase D — MatrixExpander.Expand 守卫拆分（P2 / 1.2）

- **文件**: `src/ActionLens.Core/Matrix/MatrixExpander.cs`
- **问题**: `Expand()` 126 行（119–244），五段顺序守卫（scheduling 提示 → 动态维度 → 空值 → 乘积上限 → 展开/截断）挤在一个方法里
- **方案**: 早退守卫提取为返回 `MatrixExpansion?` 的私有静态方法（null = 继续），`Expand` 变为编排：

  ```csharp
  public static MatrixExpansion Expand(StrategySection? strategy, MatrixSection? matrix, SourceRange fallbackRange)
  {
      var diagnostics = new List<Diagnostic>();
      NoteUnmodelledScheduling(strategy, fallbackRange, diagnostics);   // 原 126–139，Info 不中断
      if (matrix is null) return MatrixExpansion.NoMatrix with { Diagnostics = diagnostics };
      return RejectDynamic(matrix, fallbackRange, diagnostics)         // 原 146–171
          ?? ExpandStatic(strategy, matrix, fallbackRange, diagnostics); // 原 173–244
  }
  ```

  `ExpandStatic` 内再提取 `NoteEmptyDimensions`（原 173–184）与 `RejectOversizedProduct`（原 186–209，返回 `MatrixExpansion?`）。**关键不变量**：诊断追加顺序、`MaxGeneratedCombinations` 在分配任何 CombinationBuilder 之前拒绝（原 190–192 注释所述）、截断时 `ProducedCount` 语义——全部原样保留
- **影响范围**: 单文件；`Expand` 降至约 20 行，新增 4 个私有方法。公共签名不变
- **验证**: 统一验收命令；重点 `MatrixExpanderTests`（327 行，逐条 include/exclude 规则）全绿
- **预估耗时**: 1 小时
- **风险/回滚**: 中低——早退顺序错一位就会改变诊断集合；靠现有测试钉住，`git revert` 单提交

## Phase E — ParsePrimaryWithPostfix 后缀链拆分（P2 / 1.5）

- **文件**: `src/ActionLens.Core/Expressions/ExpressionBodyParser.Primaries.cs`
- **问题**: 136 行（14–149），三个 postfix case（`.` / `[]` / 续参数列表）交织，嵌套深度 7
- **方案**: 每个 case 体提取为私有方法，循环只做分发：

  ```csharp
  while (!_aborted)
  {
      switch (Current.Kind)
      {
          case ExpressionTokenKind.Dot:
              target = ParsePostfixMember(target, ref charged); break;
          case ExpressionTokenKind.LeftBracket:
              target = ParsePostfixIndex(target, ref charged); break;
          case ExpressionTokenKind.OpenArguments when target is FunctionCallNode call:
              target = ParsePostfixArguments(target, call, ref charged); break;
          default:
              for (var i = 0; i < charged; i++) ExitLevel();
              return target;
      }
      if (target is null) return null;
  }
  ```

  **关键不变量**（必须逐字保持，这是本 Phase 风险最高的点）：
  - `EnterLevel()` 成功后立即 `charged++`，且 charged 的 ExitLevel 配对只发生在 default 分支——`ref charged` 保持计数语义不变
  - 失败路径（FailHere/Fail/EnterLevel 失败）直接返回 null，不做 ExitLevel——与原代码一致，不要"顺手修复"
  - `a[*]` 与 `a.*` 构造同一个 WildcardAccessNode、range 端点取各自闭合 token
- **影响范围**: 单文件；主方法降至约 35 行，新增 3 个私有方法。internal partial 类，无 API 变化
- **验证**: 统一验收命令；重点 `GithubExpressionCorpusTests`（1003 条语料）与表达式定位断言全绿——语料是此重构最强的安全网
- **预估耗时**: 1.5 小时
- **风险/回滚**: 中——深度计费（charged/EnterLevel/ExitLevel）是防 runaway 表达式的护栏，语义漂移会放过敌意输入或误杀合法链；`git revert` 单提交

---

## 验收标准（全部 Phase 完成后）

| 项 | 标准 |
|---|---|
| 构建 | `dotnet build` 0 警告 0 错误 |
| 格式 | `dotnet format --verify-no-changes` 通过 |
| Core 套件 | 1430 通过 / 0 失败（重构不得改变任何计数） |
| Ui 套件 | 25 + 约 11 新增，全绿 |
| App 套件 | 约 8 新增，全绿 |
| CI | gate2.yml 两个 job 在 PR 上绿 |
| 指标 | Scan ≤160 行、Expand ≤25 行、ParsePrimaryWithPostfix ≤40 行 |

## 风险与回滚总则

- 每个 Phase 一个 commit（遵循仓库 conventional commits 风格：`test(app):`、`docs:`、`refactor(core):`），独立 `git revert` 即可回滚
- C/D/E 为行为保持重构：任何测试计数变化（除新增外）都视为回归，先 revert 再排查
- 不在本计划内夹带 P3 清理；若重构时发现真实缺陷，停下来单独报告，不静默修
- 当前分支 `refactor/implicit-atlas-content-routes` 已有未合并工作——建议从 `dev` 切新分支 `refactor/quality-plan-2026-11` 实施，避免夹带