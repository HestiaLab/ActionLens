using ActionLens.Core.Diagnostics;
using ActionLens.Core.Text;
using ActionLens.Core.Workflows;
using YamlDotNet.RepresentationModel;

namespace ActionLens.Core.Yaml;

internal sealed partial class WorkflowModelBuilder
{
    // ---------------------------------------------------------------- strategy, defaults, concurrency

    private StrategySection? BuildStrategy(List<Entry> fields, string jobId)
    {
        var direct = Find(fields, "matrix");
        var entry = Find(fields, "strategy");
        if (direct is not null)
        {
            Add(DiagnosticCodes.UnknownField, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"'matrix' of job '{jobId}' must be nested under 'strategy'.", direct.KeyRange,
                $"Write 'strategy:' and put 'matrix:' under it.",
                "https://docs.github.com/en/actions/using-jobs/using-a-matrix-for-your-jobs");
        }

        if (entry?.Value is not YamlMappingNode map)
        {
            if (entry is not null)
            {
                Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                    $"'strategy' of job '{jobId}' must be a mapping with 'matrix', 'fail-fast' or 'max-parallel'.",
                    entry.ValueRange, "Write 'strategy:' followed by 'matrix:' and its dimensions.");
            }

            return entry is null ? null : new StrategySection { Sources = new SourceMap() };
        }

        var strategyFields = ReadMapping(map, new HashSet<string>(StringComparer.Ordinal) { "matrix", "fail-fast", "max-parallel" }, $"strategy of job '{jobId}'");
        var sources = new SourceMap();
        foreach (var field in strategyFields)
        {
            sources.Set(field.Name, field.ValueRange);
        }

