# Expression semantics, with sources

Every rule ActionLens implements about GitHub Actions expressions is listed here with where it came
from. This exists because "I thought that's how it worked" is not a specification, and because two of
the three sources below disagree in places — where they do, the disagreement is recorded rather than
smoothed over.

Sources, in the order they are trusted:

1. **GitHub documentation** — [Expressions], [Contexts], [Workflow syntax], [Matrix], [Events that trigger workflows], [Usage limits].
2. **`actions/runner`** — the C# evaluator that actually runs workflows:
   `src/Sdk/DTExpressions2/Expressions2/**` (`EvaluationResult.cs`, `ExpressionConstants.cs`,
   `Tokens/Token.cs`, `Sdk/Functions/*.cs`), `src/Sdk/WorkflowParser/Conversion/{MatrixBuilder,WorkflowTemplateConverter}.cs`,
   `src/Runner.Worker/Expressions/*Function.cs`.
3. **`actions/languageservices`** — the TypeScript reference used by the editor, plus
   `expressions/testdata/*.json`, which its README describes as "test cases that all implementations
   should pass". That folder is vendored at commit `4043eda158e16579cc5fb1b0b07a4bce2a76f0b5` under
   `tests/golden/languageservices/` (MIT; see its README).

ActionLens follows the **C# runner** where the two implementations differ, because it is the same
runtime and it is the one that decides what a real workflow does.

## Values and coercion

| Rule | Source | Note |
|---|---|---|
| Types are `null`, `boolean`, `number` (IEEE-754 double), `string`, `object`, `array` | docs + `ValueKind.cs` | |
| To text: `null` → `""`, `true`/`false` → lowercase, number → `G15`, array → `"Array"`, object → `"Object"` | `ConvertToString()` + `coerce_string.json` | The docs say arrays and objects "are not converted to a string"; the implementations render the kind name. Followed the implementations, since that is what a step actually receives. |
| Truthy: everything except `false`, `0`, `-0`, `NaN`, `""`, `null` | docs falsy list + `EvaluationResult.IsFalsy` + `op_not.json` | `NaN` is falsy in the implementations but absent from the documented list. Arrays and objects are always truthy. |
| `"false"` and `"0"` are truthy strings | `op_not.json` | This is why `if:` values keep their YAML type. |
| To number: `null` → 0, booleans → 0/1, text parsed as a number else `NaN`, blank text → 0, composites → `NaN` | docs coercion table + `coerce_number.json` | Text parsing delegates to .NET `double.TryParse` with `NumberStyles.Float`, which is what the C# runner does — that is how `' Infinity ' == Infinity` and `' 0o10 ' == 8` come out true. |

## Comparison

| Rule | Source |
|---|---|
| `==` / `!=` are loose: mismatched scalar kinds both coerce to numbers, then compare | docs ("If the types do not match, GitHub coerces the type to a number") + `CoerceTypes` |
| Two strings compare case-insensitively, ordinal-ignore-case, **without** trimming whitespace | docs ("GitHub ignores case when comparing strings") + `operators_case_insensitive.json` |
| `NaN` in either operand makes any comparison false, including `NaN == NaN` | docs + `op_eq.json` |
| Objects and arrays compare equal only to themselves | docs prose + `op_eq.json` identity cases |
| `<`, `>`, `<=`, `>=` are not numeric-only: two strings compare case-insensitively, booleans order `false < true`, composites are never ordered | implementations + `op_lt.json` / `op_gt.json` |
| Two **strings** are never coerced to numbers for ordering, so the comparison is by character: `'2' < '10'` is false and `'10' < '2'` is true; `'B' < 'a'` folds to `'b'` vs `'a'` and is false | `EvaluationResult.AbstractLessThan` / `AbstractGreaterThan`, which branch on `(String, String)` to `String.Compare(..., OrdinalIgnoreCase)` before any number coercion; `op_lt.json` `'abc' < 'def'` true, `operators_case_insensitive.json` `'A' < '['` true |
| `<=` and `>=` are defined as equality-or-strict, so `'a' <= 'A'` is true and `{} >= {}` is true for the same instance | implementations |
| `3 < 2 < 1` is true, because it is `(3 < 2) < 1` → `0 < 1` | `operators_precedence.json` |

Worked cases pinned by tests: `1 == '1'` true · `'a' == 'A'` true · `'' == 0` true · `'0' == false` true
· `null == 0` true · `null == ''` true · `'   ' == 0` true · `'false' == null` false ·
`'null' == null` false · `NaN == NaN` false · `0xff == 255` true.

## Operators, precedence and results

