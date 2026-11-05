# Gate 1 报告（W1–W2 技术验证）

日期：2026-11-04 · 结论：**通过**。初判为「未通过（条件性）」，其后的两轮修正把表达式层拉回与官方
语料一致：**1145 个用例全部通过，0 失败**（自有模块 142/142，GitHub 官方表达式语料 1003/1003），
构建 0 错误 0 警告，`dotnet format --verify-no-changes` 干净。

第 2 节保留初判数据作为过程记录；第 5 节的「下一步」已按修正结果改写，实际结论见文末第 6 节。

> **状态说明（2026-11-05）**：本文是 Gate 1 时点的历史快照，第 2 节的数字与第 4/6.4 节里用现在时
> 描述的「未完成」项均已被后续工作推翻，保留仅为过程记录。当前状态：三个 fixture 已由
> `tests/ActionLens.Core.Tests/EndToEnd/FixturesTests.cs` 跑通端到端；`ActionLens.Ui` RCL 与
> `ActionLens.Ui.Tests`（bUnit）已存在；四份限制文档已落盘；表达式定位的根因见
> `docs/review-v1-grill.md` 分支 A。

## 1. 已实现

| 模块 | 位置 | 状态 |
|---|---|---|
| 解决方案骨架 | `ActionLens.sln` + Core/App/Core.Tests/App.Tests | 完成，`net10.0`，中央包管理，Atlas.Blazor 0.4.0 已锁定 |
| YAML 安全预扫描 | `src/ActionLens.Core/Yaml/YamlSafetyScanner.cs` | 完成：文档数、别名数(128)、深度(64)、token 数(200k)、**全部**重复 key 及双位置 |
| YAML 建模 + 校验 | `WorkflowModelBuilder.cs` / `WorkflowLoader.cs` | 完成：1-based 行列、`on` 的 YAML 1.1 拼写归一、锚点复用、**自行实现 `<<` 合并语义**、schema 诊断 |
| 工作流 DTO | `Workflows/*.cs` | 完成：job/step/matrix/trigger/env/outputs 带 `SourceMap` 与继承标记 |
| 表达式词法/语法 | `Expressions/ExpressionLexer.cs`、`ExpressionBodyParser.cs`、`ExpressionParser.cs` | 完成：`${{ }}` 与裸 `if:`、模板插值、优先级表、深度/长度上限 |
| 三态求值 | `ExpressionEvaluator.cs`、`ThreeState.cs`、`ExpressionCoercion.cs` | 完成：Kleene 逻辑、`&&`/`||` 返回操作数、unknown 原因树、13 个函数、按位置限制 context |
| 隐式 `success()` | `ConditionEvaluator.cs` | 完成：树中无状态函数时包装为 `success() && (...)` |
| 矩阵展开 | `Matrix/MatrixExpander.cs` | 完成：笛卡尔积、exclude 先行部分匹配、include 过滤/追加/空过滤、256 上限、动态维度拒猜 |
| Job DAG | `Graph/*.cs` | 完成：`needs` 建边、缺失依赖、**带路径的环报告**、拓扑序、最长路径分层、文档序槽位、祖先/后代 |
| 官方语料适配 | `tests/.../GithubExpressionCorpusTests.cs` | 完成：1003 个用例可执行 |
| fixture | `samples/workflows/` | 完成：2 个真实 + 1 个合成不安全样例 |

关键工程判断（都有代码注释与测试）：
- **不使用 `MergingParser`**：实测它会把「`<<: *anchor` 后显式覆盖同名 key」误报为重复 key，而这在真实
  workflow 里极常见。合并语义在 `WorkflowModelBuilder` 内自实现（显式 key 优先，多来源按序）。
- **重复 key 阻断建模**：YamlDotNet 的对象图在重复 key 上会抛异常；预扫描已能报出全部重复项与两处位置。
- **`G15` 数字格式、`null → ''`、数组/对象 → `"Array"`/`"Object"`** 等均按 runner 实现校正，而非按文档字面。

## 2. 测试结果

