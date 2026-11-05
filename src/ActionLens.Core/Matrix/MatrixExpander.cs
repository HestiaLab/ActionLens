using ActionLens.Core.Diagnostics;
using ActionLens.Core.Expressions;
using ActionLens.Core.Text;
using ActionLens.Core.Values;
using ActionLens.Core.Workflows;

namespace ActionLens.Core.Matrix;

/// <summary>One expanded matrix combination, with the values that name it.</summary>
public sealed record MatrixCombination
{
    /// <summary>Position within the expansion, used to label jobs and matrix rows.</summary>
    public required int Index { get; init; }

    /// <summary>Matrix keys as the author wrote them. Lookups are case-insensitive, as in Actions.</summary>
    public required IReadOnlyDictionary<string, WorkflowValue> Values { get; init; }

    /// <summary>Keys contributed by <c>include</c> rather than by the cross product of the dimensions.</summary>
    public IReadOnlySet<string> AddedKeys { get; init; } = NoAddedKeys;

    /// <summary>True when every value came from a declared dimension.</summary>
    public bool IsPureProduct => AddedKeys.Count == 0;

    private static IReadOnlySet<string> NoAddedKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The <c>matrix</c> context object this combination exposes to expressions.</summary>
    public GitHubValue ToContextValue()
    {
        var members = new Dictionary<string, GitHubValue>(StringComparer.Ordinal);
        foreach (var pair in Values)
        {
            members[pair.Key] = pair.Value.Value;
        }

        return GitHubValue.FromObject(members);
    }

    /// <summary>A short label for the matrix table and for job names, e.g. <c>ubuntu, 22.04</c>.</summary>
    public string DisplayName => Values.Count == 0
        ? "(no matrix values)"
        : string.Join(", ", Values.Select(pair => pair.Value.Text));
}

/// <summary>
/// The result of expanding one job's matrix: the combinations that will run, or an honest statement
/// that the answer only exists at runtime.
/// </summary>
public sealed record MatrixExpansion
{
    public required IReadOnlyList<MatrixCombination> Combinations { get; init; }

    /// <summary>True when a dimension is fed by an expression such as <c>fromJSON(needs.setup.outputs.matrix)</c>.</summary>
    public bool IsDynamic { get; init; }

    /// <summary>Names of the dimensions whose values only exist at runtime.</summary>
    public IReadOnlyList<string> DynamicDimensions { get; init; } = Array.Empty<string>();

    /// <summary>True when more combinations were produced than Actions allows and the tail was dropped.</summary>
    public bool Truncated { get; init; }

    /// <summary>How many combinations the expansion produced before the cap was applied.</summary>
    public int ProducedCount { get; init; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Array.Empty<Diagnostic>();

    /// <summary>How many jobs this one job turns into. Null when only a run can say.</summary>
    public int? JobCount => IsDynamic ? null : Combinations.Count;

    /// <summary>A job with no matrix still runs exactly once, with no matrix values.</summary>
    public static MatrixExpansion NoMatrix { get; } = new()
    {
        Combinations =
        [
            new MatrixCombination
            {
                Index = 0,
                Values = new Dictionary<string, WorkflowValue>(StringComparer.OrdinalIgnoreCase),
            },
        ],
    };
}

/// <summary>
/// Expands <c>strategy.matrix</c> the way Actions does, including the <c>include</c> behaviours that
/// people most often get wrong.
/// </summary>
/// <remarks>
/// Rules implemented here, each pinned by tests in <c>MatrixExpanderTests</c>:
/// <list type="bullet">
///   <item><c>exclude</c> is applied first, and a partial match removes a combination — exclusions do
///     reduce the job count;</item>
///   <item>an <c>exclude</c> key that is not a declared dimension makes the entry match nothing, so a
///     typo there costs a diagnostic, not a slice of the product;</item>
///   <item>an <c>include</c> entry's keys split into those naming a matrix variable (a filter) and those
///     that do not (values to add);</item>
///   <item>a filter that matches nothing creates a new combination instead of being dropped;</item>
///   <item>an entry with no matrix-variable keys has an empty filter, so it adds its values to every
///     combination;</item>
///   <item>matching only ever looks at the original cross product, never at combinations an earlier
///     <c>include</c> appended;</item>
///   <item><c>include</c> may overwrite a value an earlier <c>include</c> added, but never a value that
///     came from a dimension;</item>
///   <item>the product is capped at 256 combinations per workflow run, hosted and self-hosted alike.</item>
/// </list>
/// </remarks>
public static class MatrixExpander
{
    /// <summary>The documented ceiling on matrix combinations per workflow run.</summary>
    public const int MaxCombinations = 256;