Precedence is **not documented anywhere** — not on the live expressions page, not in the GHES
snapshot. It is taken from `Token.cs` and confirmed by `parser.ts` and `operators_precedence.json`,
which agree:

```text
20  ( )                                    grouping
19  [ ]  ( )  .                            index, call, member access
16  !                                      NOT, right-to-left
11  > >= < <=                              relational, left-to-right
10  == !=                                  equality, left-to-right
 6  &&                                     AND, left-to-right
 5  ||                                     OR, left-to-right
```

The consequence people get wrong: relational binds **tighter** than equality, so `a == b < c` is
`a == (b < c)`.

`&&` and `||` return an **operand**, not a boolean (`false || 0` is the number `0`), and short-circuit
on the falsy/truthy operand. Only the value handed back to `if:` is coerced to a condition
(`WorkflowTemplateConverter.ConvertToIfResult` → `IsTruthy`). ActionLens models the operand-returning
behaviour, which is what makes `unknown && false == false` correct rather than a lucky coincidence.

`||` and `&&` are n-ary in both implementations, so a chain of the same operator is flattened into one
node — a 60-operand chain does not consume parse depth.

## Functions

Names are case-insensitive. Arities come from `ExpressionConstants.cs` and match `funcs.ts`:
`contains(2,2)` `startsWith(2,2)` `endsWith(2,2)` `join(1,2)` `format(1,255)` `toJSON(1,1)`
`fromJSON(1,1)` `hashFiles(1,255)` `success(0,∞)` `failure(0,∞)` `always(0,0)` `cancelled(0,0)`
`case(3,∞)`.

| Function | Behaviour ActionLens implements | Source |
|---|---|---|
| `contains` | array: element-wise loose equality; string: case-insensitive substring, both sides must be primitives, each cast to text; object: false | docs + `contains.json` |
| `startsWith` / `endsWith` | cast both to text, case-insensitive, false if either side is not a primitive | docs + implementations |
| `format` | `{N}` substitution, `{{`/`}}` escape, every substituted value converted with the text coercion; one argument is legal | docs + `format.json` (`format('{0}', .5)` → `0.5`) |
| `join` | array elements each converted to text and joined (default separator `,`); a non-array primitive becomes its own text; an object becomes `""`; zero arguments is an arity error (`EXPR004`), not an internal failure | docs + `basic.json` join cases (`join(myArray)` → `…,Array,Object`) |
| `toJSON` | pretty-printed, two-space indent; `-0` becomes `0` | docs + `tojson.json` |
| `fromJSON` | argument converted to text first; invalid or empty JSON is an error, which ActionLens reports as unknown | docs + `fromJSON.json` |
| `hashFiles` | **always unknown** — it reads the checkout, which does not exist here | docs + ADR 0001 |
| `success/failure/cancelled` | scenario-dependent; unknown when the scenario cannot decide | `Runner.Worker/Expressions/*.cs` |
| `always` | `true` unconditionally, including after a cancellation | `AlwaysFunction.cs` returns `true` |
| `case` | odd argument count, predicates must be booleans, first true predicate wins, last argument is the default | docs (feature-gated to `fpt`/`ghec`) + `case.json` |

## `if:` specifically

- The `${{ }}` wrapper is optional in `if:` and required elsewhere; the value is trimmed before parsing.
  An empty or whitespace `if:` is treated as `success()`. (docs NOTE + `if-condition.ts`)
- A condition containing **no** status function anywhere in its tree is evaluated as
  `success() && (condition)`. Confirmed by the documented runtime log
  (`Evaluating: (success() && ((github.repository == '…')))`) and by `if-condition.ts`
  `ensureStatusFunction`. Detection walks the tree, so a status function in an untaken branch still
  suppresses the wrap.
- `!` at the very start of an `if:` value must be quoted or wrapped, because YAML reserves `!`.
- String literals inside expressions use single quotes only; `''` is an escaped quote; double quotes are
  an error. ActionLens un-escapes **every** `''`, following the C# runner — the TypeScript lexer only
  replaces the first occurrence.
- Limits: 21 000 characters of expression body and parser depth 50. Not documented; both implementations
  agree, and the corpus pins the boundary exactly (50 `!`s fail, 49 pass).

## Context availability

From [Contexts] `#context-availability`, which the page states is the normative list:

| Workflow key | Available contexts | Functions |
|---|---|---|
| `jobs.<id>.if` | `github`, `needs`, `vars`, `inputs` | `always` `cancelled` `success` `failure` |
| `jobs.<id>.steps[*].if` | `github`, `needs`, `strategy`, `matrix`, `job`, `runner`, `env`, `vars`, `steps`, `inputs` | the above plus `hashFiles` |

