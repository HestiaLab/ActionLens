# Official GitHub Actions expression golden corpus

Vendored test data from the **`actions/languageservices`** repository — the
reference implementation of the GitHub Actions expression language. The repo's
`expressions/README.md` states that `./testdata` "contains test cases that all
implementations should pass", which is why ActionLens/Gate 1 validation treats
these files as golden expected values rather than fixtures we may freely edit.

## Provenance

| Item | Value |
| --- | --- |
| Source repository | https://github.com/actions/languageservices |
| Source directory | `expressions/testdata/` |
| Pinned commit SHA | `4043eda158e16579cc5fb1b0b07a4bce2a76f0b5` |
| Retrieval date | 2026-11-04 |
| Retrieval method | `https://raw.githubusercontent.com/actions/languageservices/4043eda158e16579cc5fb1b0b07a4bce2a76f0b5/expressions/testdata/<file>` |
| License | MIT (Copyright GitHub) — see `LICENSE.actions-languageservices` (fetched from the repo root at the same SHA) |

**License conclusion (read from the text, not assumed):** this is the standard
MIT license. Vendoring is permitted; the only condition is that "the above
copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software". `LICENSE.actions-languageservices` kept
alongside these JSON files satisfies that attribution requirement. The MIT text
imposes no NOTICE file obligation. No other part of the repo was copied.

## Contents

27 JSON files, 1,003 test cases in total (counted as the sum of cases across
all group arrays). Each file targets one operator, function, or coercion
family: `basic`, `case`, `coerce_boolean`, `coerce_number`, `coerce_string`,
`contains`, `endsWith`, `format`, `fromJSON`, `number`, `op_and`,
`op_dot`, `op_eq`, `op_gt`, `op_gte`, `op_idx`, `op_idx_star`, `op_lt`,
`op_lte`, `op_ne`, `op_not`, `op_or`, `operators_case_insensitive`,
`operators_precedence`, `startsWith`, `syntax-errors`, `tojson`.
There is no `join` file in this pinned commit; `join()` cases live in
`basic.json` and are covered there.

Caveat: `coerce_boolean.json` is a stub — one group named
`"refer ./operator_not.json"` with an empty case array (verbatim upstream
content; the referenced file is actually named `op_not.json`).

## Test-case schema

Verified by reading `basic.json`, `op_eq.json`, `coerce_string.json`,
`syntax-errors.json`, `case.json`, `number.json`, `tojson.json`, and `op_idx.json`.

Top level is a JSON object: **group name → array of cases**. Group names are
human-readable labels, sometimes with spaces (e.g. `"context access with
wildcard"` in `basic.json`) and are not semantically load-bearing.

Each case object has:

- **`expr`** (always): the expression text to evaluate — the bare expression
  *without* the `${{ }}` wrapper, e.g. `"1 == 2"`, `"fromJSON('{\"key\":\"value\"}')"`.
- **`contexts`** (optional): an object of fixture context data, keyed by the
  context root name used in `expr` (e.g. `{"simple": "foo", "simPLE": {"teST": 123}}`).
  Values are ordinary JSON (strings, numbers, booleans, nested objects/arrays);
  expression evaluation resolves them case-insensitively.
- Exactly one outcome, either:
  - **`result`**: `{"kind": <type>, "value": <json>}` where `kind` is one of
    `Null`, `Boolean`, `Number`, `String`, `Object`, `Array`. `value` is encoded
    in native JSON for its type: `null`, `true/false`, JSON numbers (e.g.
    `123.0`, `-0.0`), strings (newlines JSON-escaped, as in the multi-line
    `toJson` output in `basic.json`), and **`Object`/`Array` results carry the
    actual nested JSON value** (e.g.
    `{"kind": "Object", "value": {"key": "value"}}` in `tojson.json`;
    `{"kind": "Array", "value": ["one", "two"]}` in `op_idx.json`).
  - **`err`**: `{"kind": <phase>, "value": <exact message string>}` where
    `kind` is one of `lexing` (20 cases), `parsing` (35), or `evaluation` (23).
    `value` is the *exact expected error text*, which typically embeds the
    offending symbol and 1-based position, e.g.
    `{"kind": "parsing", "value": "Unexpected symbol: ')'. Located at position 5 within expression: 1 < )"}`
    (`syntax-errors.json`) or the positional-free
    `{"kind": "evaluation", "value": "case predicate must evaluate to a boolean value"}`
    (`case.json`). Error files: `basic`, `case`, `format`, `number`, `op_dot`,
    `startsWith`, `syntax-errors`.

Notes for implementers: `NaN`/`Infinity` appear only inside `expr` text, never
as JSON `result` values; there is no field distinguishing "no `contexts`"
from an empty one (the key is simply absent); a case never has both `result`
and `err`.