    /// <summary>
    /// A memory bound of our own, deliberately larger than <see cref="MaxCombinations"/> so that
    /// <c>exclude</c> and <c>include</c> still see the whole product for every realistic workflow. Ten
    /// dimensions of ten values each used to allocate ten billion <see cref="CombinationBuilder"/>
    /// objects before the 256 cap was applied; the expansion is now abandoned at this bound instead.
    /// </summary>
    internal const int MaxGeneratedCombinations = 4096;

    public static MatrixExpansion Expand(StrategySection? strategy, MatrixSection? matrix, SourceRange fallbackRange)
    {
        var diagnostics = new List<Diagnostic>();

        // fail-fast and max-parallel decide cancellation and scheduling once a run is under way. Reading
        // the keys and then dropping them would let the report look complete while silently ignoring
        // part of the file, so say plainly that they are outside the model.
        if (strategy is { FailFast: false } || strategy?.MaxParallel is not null)
        {
            var key = strategy?.MaxParallel is not null ? "max-parallel" : "fail-fast";
            var declared = strategy?.Sources.Get(key) ?? SourceRange.Unknown;
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.MatrixSchedulingNotModelled,
                Severity = DiagnosticSeverity.Info,
                Stage = DiagnosticStage.Matrix,
                Message = $"'{key}' controls the order and cancellation of matrix runs, which only a run can show. ActionLens reports which jobs would start, not how the run is scheduled, so this key is not modelled.",
                Range = declared.IsKnown ? declared : fallbackRange,
                Suggestion = "Read this key as a runtime concern; the job counts above are for the default scenario.",
            });
        }

        if (matrix is null)
        {
            return MatrixExpansion.NoMatrix with { Diagnostics = diagnostics };
        }

        var dynamicDimensions = matrix.Dimensions.Where(dimension => dimension.Values is null).ToArray();
        if (dynamicDimensions.Length > 0)
        {
            foreach (var dimension in dynamicDimensions)
            {
                diagnostics.Add(new Diagnostic
                {
                    Code = DiagnosticCodes.MatrixDynamic,
                    Severity = DiagnosticSeverity.Warning,
                    Stage = DiagnosticStage.Matrix,
                    Message = $"The matrix dimension '{dimension.Name}' is resolved at runtime.",
                    Range = dimension.Range.IsKnown ? dimension.Range : fallbackRange,
                    Suggestion = "Paste the JSON the producing job will output to see the combinations; ActionLens will not guess them.",
                });
            }

            // A partial product would be a subset of the truth with a wrong count, which is worse than
            // saying nothing.
            return new MatrixExpansion
            {
                Combinations = Array.Empty<MatrixCombination>(),
                IsDynamic = true,
                DynamicDimensions = dynamicDimensions.Select(dimension => dimension.Name).ToArray(),
                Diagnostics = diagnostics,
            };
        }