`secrets` is in neither row, and the docs state directly that secrets cannot be referenced in `if:`.
A disallowed context name is a parse-level rejection in the implementations
(`Unrecognized named-value: 'secrets'`), which is why AL004 is an error and not a warning.

`matrix` and `strategy` are **not** available in a job-level `if:` — the same page notes that
`jobs.<job_id>.if` is evaluated *before* the matrix is applied. The `matrix` context section elsewhere
says it can be reached "from any job or step", which contradicts the availability table; the table and
the schema are the narrower, normative sources, so ActionLens follows them.

Dereferencing a property that does not exist returns `null` in both implementations, while the docs say
"it will evaluate to an empty string". The two agree under `== ''`, `== 0` and string interpolation, and
disagree only under `toJSON()` or an explicit `== null`. ActionLens follows the implementations and
returns `null`.

## `needs`, status and skipping

- A job whose dependency failed **or was skipped** is skipped, unless a condition makes it continue
  ([Workflow syntax] `jobs.<job_id>.needs`).
- `needs.<id>.result` ∈ `success` `failure` `cancelled` `skipped` (verified live).
- At step level, `success()` looks at the **job** status, so steps skipped by their own `if:` do not
  make later steps fail (`StepsRunner.cs` only merges the job result for `TaskResult.Failed`).

## Matrix

- `exclude` first, then `include` — documented, and it is what lets `include` add a combination back.
- `exclude` matches partially, and it **does** reduce the job count (the docs' own example: nine jobs
  from twelve configurations). The claim that exclude never reduces jobs is wrong.
- An `exclude` key that is not a declared matrix variable makes the whole entry match **nothing**: it
  costs a diagnostic, not a slice of the product. An `exclude` entry describes a combination, so a clause
  no combination can satisfy cannot match any of them. (Not documented; the alternative reading — ignore
  the unknown key and match on the rest — silently deletes jobs on a typo.)
- An `include` entry's keys split into matrix-variable keys (a filter) and the rest (values to add).
- A filter that matches no original combination creates a new combination; matching only ever looks at
  the original cross product, never at combinations an earlier `include` appended. (The docs' own
  six-combination example is the source of both halves: `'{fruit: banana} cannot be added to any original
  matrix combination without overwriting a value, so it is added as an additional matrix combination'`, and
  the next entry `'does not add to the {fruit: banana} matrix combination because that combination was not
  one of the original matrix combinations'`.)
- An entry with no matrix-variable keys has an empty filter, which every combination satisfies — so its
  values are added to all of them. It never becomes a combination of its own: in the same example
  `{color: green}` is 'added to all of the original matrix combinations'.
- `include` may overwrite a value an earlier `include` added, but never a value from a dimension.
- Dimension names are case-insensitive, so `OS` and `os` collide and are an error.
- Maximum 256 combinations per workflow run, hosted and self-hosted alike ([Workflow syntax], [Usage limits]).
- Identical resulting combinations are **not** deduplicated; job names get `_2`, `_3` suffixes
  (`IdBuilder.cs`). Not documented, implementation-only.

## Trigger filters

- `branches` and `branches-ignore` (likewise `tags`/`paths`) cannot both be used for the same event.
- `branches`/`paths` are ANDed: the workflow runs only when both are satisfied.
- For `pull_request`, `branches` filters the **base** branch; the head branch needs `github.head_ref` in
  a condition. `tags` is not a valid key for `pull_request` at all.
- For `push`: defining only `tags` means branch pushes do not run, and vice versa; defining neither means
  both run. Path filters are not evaluated for tag pushes.
- `!` patterns negate, and order matters: a negative pattern after a positive match excludes, a positive
  pattern after a negative match includes again. A `!`-prefixed pattern requires at least one un-prefixed
  pattern alongside it.
- Glob: `*` does not cross `/`, `**` does; `?` and `+` apply to the preceding character; `[]` ranges are
  alphanumeric only; patterns starting with `*`, `[` or `!` must be quoted in YAML.
- `workflow_dispatch` accepts only `inputs`; `schedule` accepts only cron entries, in UTC unless a `timezone` is given.

### Filter pattern grammar, as `Triggers/FilterMatcher.cs` reads it

Every token is written out as a backtracking walk; there is no `Regex` translation on this path, because a
translation would have to re-derive the "does not cross `/`" rule, the quantified-preceding-character rule
and the alphanumeric-only ranges, and silently differing from the table is the failure mode to avoid.