总计 **1145 个断言用例：883 通过，262 失败**（执行 1.2s，无网络/时钟/随机依赖）。

| 分组 | 总数 | 通过 | 失败 |
|---|---|---|---|
| 自有模块单测（YAML / 矩阵 / DAG / 强制规则） | 142 | 106 | **36** |
| GitHub 官方表达式语料 | 1003 | 777 | **226** |

失败按类别（不是按用例名罗列，便于判断是否可接受）：

**A. 我自己的规格里写错的部分 —— 语料证明是我想错了（占语料失败的大头）**
- 数字字面量形式：我给解析器代理的指令是「Actions 没有 hex/octal 字面量，`0xff` 应报 EXPR001」。
  `coerce_number.json` / `number.json` 表明 runner 词法器**接受** `0x`、`0o`、`Infinity`、`NaN` 形式。
  → 需修正词法器的数字规则（不是我原先写的文档推断），并把 `docs/expression-semantics.md` 里那条
  「按文档字面拒绝」改掉。这是 Gate 1 的主要收益之一：它抓出了我的规格错误。
- 索引通配 `a.*`：`op_idx_star.json`(41) 与 `op_idx.json`(21) 表明 GitHub 支持 `.*` 通配索引，
  当前 AST 与词法都不建模它。需要决定：完整支持，还是明确降级为 unknown 并在限制文档中声明。
- `operators_precedence.json`(16)：优先级表实现后仍有偏差，需要逐例核对（例如 `==` 与 `<` 的相对位置
  已在文档中确认，但链式求值的返回值类型还需对齐）。

**B. 尚未修的实现缺陷（自有测试里的 36 个）**
- `ExpressionCoercionTests` 中 `TextCoercesToTheNumberActionsSees`(4) 与
  `RelationalOperatorsFollowTheSameCoercion`(6)：我按报告写的期望与当前实现不一致，需要判定哪一侧错。
- `WorkflowLoaderTests` 若干定位/建议类断言：`RecordsPositionsOfEveryInterestingValue`、
  `UnknownFieldIsRejectedWithTheNearestRealName`、`KeepsMatrixDimensionsIncludeAndExcludeWithPositions`、
  `RejectsMalformedUses`、`ReportsMatrixDeclaredDirectlyOnTheJob` —— 多为行列号断言与实际节点范围
  的边界约定（值节点起点 vs key 起点）不一致，属于可判定后一次性对齐的问题。
- `JobGraphTests.Reachability`(2)：环图中的祖先/后代集合边界与测试期望不一致。

**C. 并行代理中断留下的痕迹（已全部修复，作为风险记录）**
- 表达式代理因模型中断未交付任何测试，且 `One()` 只前进 1 个字符，导致 `==`、`!=`、`&&`、`||`、`<=`、`>=`
  全部被拆成两个单字符 token —— 这是当时 577 个失败的主因。修复后通过数从 568 升到 883。
- DAG 代理留下重复常量定义与集合表达式类型推断问题，已修。
- 结论：代理产出的**语义**代码必须由主代理逐行验证；本轮所有代理文件我都做了编译+语料双重校验。

## 3. fixture 结果

三个样例已落盘（真实文件与来源 commit 逐字节一致，头部注明来源与 SHA）：

| 文件 | 来源 | 覆盖能力 |
|---|---|---|
| `atlas-ci.yml` | HestiaLab/Atlas `.github/workflows/ci.yml`（本地 HEAD `f2def23d`） | `workflow_dispatch`、matrix(2 值)、`runs-on: ${{ matrix.os }}`、`if: always()`、tag 固定的远程 action、多行 `run` |
| `matrix-build.yml` | prettier/prettier `prod-test.yml` @ `d5d30c1` | 两个 matrix job **同时含 include 与 exclude**、`needs`、job 级 `if:`、step 级 matrix 比较、本地 `uses: ./…_build.yml` 与 SHA 固定 action 并存、6 个 job 中 2 个有 `timeout-minutes` |
| `unsafe-demo.yml` | 合成（头部已声明） | AL001–AL008 每条一个反例 + 一个合法对照 job（含真实 checkout SHA），matrix + include + 分类型 dispatch inputs + 自环 `needs` |