        foreach (var dimension in matrix.Dimensions.Where(entry => entry.Values is { Count: 0 }))
        {
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.MatrixValueInvalid,
                Severity = DiagnosticSeverity.Warning,
                Stage = DiagnosticStage.Matrix,
                Message = $"The matrix dimension '{dimension.Name}' has no values, so this job expands to nothing.",
                Range = dimension.Range.IsKnown ? dimension.Range : fallbackRange,
                Suggestion = $"Add at least one value under '{dimension.Name}:'.",
            });
        }

        var variables = new HashSet<string>(matrix.Dimensions.Select(dimension => dimension.Name), StringComparer.OrdinalIgnoreCase);
        var estimated = EstimatedProduct(matrix.Dimensions);
        if (estimated > MaxGeneratedCombinations)
        {
            // A prefix with a confident-looking count is a guess, which is worse than saying nothing.
            // Refuse the expansion before allocating a single CombinationBuilder, as the dynamic
            // dimension case does.
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.MatrixCapReached,
                Severity = DiagnosticSeverity.Error,
                Stage = DiagnosticStage.Matrix,
                Message = $"The matrix dimensions multiply to {estimated} combinations, far above the {MaxCombinations} Actions allows and the {MaxGeneratedCombinations} ActionLens will materialise. No combinations were expanded.",
                Range = matrix.Sources.Get("matrix").IsKnown ? matrix.Sources.Get("matrix") : fallbackRange,
                Suggestion = "Reduce the number of values per dimension, or split the work across jobs.",
            });
            return new MatrixExpansion
            {
                Combinations = Array.Empty<MatrixCombination>(),
                Truncated = true,
                ProducedCount = 0,
                Diagnostics = diagnostics,
            };
        }

        var working = CrossProduct(matrix.Dimensions);
        working = ApplyExclude(working, matrix.Exclude, variables, diagnostics);
        working = ApplyInclude(working, matrix.Include, variables, diagnostics, fallbackRange);

        if (working.Count == 0 && matrix.Dimensions.Count == 0 && matrix.Include.Count == 0)
        {
            // 'matrix: {}' with nothing under it still runs the job once.
            working.Add(new CombinationBuilder());
        }

        var produced = working.Count;
        var truncated = produced > MaxCombinations;
        if (truncated)
        {
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.MatrixCapReached,
                Severity = DiagnosticSeverity.Error,
                Stage = DiagnosticStage.Matrix,
                Message = $"The matrix expands to {produced} combinations, above the {MaxCombinations} that Actions allows per run.",
                Range = matrix.Sources.Get("matrix").IsKnown ? matrix.Sources.Get("matrix") : fallbackRange,
                Suggestion = "Reduce the number of values per dimension, or split the work across jobs.",
            });
        }

        var kept = truncated ? working.Take(MaxCombinations) : working;
        return new MatrixExpansion
        {
            Combinations = kept.Select((builder, index) => builder.ToCombination(index)).ToArray(),
            Truncated = truncated,
            ProducedCount = produced,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// Mutable during expansion only: combinations are updated in place by <c>include</c>, and identity
    /// has to be preserved so that two combinations holding the same values are not conflated.
    /// </summary>
    private sealed class CombinationBuilder
    {
        internal Dictionary<string, WorkflowValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal HashSet<string> Added { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Keys that came from the dimensions; include is never allowed to replace these.</summary>
        internal HashSet<string> ProductKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal MatrixCombination ToCombination(int index) => new()
        {
            Index = index,
            Values = new Dictionary<string, WorkflowValue>(Values, StringComparer.OrdinalIgnoreCase),
            AddedKeys = new HashSet<string>(Added, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// The size of the cross product before <c>exclude</c>/<c>include</c>, computed with arithmetic
    /// rather than by building it. Saturating at <see cref="long.MaxValue"/> keeps a hostile file from
    /// overflowing the estimate into a small number that would look safe.
    /// </summary>
    private static long EstimatedProduct(IReadOnlyList<MatrixDimension> dimensions)
    {
        if (dimensions.Count == 0)
        {
            return 1;
        }

        long product = 1;
        foreach (var dimension in dimensions)
        {
            var count = dimension.Values?.Count ?? 0;
            if (count == 0)
            {
                return 0;
            }

            product = product > long.MaxValue / count ? long.MaxValue : product * count;
        }

        return product;
    }

    /// <summary>
    /// The cross product of the dimensions, with the first dimension varying slowest so the list reads
    /// like the YAML. A dimension with no values collapses the product to nothing, as Actions does.
    /// </summary>
    /// <remarks>
    /// Called only after <see cref="EstimatedProduct"/> has established that the product fits inside
    /// <see cref="MaxGeneratedCombinations"/>, so this loop cannot be asked to allocate a billion rows.
    /// </remarks>
    private static List<CombinationBuilder> CrossProduct(IReadOnlyList<MatrixDimension> dimensions)
    {
        var result = new List<CombinationBuilder>();
        if (dimensions.Count == 0)
        {
            return result;
        }

        if (dimensions.Any(dimension => dimension.Values is { Count: 0 }))
        {
            return result;
        }

        var indexes = new int[dimensions.Count];
        while (true)
        {
            var builder = new CombinationBuilder();
            for (var i = 0; i < dimensions.Count; i++)
            {
                var dimension = dimensions[i];
                builder.Values[dimension.Name] = dimension.Values![indexes[i]];
                builder.ProductKeys.Add(dimension.Name);
            }

            result.Add(builder);

            var position = dimensions.Count - 1;
            while (position >= 0)
            {
                indexes[position]++;
                if (indexes[position] < dimensions[position].Values!.Count)
                {
                    break;
                }

                indexes[position] = 0;
                position--;
            }

            if (position < 0)
            {
                return result;
            }
        }
    }

    private static List<CombinationBuilder> ApplyExclude(
        List<CombinationBuilder> combinations,
        IReadOnlyList<IReadOnlyDictionary<string, WorkflowValue>> exclude,
        HashSet<string> variables,
        List<Diagnostic> diagnostics)
    {
        foreach (var entry in exclude)
        {
            if (entry.Count == 0)
            {
                diagnostics.Add(new Diagnostic
                {
                    Code = DiagnosticCodes.MatrixInvalidShape,
                    Severity = DiagnosticSeverity.Error,
                    Stage = DiagnosticStage.Matrix,
                    Message = "An 'exclude' entry is empty, which would remove every combination.",
                    Suggestion = "Give the entry at least one matrix key and value to match.",
                });
                continue;
            }

            foreach (var key in entry.Keys.Where(name => !variables.Contains(name)))
            {
                diagnostics.Add(new Diagnostic
                {
                    Code = DiagnosticCodes.MatrixInvalidShape,
                    Severity = DiagnosticSeverity.Error,
                    Stage = DiagnosticStage.Matrix,
                    Message = $"The 'exclude' key '{key}' is not a matrix variable, so it can never match a combination.",
                    Range = entry[key].Range,
                    Suggestion = variables.Count == 0
                        ? "'exclude' needs at least one declared matrix dimension to refer to."
                        : $"Use one of the declared dimensions: {string.Join(", ", variables)}.",
                });
            }

            combinations.RemoveAll(combination => EntryMatches(combination, entry, variables));
        }

        return combinations;
    }

    private static List<CombinationBuilder> ApplyInclude(
        List<CombinationBuilder> combinations,
        IReadOnlyList<IReadOnlyDictionary<string, WorkflowValue>> include,
        HashSet<string> variables,
        List<Diagnostic> diagnostics,
        SourceRange fallbackRange)
    {
        // Only the original cross product is eligible for matching; combinations appended by an earlier
        // include are never a match target, which is the half of the rule people most often invert.
        var originals = combinations.ToList();
        var appended = new List<CombinationBuilder>();

        foreach (var entry in include)
        {
            // The model keeps include keys with an Ordinal comparer (YAML keys are case-sensitive), so
            // `OS:` and `os:` can coexist in one entry. Rebuilding the filter/extra split with a
            // case-insensitive ToDictionary used to throw on that perfectly readable input.
            var filter = SplitEntry(entry, pair => variables.Contains(pair.Key), diagnostics, fallbackRange);
            var extras = SplitEntry(entry, pair => !variables.Contains(pair.Key), diagnostics, fallbackRange);

            // An empty filter matches every combination, so the extras are added to all of them.
            var matches = filter.Count == 0
                ? originals
                : originals.Where(combination => FilterMatches(combination, filter)).ToList();

            if (matches.Count == 0)
            {
                var created = new CombinationBuilder();
                foreach (var pair in entry)
                {
                    created.Values[pair.Key] = pair.Value;
                    created.Added.Add(pair.Key);
                }

                appended.Add(created);
                continue;
            }

            foreach (var match in matches)
            {
                foreach (var extra in extras)
                {
                    match.Values[extra.Key] = extra.Value;
                    match.Added.Add(extra.Key);
                }
            }
        }

        return combinations.Concat(appended).ToList();
    }

    /// <summary>
    /// Rebuilds one half of an <c>include</c> entry with Actions' case-insensitive key lookup. Two keys
    /// that differ only by case are reported once and the first spelling wins, so a readable workflow
    /// cannot take the analysis down with a dictionary-key exception.
    /// </summary>
    private static Dictionary<string, WorkflowValue> SplitEntry(
        IReadOnlyDictionary<string, WorkflowValue> entry,
        Func<KeyValuePair<string, WorkflowValue>, bool> selected,
        List<Diagnostic> diagnostics,
        SourceRange fallbackRange)
    {
        var result = new Dictionary<string, WorkflowValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in entry.Where(selected))
        {
            if (result.TryAdd(pair.Key, pair.Value))
            {
                continue;
            }

            var range = pair.Value.Range.IsKnown ? pair.Value.Range : fallbackRange;
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.MatrixInvalidShape,
                Severity = DiagnosticSeverity.Error,
                Stage = DiagnosticStage.Matrix,
                Message = $"An 'include' entry declares '{pair.Key}' more than once with keys that differ only by case; Actions reads matrix keys case-insensitively, so only the first spelling is used.",
                Range = range,
                Suggestion = "Use one spelling for each matrix key throughout the workflow.",
            });
        }

        return result;
    }

    /// <summary>
    /// Exclude matching: every key the entry names has to agree with the combination, and keys the entry
    /// does not name are ignored. That partial match is what removes several combinations at once.
    /// </summary>
    /// <remarks>
    /// A key the entry names that is <em>not</em> a matrix variable makes the entry match nothing, not
    /// everything: an <c>exclude</c> entry describes a combination, so an unsatisfiable clause means the
    /// whole clause set is unsatisfied and no combination is removed. Treating the unknown key as
    /// 'nothing to check' instead would silently delete whole slices of the product on a typo — the
    /// opposite of the diagnostic that names that key.
    /// </remarks>
    private static bool EntryMatches(
        CombinationBuilder combination,
        IReadOnlyDictionary<string, WorkflowValue> entry,
        HashSet<string> variables)
    {
        foreach (var pair in entry)
        {
            if (!variables.Contains(pair.Key))
            {
                return false;
            }

            if (!combination.Values.TryGetValue(pair.Key, out var actual)
                || !ExpressionCoercion.AreEqual(actual.Value, pair.Value.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FilterMatches(CombinationBuilder combination, IReadOnlyDictionary<string, WorkflowValue> filter) =>
        filter.All(pair => combination.Values.TryGetValue(pair.Key, out var actual)
            && ExpressionCoercion.AreEqual(actual.Value, pair.Value.Value));
}