- `*` — zero or more characters, never a `/`. `**` — zero or more, `/` included.
- `?` — zero or one of the **preceding** character; `+` — one or more of it. These are quantifiers, not
  single-character wildcards: `ab?c` matches `abc` and `ac`, `ab+c` matches `abc` and `abbc` but not `ac`.
- `[]` — one alphanumeric character, or an `a-z`, `A-Z` or `0-9` range. A reversed (`[z-a]`), cross-group
  (`[a-9]`), empty or unterminated class, or one holding a non-alphanumeric member, is not a valid pattern:
  it is reported where it sits in the YAML instead of being guessed at.
- A leading `!` negates; anywhere else it is literal. `\` escapes the next character, which is also how a
  literal leading `!` is written (`\!urgent`).
- A pattern is matched against the **whole** value — the ref name without its `refs/heads/` or `refs/tags/`
  prefix ("the patterns defined in `branches` are evaluated against the name of the Git ref"), or the whole
  path from the repository root. So `release/*` does not match `release/a/b`, and `**/README.md` does not
  match a root-level `README.md`.
- Patterns starting with `*`, `[` or `!` must be quoted in YAML. Not a style point: an unquoted
  `**/README.md` is an alias token and the file never loads as a workflow.

### Which filter reads what

- The include/ignore decision is made by the pattern that matched **last**: a negative match after a
  positive one excludes, a positive match after a negative one includes again. A value no pattern matches
  falls back to the list's own sense — a `branches` list refuses it, a `branches-ignore` list admits it.
- `paths` is satisfied by **any** changed file, and the report names the file and the pattern that admitted
  it. A `!` pattern requires at least one un-prefixed pattern in the same list.
- `pull_request` and `pull_request_target`: `branches` filters the base branch — the head branch needs
  `github.head_ref` in a condition, which no filter can reach — and there is no `tags` key for them at all.
- `workflow_dispatch` accepts only `inputs` and `schedule` only cron entries. The per-event key table stops
  there on purpose: any other key under any other event is read as a filter and evaluated, not declared
  illegal, so ActionLens never rejects a file the sources do not reject.
- `types` entries are exact activity-type names and cron entries are cron text: the glob grammar is not
  applied to either.
- Findings, all positioned at the pattern that caused them: `TRIG001` a mutually exclusive pair (reported at
  the list written second), `TRIG002` a key the event does not have, `TRIG003` a pattern that cannot be read
  or a list of nothing but negations, `TRIG004` the caller's event selection disagreeing with the payload's
  `event_name`.

### Path-filter limits — why several answers are forced to `unknown`

Sources: "Git diff comparisons" in [Workflow syntax] and "filter and diff limits" in
[Events that trigger workflows].

- More than 1,000 commits in a push, or a diff GitHub fails to generate before its timeout: "the workflow
  will always run". ActionLens answers `unknown` — "a filter let this through" would be the false claim, and
  a payload cannot show whether the limit was crossed for a run it did not record.
- A diff larger than the filter reads (3,000 files hosted, 300 on GHES): only that first set is read, and a
  matching file past the end means the workflow does *not* run. Which side of the line the match fell on is
  not in the payload, so `unknown`.
- No changed-files list in the payload: `unknown`, naming `changed_files` as the input that would settle it.
  An **empty** list is a stated fact rather than a gap, and gets a definite answer.
- A file list assembled from a webhook body's per-commit `added`/`modified`/`removed` arrays is capped at 20
  commits, so it is not the diff GitHub filtered against: `unknown`, with the partial result kept as the
  evidence the report shows.
- `schedule` needs a clock and `workflow_dispatch` a person; [ADR 0001](adr/0001-static-preflight-no-execution.md)
  forbids reading either, so both are `unknown` unless the payload records that event or supplies dispatch
  inputs. Because no firing time is computed here, the `timezone` key is not modelled: UTC or not, the
  answer is the same unknown.

### What the payload has to say

`github.event_name` selects the event when the caller does not; `github.ref` (or `ref_name`/`ref_type`)
gives the ref, `base_ref` / `pull_request.base.ref` the base branch, `github.event.action` the activity type
and `inputs` the dispatch inputs. Keys no webhook body carries, which a caller may add the way `vars` is
added for `AnalysisInput`: `changed_files` (a list of paths, or a count), `commit_count`,
`commits_over_limit`, `diff_timed_out`, `diff_truncated`, `ghes`. All are read case-sensitively, because
that is how JSON keys are. An explicit `triggerEvent` passed to `WorkflowAnalyzer.Analyze` takes priority
over `event_name`, and the disagreement is reported rather than preferred silently.

## Ambiguities that ActionLens answers with `unknown`

These are the points where no source settles the question. Each one is a deliberate `unknown`, not a
gap in the implementation. Items 9 to 15 are the trigger-filter ones: where the sources say nothing,
ActionLens either refuses the answer (the path-filter and clock cases above) or takes the conservative
reading and records it here rather than inventing a rule silently.

1. **`success()` after an ancestor was skipped.** The `needs` documentation implies the dependent job is
   skipped, while the required-status-checks documentation states that "a job that is skipped will
   report its status as Success" — opposite readings, and job-level status functions are evaluated
   server-side with no public implementation. ActionLens reports `unknown` whenever an ancestor's outcome
   can be `skipped`.
2. **`cancelled()` scope.** Docs say "if the workflow was canceled"; the step-level implementation
   compares the *job* status. Whether a sibling job's cancellation makes a still-running job's
   `cancelled()` true is not stated.
3. **`if:` that fails to parse or throws.** Undocumented for jobs. For steps, the implementation fails the
   step. ActionLens reports a diagnostic and leaves the result unknown.
4. **Number formatting at the extremes.** `G15` (C#) versus `toFixed(15)` (TS) agree on the values the
   corpus pins; `1e21`-scale magnitudes are not pinned and may differ between the two.
5. **`hashFiles` negation and argument limits** beyond the schema's arity, and its case-insensitivity
   being Windows-specific.
6. **Path-filter degenerate cases**: more than 1000 commits or a timed-out diff means the workflow
   *always* runs; more than 3000 matched files (300 on GHES) can mean it does *not* run; no changed files
   means it does not run. Any answer that depends on `paths` is therefore `unknown` without the payload.
7. **`case()` availability** is feature-gated to `fpt`/`ghec` and absent from GHES 3.12.
8. **Runner labels churn.** The documented set is a moving target and custom images are not listed, so an
   unrecognised label is a warning, never an error. `arms-class` appears in no official page or schema and
   is deliberately not treated as known.
9. **Case sensitivity of filter patterns.** Nothing in the sources says it for trigger filters — only
   `hashFiles` is documented as case-insensitive on Windows. ActionLens matches case-sensitively, the
   reading the documented character classes imply (`[a-z]` and `[A-Z]` are different ranges, so the table
   never grants a folded match). The direction of the risk is deliberate: a pattern differing from the ref
   only in case reports "did not run", which can never claim a run GitHub would not have made.
10. **A star run longer than two.** The table lists `*` and `**` only, and says nothing about `***`. It is
    read as `**` — the same token — rather than rejected, because inventing a rejection needs a source.
11. **Negated character classes** (`[!aeiou]`). The `[]` entry is limited to alphanumeric characters and
    ranges, so a complement is not in the grammar; ActionLens reports such a class as an invalid pattern
    instead of inventing the complement.
12. **A list of nothing but `!` patterns.** The docs state the requirement ("a `!` pattern requires at least
    one un-prefixed pattern") without spelling out the consequence. ActionLens reports it as an error, the
    same treatment as the mutually exclusive pair, and the list then admits nothing, so the verdict is a
    definite "did not run" rather than a guess.
13. **`paths-ignore` with an empty changed-file list.** "If there are no files changed, the workflow will
    not run" is written for the path filter as a whole, while an exclusion list with nothing to exclude has
    stopped nothing. ActionLens follows the exclusion semantics (it runs) because the other reading would
    let an absent diff veto a workflow that declared only exclusions.
14. **`tags` under `pull_request_target`.** The illegality is named for `pull_request`; the two events are
    documented with the same filter list, so the same error is reported for both. No source states it for
    `pull_request_target` in its own words.
15. **A pattern too expensive to walk.** The matcher backtracks under a step budget instead of a regex, so a
    pathological pattern can exhaust it; ActionLens then reports no match. The claim is never "it matched",
    and the file's own findings still carry the pattern's position.

## Deliberate deviations, stated

- Bare `y`/`n` are left as strings rather than becoming booleans, unlike the YAML 1.1 table (see
  `YamlScalarResolver`).
- Expression limits (21000 / depth 50) are enforced from the implementations although the docs do not
  state them, because a file that GitHub would reject must not be analysed as if it were fine.
- Parse and evaluation errors never surface as exceptions: every failure becomes a positioned diagnostic
  and, where it matters, an `unknown`.

[Expressions]: https://docs.github.com/en/actions/reference/workflows-and-actions/expressions
[Contexts]: https://docs.github.com/en/actions/reference/workflows-and-actions/contexts
[Workflow syntax]: https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax
[Matrix]: https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/run-job-variations
[Events that trigger workflows]: https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows
[Usage limits]: https://docs.github.com/en/actions/reference/limits