**尚未完成**：把这三位跑通「端到端分析报告」。原因是分析编排层（Scenario + Reporting）按计划属于 W3–W4，
目前只有 load → matrix → graph 三段可用。当前只能验证解析与建模不崩、诊断定位可用；完整 DAG/矩阵/状态
结论要等 W3 的编排层。**这一项不在 Gate 1 起手清单第 9 条的验收之内，但必须如实记录。**

顺带发现：Atlas 仓库现有 workflow **没有任何 `needs:`**，所以 dogfooding 样例在依赖图上是弱样本，
矩阵与依赖两类能力实际由 `matrix-build.yml` 覆盖。

## 4. 已知限制（本轮新增，非既有 MVP 限制）

1. 动态矩阵（`fromJSON` 维度）只报 unknown，不给出静态子积 —— 组合数不可知即不猜。
2. `hashFiles()` 一律 unknown（不读文件系统，见 ADR 0001）。
3. 祖先可能被 skip 时，`success()`/`failure()` 报 unknown：GitHub 文档在 `needs` 与
   「skipped 报 Success」两处表述互相冲突，且 job 级状态函数为服务端实现，无公开源码。
4. 表达式内部的诊断定位精确到 `if:` 整条值，未细化到子表达式列。当时的归因是「词法器给出的是文档
   绝对坐标，但 YAML 节点起点与表达式起点之间还需一次偏移换算」——**这个归因是错的**：根因是
   `ExpressionLexer.At()` 把 clamp 上界写成「最后一行起点」而非文本长度，连绝对坐标都没算对。
   2026-11-05 已修：`SourcePositionMap` 接受 `if:` 值的文件原点，token 与整棵 `unknown` 原因树
   都落在文件坐标上；见 `docs/review-v1-grill.md` 分支 A。
5. 触发过滤的逐事件合法 key 表（例如 `pull_request` 下 `tags:` 属非法 key）本轮只建模不校验，属 W3。
6. `App.Tests` 尚未引用 `ActionLens.App`：WASM 项目被测试项目引用需要先把组件下沉为 Razor 类库，
   该决策留给 W6，不在本轮伪造。
7. `docs/limitations.md`、`CONTRIBUTING.md`、`SECURITY.md`、`CHANGELOG.md` 尚未落盘（README 中的链接
   目前指向不存在的文件），留到 W7 文档阶段一并补齐。

## 5. 下一步建议

按 Gate 1 的判定规则（求值器/矩阵/DAG 不可信 → 砍场景模拟或 lint 数量，不进 UI），我的建议是
**不进入 W3 UI 相关工作，先做一次有针对性的表达式修正**，工作量估计 3–5 小时，顺序：

1. 词法器数字字面量对齐 runner（接受 `0x/0o/Infinity/NaN`），并同步修正
   `docs/expression-semantics.md` 中我写错的那条推断。
2. 决定 `.*` 通配索引：支持（推荐，成本集中在 AST 一处）或显式 unknown + 文档声明。
3. 逐例核对 `operators_precedence.json` 的 16 个失败，把结论固化成表驱动测试。
4. 修自有测试的 36 个失败；行列号约定一次定死（**值节点起点**），改断言前先确认实现正确性。
5. 复跑，目标：自有 142/142，语料 ≥ 950/1003，剩余差额逐条归类为「故意 unknown」。

矩阵与 DAG 已达可信赖水位；表达式层的问题集中在**词法形式**，不在三态语义骨架上 —— 这是好消息：
不需要推翻 ADR 0002 的设计。若修正后仍不达标，按闸门规则应削减场景模拟与 lint 条数，而不是带着
不可信的求值器进 UI。

---

## 6. 修正轮结果（Gate 1 复判依据）

复测数字（主代理独立执行，非代理自述）：`total 1145 / failed 0 / succeeded 1145`，
`dotnet build` 0 错误 0 警告，`dotnet format --verify-no-changes` 无输出。

### 6.1 语料的可信度核验