        return new StrategySection
        {
            Matrix = BuildMatrix(strategyFields, jobId),
            FailFast = ReadOptionalBool(strategyFields, "fail-fast", jobId),
            MaxParallel = ReadOptionalInt(strategyFields, "max-parallel", jobId),
            Sources = sources,
        };
    }

    private bool? ReadOptionalBool(List<Entry> fields, string name, string scope)
    {
        var entry = Find(fields, name);
        if (entry?.Value is not YamlScalarNode scalar)
        {
            return null;
        }

        if (IsExpressionText(scalar.Value ?? string.Empty))
        {
            return null;
        }

        if ((scalar.Value ?? string.Empty) is "true" or "True" or "TRUE" or "false" or "False" or "FALSE")
        {
            return IsTrue((scalar.Value ?? string.Empty));
        }

        Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
            $"'{name}' of {scope} must be true or false.", RangeOf(scalar), "Use 'true' or 'false'.");
        return null;
    }

    private int? ReadOptionalInt(List<Entry> fields, string name, string scope)
    {
        var entry = Find(fields, name);
        if (entry?.Value is not YamlScalarNode scalar)
        {
            return null;
        }

        if (!int.TryParse((scalar.Value ?? string.Empty), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"'{name}' of {scope} must be a whole number.", RangeOf(scalar), "Use a number such as 4.");
            return null;
        }

        return parsed;
    }

    private MatrixSection? BuildMatrix(List<Entry> strategyFields, string jobId)
    {
        var entry = Find(strategyFields, "matrix");
        if (entry?.Value is not YamlMappingNode map)
        {
            return null;
        }

        var sources = new SourceMap();
        var dimensions = new List<MatrixDimension>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in ReadMapping(map, allowedFields: null, scope: $"matrix of job '{jobId}'"))
        {
            if (field.Name is "include" or "exclude")
            {
                sources.Set(field.Name, field.ValueRange);
                continue;
            }

            if (!seenNames.Add(field.Name))
            {
                Add(DiagnosticCodes.MatrixValueInvalid, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                    $"The matrix dimension '{field.Name}' appears more than once in job '{jobId}'.",
                    field.ValueRange,
                    "Matrix variable names are case-insensitive, so 'OS' and 'os' collide. Keep one of them.");
                continue;
            }

            dimensions.Add(BuildDimension(field, jobId));
        }

        var include = ReadMatrixEntries(MatrixEntryNodes(FindRaw(map, "include")), $"include of job '{jobId}'");
        var exclude = ReadMatrixEntries(MatrixEntryNodes(FindRaw(map, "exclude")), $"exclude of job '{jobId}'");

        return new MatrixSection
        {
            Dimensions = dimensions,
            Include = include,
            Exclude = exclude,
            Sources = sources,
        };
    }

    private MatrixDimension BuildDimension(Entry field, string jobId)
    {
        var name = field.Name;
        switch (field.Value)
        {
            case YamlSequenceNode sequence:
                return new MatrixDimension
                {
                    Name = name,
                    Values = sequence.Children.Select(ToWorkflowValue).ToArray(),
                    Range = field.ValueRange,
                };
            case YamlScalarNode scalar when IsExpressionText(scalar.Value ?? string.Empty):
                // A dimension fed by fromJSON(...) or an input is real, but its contents only exist at
                // runtime, so the expander reports it as dynamic instead of inventing values.
                return new MatrixDimension
                {
                    Name = name,
                    DynamicExpression = (scalar.Value ?? string.Empty),
                    Range = field.ValueRange,
                };
            case YamlScalarNode scalar:
                Add(DiagnosticCodes.MatrixInvalidShape, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                    $"The matrix dimension '{name}' of job '{jobId}' is the single value '{(scalar.Value ?? string.Empty)}'.",
                    field.ValueRange, $"Write '{name}:' followed by a list, for example '- {(scalar.Value ?? string.Empty)}'.");
                return new MatrixDimension { Name = name, Values = Array.Empty<WorkflowValue>(), Range = field.ValueRange };
            default:
                Add(DiagnosticCodes.MatrixInvalidShape, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                    $"The matrix dimension '{name}' of job '{jobId}' is {Describe(field.Value)}; it must be a list of values.",
                    field.ValueRange, $"Write a list under '{name}:'.");
                return new MatrixDimension { Name = name, Values = Array.Empty<WorkflowValue>(), Range = field.ValueRange };
        }
    }

    /// <summary>
    /// The nodes that are each one <c>include</c>/<c>exclude</c> entry. <see cref="FindRaw"/> returns the
    /// field's own value, and both fields are written as a list, so the entries are that list's children —
    /// handing the list over as if it were an entry made every real matrix look like one malformed entry and
    /// silently dropped all of them. A mapping written directly under the key stays the single entry it
    /// reads as.
    /// </summary>
    private static IReadOnlyList<YamlNode>? MatrixEntryNodes(IReadOnlyList<YamlNode>? value) =>
        value is null
            ? null
            : [.. value.SelectMany(node => node is YamlSequenceNode sequence
                ? sequence.Children
                : new YamlNode[] { node })];

    private IReadOnlyList<IReadOnlyDictionary<string, WorkflowValue>> ReadMatrixEntries(
        IReadOnlyList<YamlNode>? nodes, string scope)
    {
        if (nodes is null)
        {
            return Array.Empty<IReadOnlyDictionary<string, WorkflowValue>>();
        }

        var result = new List<IReadOnlyDictionary<string, WorkflowValue>>();
        foreach (var node in nodes)
        {
            if (node is not YamlMappingNode map)
            {
                Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                    $"An entry of {scope} must be a mapping of matrix keys to values.", RangeOf(node),
                    "Write '- os: linux' style entries.");
                continue;
            }

            var entry = new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);
            foreach (var pair in ReadMapping(map, allowedFields: null, scope))
            {
                entry[pair.Name] = ToWorkflowValue(pair.Value);
            }

            result.Add(entry);
        }

        return result;
    }

    private DefaultsSection ReadDefaults(List<Entry> fields)
    {
        var entry = Find(fields, "defaults");
        if (entry?.Value is not YamlMappingNode map)
        {
            return DefaultsSection.Empty;
        }

        var defaultsFields = ReadMapping(map, new HashSet<string>(StringComparer.Ordinal) { "run" }, "defaults");
        var runEntry = Find(defaultsFields, "run");
        if (runEntry?.Value is not YamlMappingNode runMap)
        {
            return DefaultsSection.Empty;
        }

        var runFields = ReadMapping(runMap, new HashSet<string>(StringComparer.Ordinal) { "shell", "working-directory" }, "defaults.run");
        var sources = new SourceMap();
        foreach (var field in runFields)
        {
            sources.Set(field.Name, field.ValueRange);
        }

        return new DefaultsSection
        {
            Shell = ReadOptionalString(runFields, "shell", "defaults.run", allowExpression: true),
            WorkingDirectory = ReadOptionalString(runFields, "working-directory", "defaults.run", allowExpression: true),
            Sources = sources,
        };
    }

    private ConcurrencySection? ReadConcurrency(List<Entry> fields)
    {
        var entry = Find(fields, "concurrency");
        if (entry is null)
        {
            return null;
        }

        if (entry.Value is YamlScalarNode scalar)
        {
            return new ConcurrencySection { Group = (scalar.Value ?? string.Empty), Sources = new SourceMap() };
        }

        if (entry.Value is not YamlMappingNode map)
        {
            Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"'concurrency' must be a group name or a mapping, but it is {Describe(entry.Value)}.",
                entry.ValueRange, "Write 'concurrency: my-group' or 'concurrency:' with 'group:' and 'cancel-in-progress:'.");
            return null;
        }

        var fields2 = ReadMapping(map, new HashSet<string>(StringComparer.Ordinal) { "group", "cancel-in-progress" }, "concurrency");
        var sources = new SourceMap();
        foreach (var field in fields2)
        {
            sources.Set(field.Name, field.ValueRange);
        }

        return new ConcurrencySection
        {
            Group = ReadOptionalString(fields2, "group", "concurrency", allowExpression: true) ?? string.Empty,
            CancelInProgress = ReadOptionalBool(fields2, "cancel-in-progress", "concurrency") ?? false,
            Sources = sources,
        };
    }
}