语料 1003 例并非「跳出来的绿」。按标记统计，被排除规则吸收的用例只有 **10 个**（约 1%）：
`Unrecognized named-value` 6、`Unrecognized function` 2、空 `${{ }}` body 1、runner 内存上限 1；
`DeferredFunctions`（status 函数 / `hashFiles`）命中 **0** 个 —— 语料本身不含状态函数用例。
其余 **993 例为真实断言**。修正轮结束后我复查了四条排除规则与 `IsDeferredEmptyBody`，未被加宽。

### 6.2 初判中我自己的错误，已被证据纠正

| 初判 | 证据 | 结论 |
|---|---|---|
| 「Actions 无 hex / `Infinity` / `NaN` 字面量」 | `coerce_number.json`、`number.json`：`' 0xff ' == 255`、`' Infinity ' == Infinity` 为 true | **我错**。词法器按 runner 接受这些形式，`docs/expression-semantics.md` 同步改掉那条推断 |
| 「不支持 `.*` 通配索引」 | `op_idx_star.json` 41 例 + `op_idx.json` 21 例 | **我错**。已建模 |
| 字符串关系比较「先转数字」 | runner `AbstractLessThan` 对 `(String,String)` 直接 `String.Compare(OrdinalIgnoreCase)`；语料 `'A'<'['` true | **我的测试错**：`'2'<'10'` 为 **false**，`'10'<'2'` 为 true |
| 矩阵 5 个 include/exclude 断言 | 文档六组合官方示例 | **我的测试错**（含一处 `Assert.Single` 与同函数内 `Assert.Equal(2,…)` 自相矛盾，以及 2×2×3=12 被我写成 18） |
| `@v4` 应报 `uses` 非法 | YAML 1.1 保留 `@` | **我的测试错**：它在 YAML 层就死（YAML001），改为加引号后仍断言 VAL005 |

### 6.3 修正轮发现并修掉的**实现**缺陷（3 处，均为真 bug）

1. `MatrixExpander.EntryMatches`：非法 exclude key 用 `continue` 跳过后返回 true，导致**一个拼错的 key
   删掉整个笛卡尔积**。现在非法 key 的条目匹配不到任何组合（该规则此前无文档明文，已在代码与语义文档中写明）。
2. `WorkflowModelBuilder.ReadMapping`：不在允许集合内的字段被直接丢弃，使得「job 级 `matrix:` 应提示
   挪到 `strategy:`」这条诊断成为**死代码**。现在错位 key 透传，且只报一条而不是两条诊断。
3. 命名级发现（未知字段）现在指向 **key 的位置**，而取值类字段仍统一为 **值节点起点** —— 这个区分此前
   未定，导致 3 个行列号断言各自漂移。约定已写进 `ARCHITECTURE.md`。

### 6.4 仍然存在的限制（进入 W3 前不需要解决，但必须知道）

- 语料中被排除的 10 例里，6 例是 `Unrecognized named-value`：GitHub 视其为**解析期拒绝**，ActionLens
  选择报 unknown + 原因树。这是有意的产品差异，但意味着「context 在此位置非法」这条能力**没有语料背书**，
  需要在 W4 用 AL004 的专门测试自己证明。
- 表达式内部诊断仍定位到 `if:` 整条值，未到子表达式列。
- `.*` 已建模，但通配结果与 `needs.*.result` 的场景联动要等 W3 的场景引擎。
- 起手清单第 9 条「三个 fixture 跑通端到端分析」在 Gate 1 时点尚未完成（编排层属 W3–W4）。**现已完成**：
  `tests/ActionLens.Core.Tests/EndToEnd/FixturesTests.cs` 对三个 fixture 断言完整 `AnalysisReport`。

### 6.5 复判

按闸门定义「求值器 / 矩阵 / DAG 是否可信」：三者现在都有可复现证据，**Gate 1 通过**，可以进入 W3
（场景引擎 + 触发过滤 + 报告模型），不需要削减场景模拟或 lint 条数。
建议 W3 的第一个动作就是把三个 fixture 跑通端到端，用它验证「unknown 与确定结果可区分」这条验收项。

