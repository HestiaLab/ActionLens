using System.Text.RegularExpressions;
using ActionLens.Core.Diagnostics;
using ActionLens.Core.Text;
using ActionLens.Core.Values;
using ActionLens.Core.Workflows;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ActionLens.Core.Yaml;

/// <summary>
/// Turns a YAML node graph into <see cref="WorkflowDocument"/> plus positioned diagnostics.
/// </summary>
/// <remarks>
/// Three rules shape everything here. First, nothing throws: an unusable node becomes a diagnostic and
/// the walk continues as far as it can, because a workflow that does not fully parse is exactly the
/// input people most want explained. Second, YAML merge keys are resolved by this class instead of by
/// <c>MergingParser</c>, which reports a mapping that both merges an anchor and overrides one of its
/// keys as a duplicate key — an extremely common workflow shape that is not a duplicate at all.
/// <para>
/// Third, one position convention: a recorded field range is its <em>value</em> node
/// (<see cref="SourceMap.Get"/> returns where the value starts), so a finding about a value points at
/// that value. A finding about a <em>name</em> — an unknown or mis-nested key — points at the key node
/// instead, because the word GitHub did not accept is what the author has to look at.
/// </para>
/// </remarks>
internal sealed partial class WorkflowModelBuilder
{
    /// <summary>Bounds how far <c>&lt;&lt;</c> chains are followed, in case anchors are reused through several layers.</summary>
    private const int MaxMergeDepth = 16;

    private static readonly Regex JobIdPattern = new(@"^[_a-zA-Z][-_a-zA-Z0-9]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StepIdPattern = new(@"^[-_a-zA-Z][-_a-zA-Z0-9]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RemoteActionPattern = new(
        @"^[A-Za-z0-9._-]+/[A-Za-z0-9._/-]+@[A-Za-z0-9._-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> TopLevelFields = new(StringComparer.Ordinal)
    {
        "name", "run-name", "env", "defaults", "concurrency", "permissions", "timeout-minutes", "jobs",
    };

    private static readonly HashSet<string> JobFields = new(StringComparer.Ordinal)
    {
        "name", "needs", "if", "runs-on", "strategy", "steps", "env", "defaults", "outputs",
        "timeout-minutes", "concurrency", "permissions", "container", "services", "uses", "with",
        "secrets", "environment",
    };

    /// <summary>
    /// The keys of <c>jobs.&lt;job_id&gt;.steps.&lt;step_id&gt;</c>. <c>with</c> belongs here: the workflow
    /// syntax page lists <c>jobs.&lt;job_id&gt;.steps.&lt;step_id&gt;.with</c>, and <see cref="StepDefinition"/>
    /// reads it — without it every real action call was reported as an unknown property, which blocked the
    /// whole file.
    /// </summary>
    private static readonly HashSet<string> StepFields = new(StringComparer.Ordinal)
    {
        "id", "name", "if", "env", "uses", "run", "shell", "working-directory", "continue-on-error",
        "timeout-minutes", "with",
    };

    /// <summary>
    /// A <c>matrix:</c> written directly on a job is not a job property, but it is also not an unknown
    /// word: it is <c>strategy.matrix</c> one level too high, and that is the only useful thing to say
    /// about it. <see cref="BuildStrategy"/> owns the report, so the entry survives
    /// <see cref="ReadMapping"/> for it to find.
    /// </summary>
    private static readonly HashSet<string> MisplacedJobFields = new(StringComparer.Ordinal) { "matrix" };

    /// <summary>
    /// GitHub reads workflow files with a YAML 1.1 parser, where a plain <c>on</c> is the boolean
    /// <c>true</c>. The key therefore arrives under several spellings and all of them mean the same
    /// section; quoting it is the author's choice, not a correction.
    /// </summary>
    private static readonly HashSet<string> TriggerFieldSpellings = new(StringComparer.OrdinalIgnoreCase)
    {
        "on", "true", "yes", "y",
    };

    private readonly List<Diagnostic> _diagnostics = [];

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public WorkflowDocument? Build(YamlNode? root, string? fileName)
    {
        if (root is null)
        {
            Add(DiagnosticCodes.YamlSyntax, DiagnosticSeverity.Error, DiagnosticStage.Yaml,
                "The file contains no YAML document.", SourceRange.Unknown,
                "Add a workflow to the file, or paste the workflow you want checked.");
            return null;
        }

        if (root is not YamlMappingNode rootMap)
        {
            Add(DiagnosticCodes.YamlRootNotMapping, DiagnosticSeverity.Error, DiagnosticStage.Yaml,
                $"The top level of the file is a {Describe(root)}, but a workflow file must be a mapping.",
                RangeOf(root), "Start the file with a key such as 'name:' or 'jobs:'.");
            return null;
        }

        var fields = ReadMapping(rootMap, TopLevelFields, "workflow");
        var sources = new SourceMap();

        var name = ReadOptionalString(fields, "name", "workflow", allowExpression: true)
            ?? fileName
            ?? "workflow";
        var runName = ReadOptionalString(fields, "run-name", "workflow", allowExpression: true);

        var triggers = BuildTriggers(fields);
        var jobs = BuildJobs(fields);

        var timeout = ReadOptionalTimeout(fields, "timeout-minutes", "workflow", maxForGitHubHosted: 360);

        foreach (var entry in fields)
        {
            sources.Set(entry.Name, entry.ValueRange);
        }

        return new WorkflowDocument
        {
            Name = name,
            RunName = runName,
            Triggers = triggers,
            Jobs = jobs,
            Env = ReadStringMap(fields, "env", "env"),
            Defaults = ReadDefaults(fields),
            Concurrency = ReadConcurrency(fields),
            Permissions = ReadStringMap(fields, "permissions", "permissions"),
            TimeoutMinutes = timeout,
            Sources = sources,
        };
    }

    // ---------------------------------------------------------------- mapping view

    /// <summary>One key/value pair of a mapping, after merge keys have been resolved.</summary>
    private sealed record Entry(string Name, YamlNode Value, bool Inherited)
    {
        public YamlNode Key { get; init; } = null!;

        public SourceRange KeyRange => RangeOf(Key);

        public SourceRange ValueRange => RangeOf(Value);
    }

    /// <summary>
    /// Reads a mapping into entries, applying YAML 1.1 merge semantics: keys written directly in the
    /// mapping win over keys pulled in through <c>&lt;&lt;: *anchor</c>, and among several merge sources
    /// the earliest one wins. Inherited entries keep the anchor's own position, which is honest but
    /// surprising, so <see cref="SourceMap"/> marks them as inherited.
    /// </summary>
    /// <param name="map">The mapping to read.</param>
    /// <param name="allowedFields">The names this level accepts, or <see langword="null"/> to accept any.</param>
    /// <param name="scope">What to call this mapping in a message.</param>
    /// <param name="misplaced">
    /// Names that are not allowed at this level but that the section which owns them reports itself,
    /// with a message saying where they belong. They are kept in the entry list and not reported here, so
    /// one misspelled or mis-nested key produces one finding rather than a pair.
    /// </param>
    /// <param name="mergeBudget">
    /// How many further <c>&lt;&lt;</c> levels may be followed. It is decremented on every recursive
    /// read, so a chain that folds back on itself stops with a diagnostic instead of recursing forever.
    /// </param>
    private List<Entry> ReadMapping(
        YamlMappingNode map,
        HashSet<string>? allowedFields,
        string scope,
        HashSet<string>? misplaced = null,
        int mergeBudget = MaxMergeDepth)
    {
        var entries = new List<Entry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var mergeSources = new List<YamlNode>();

        foreach (var child in map.Children)
        {
            if (child.Key is not YamlScalarNode keyScalar)
            {
                Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                    "A mapping key must be a plain name, but this one is a " + Describe(child.Key) + ".",
                    RangeOf(child.Key), "Use a simple key such as 'name:' or 'jobs:'.");
                continue;
            }

            if (keyScalar.Style == ScalarStyle.Plain && (keyScalar.Value ?? string.Empty) == "<<")
            {
                mergeSources.Add(child.Value);
                continue;
            }

            if (!names.Add((keyScalar.Value ?? string.Empty)))
            {
                // The safety scanner already reports duplicates; this guard keeps the model deterministic.
                continue;
            }

            var entry = new Entry((keyScalar.Value ?? string.Empty), child.Value, Inherited: false) { Key = child.Key };
            if (allowedFields is not null && !allowedFields.Contains(entry.Name) && !IsTriggerSpelling(entry.Name))
            {
                if (misplaced is not null && misplaced.Contains(entry.Name))
                {
                    // Reported by the owner of the name, which can say where the key belongs.
                    entries.Add(entry);
                    continue;
                }

                // A finding about a name points at the name, not at the value it was given: the author has
                // to see which word GitHub did not accept.
                Add(DiagnosticCodes.UnknownField, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                    $"'{entry.Name}' is not a workflow {scope} property.", entry.KeyRange,
                    SuggestField(entry.Name, allowedFields));
                continue;
            }

            entries.Add(entry);
        }

        var merged = ExpandMergeSources(mergeSources, mergeBudget);
        foreach (var source in merged)
        {
            foreach (var inherited in ReadMapping(source, allowedFields: null, scope, mergeBudget: mergeBudget - 1))
            {
                if (names.Add(inherited.Name))
                {
                    entries.Add(inherited with { Inherited = true });
                }
            }
        }

        return entries;
    }

    private static bool IsTriggerSpelling(string name) => TriggerFieldSpellings.Contains(name);

    private List<YamlMappingNode> ExpandMergeSources(List<YamlNode> sources, int budget)
    {
        var result = new List<YamlMappingNode>();
        if (budget <= 0)
        {
            Add(DiagnosticCodes.YamlDepthLimit, DiagnosticSeverity.Error, DiagnosticStage.Yaml,
                $"Merge keys ('<<') are nested more than {MaxMergeDepth} levels deep.", SourceRange.Unknown,
                "Flatten the anchors so that merges do not chain so deeply.");
            return result;
        }

        foreach (var source in sources)
        {
            switch (source)
            {
                case YamlMappingNode map:
                    result.Add(map);
                    break;
                case YamlSequenceNode sequence:
                    foreach (var item in sequence.Children)
                    {
                        if (item is YamlMappingNode itemMap)
                        {
                            result.Add(itemMap);
                        }
                        else
                        {
                            Add(DiagnosticCodes.YamlUnsupportedConstruct, DiagnosticSeverity.Error, DiagnosticStage.Yaml,
                                $"A '<<' merge entry is a {Describe(item)}; only mappings can be merged.",
                                RangeOf(item), "Merge an anchor that points at a mapping.");
                        }
                    }

                    break;
                default:
                    Add(DiagnosticCodes.YamlUnsupportedConstruct, DiagnosticSeverity.Error, DiagnosticStage.Yaml,
                        $"'<<' must be followed by a mapping or a list of mappings, but found {Describe(source)}.",
                        RangeOf(source), "Point the merge key at an anchor that holds a mapping.");
                    break;
            }
        }

        return result;
    }

    // ---------------------------------------------------------------- lookups

    private static Entry? Find(List<Entry> fields, string name) =>
        fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal));

    private static bool Remove(List<Entry> fields, string name)
    {
        var index = fields.FindIndex(field => string.Equals(field.Name, name, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }

        fields.RemoveAt(index);
        return true;
    }

    private string? ReadRequiredString(List<Entry> fields, string name, string scope)
    {
        var entry = Find(fields, name);
        if (entry is null)
        {
            Add(DiagnosticCodes.MissingRequired, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"'{name}' is required on {scope}.", SourceRange.Unknown,
                $"Add '{name}:' to {scope}.");
            return null;
        }

        return Text(entry.Value, name, scope);
    }

    private string? ReadOptionalString(List<Entry> fields, string name, string scope, bool allowExpression = true)
    {
        var entry = Find(fields, name);
        if (entry is null)
        {
            return null;
        }

        var text = Text(entry.Value, name, scope);
        if (text is not null && !allowExpression && IsExpressionText(text))
        {
            Add(DiagnosticCodes.InvalidValue, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"'{name}' on {scope} has to be a literal value, but it holds an expression.",
                entry.ValueRange, "Write the value itself; expressions are not substituted here.");
        }

        return text;
    }

    /// <summary>The text of a node as the author wrote it, with non-text nodes coerced the way Actions coerces them.</summary>
    private string? Text(YamlNode node, string name, string scope)
    {
        var value = ToWorkflowValue(node);
        if (value.Value.Kind is GitHubValueKind.Object)
        {
            Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"'{name}' on {scope} must be text, but it is a mapping.", value.Range,
                "Give a single scalar value, for example 'name: Build and test'.");
            return null;
        }

        if (value.Value.Kind == GitHubValueKind.Array)
        {
            Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"'{name}' on {scope} must be text, but it is a list.", value.Range,
                "Give a single scalar value instead of a list.");
            return null;
        }

        return value.Text;
    }

    private int? ReadOptionalTimeout(List<Entry> fields, string name, string scope, int maxForGitHubHosted)
    {
        var entry = Find(fields, name);
        if (entry is null)
        {
            return null;
        }

        var value = ToWorkflowValue(entry.Value);
        if (value.Value.Kind != GitHubValueKind.Number)
        {
            // An expression such as '${{ inputs.timeout }}' is legal; only its value is unknowable here.
            if (value.Text.Contains("${{", StringComparison.Ordinal))
            {
                return null;
            }

            Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"'{name}' on {scope} must be a number of minutes, but it is {Describe(entry.Value)}.",
                value.Range, $"Use a plain number, for example '{name}: 15'.");
            return null;
        }

        var minutes = value.Value.Number;
        if (minutes <= 0)
        {
            Add(DiagnosticCodes.InvalidValue, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"'{name}' on {scope} is {value.Text} minutes; it must be greater than zero.", value.Range,
                "Use a positive number of minutes.");
            return (int)minutes;
        }

        if (minutes > maxForGitHubHosted)
        {
            Add(DiagnosticCodes.InvalidValue, DiagnosticSeverity.Warning, DiagnosticStage.Schema,
                $"'{name}' on {scope} is {(int)minutes} minutes, above the {maxForGitHubHosted} minute ceiling on GitHub-hosted runners.",
                value.Range, "Job timeouts are capped at 360 minutes on GitHub-hosted runners; a self-hosted runner may allow more.");
        }

        return (int)minutes;
    }

    private IReadOnlyDictionary<string, WorkflowValue> ReadStringMap(List<Entry> fields, string name, string scope)
    {
        var entry = Find(fields, name);
        if (entry is null)
        {
            return EmptyValues();
        }

        if (entry.Value is not YamlMappingNode map)
        {
            Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"'{name}' on {scope} must be a mapping of names to values, but it is {Describe(entry.Value)}.",
                entry.ValueRange, $"Write '{name}:' followed by indented 'KEY: value' lines.");
            return EmptyValues();
        }

        var result = new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);
        foreach (var pair in ReadMapping(map, allowedFields: null, scope: name))
        {
            result[pair.Name] = ToWorkflowValue(pair.Value);
        }

        return result;

        static IReadOnlyDictionary<string, WorkflowValue> EmptyValues() =>
            new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);
    }

    private WorkflowValue[] ReadValueList(List<Entry> fields, string name, string scope)
    {
        var entry = Find(fields, name);
        if (entry is null)
        {
            return Array.Empty<WorkflowValue>();
        }

        return entry.Value switch
        {
            YamlSequenceNode sequence => sequence.Children.Select(ToWorkflowValue).ToArray(),
            YamlScalarNode or YamlMappingNode => new[] { ToWorkflowValue(entry.Value) },
            _ => Array.Empty<WorkflowValue>(),
        };
    }

    // ---------------------------------------------------------------- values

    /// <summary>Converts a YAML node into a typed value plus the text and position it came from.</summary>
    private WorkflowValue ToWorkflowValue(YamlNode node) => new(ToValue(node, 0), RawText(node, 0), RangeOf(node));

    private static GitHubValue ToValue(YamlNode node, int depth)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                return scalar.Style == ScalarStyle.Plain
                    ? YamlScalarResolver.Resolve((scalar.Value ?? string.Empty))
                    : GitHubValue.FromString((scalar.Value ?? string.Empty));
            case YamlSequenceNode sequence when depth < 16:
                return GitHubValue.FromArray(sequence.Children.Select(child => ToValue(child, depth + 1)).ToArray());
            case YamlMappingNode map when depth < 16:
                var values = new Dictionary<string, GitHubValue>(StringComparer.Ordinal);
                foreach (var pair in map.Children.Where(child => child.Key is YamlScalarNode))
                {
                    values[((YamlScalarNode)pair.Key).Value ?? string.Empty] = ToValue(pair.Value, depth + 1);
                }

                return GitHubValue.FromObject(values);
            default:
                return GitHubValue.Null;
        }
    }

    /// <summary>
    /// The value's own text. The depth bound matches <see cref="ToValue"/>: a YAML alias can point back
    /// into its own node graph, and without a bound this walk would never return.
    /// </summary>
    private static string RawText(YamlNode node, int depth)
    {
        if (depth >= 16)
        {
            return string.Empty;
        }

        return node switch
        {
            YamlScalarNode scalar => (scalar.Value ?? string.Empty),
            YamlSequenceNode sequence => "[" + string.Join(", ", sequence.Children.Select(child => RawText(child, depth + 1))) + "]",
            YamlMappingNode map => string.Join(", ", map.Children.Select(child => child.Key is YamlScalarNode key ? key.Value + ": " + RawText(child.Value, depth + 1) : "?")),
            _ => string.Empty,
        };
    }

    private static SourceRange RangeOf(YamlNode node) => new(ToPosition(node.Start), ToPosition(node.End));

    private static SourcePosition ToPosition(Mark mark) =>
        new((int)Math.Max(mark.Line, 1), (int)Math.Max(mark.Column, 1));

    private static string Describe(YamlNode node) => node switch
    {
        YamlScalarNode => "scalar",
        YamlSequenceNode => "list",
        YamlMappingNode => "mapping",
        _ => "empty value",
    };

    private void Add(string code, DiagnosticSeverity severity, DiagnosticStage stage, string message,
        SourceRange range, string? suggestion, string? helpLink = null) =>
        _diagnostics.Add(new Diagnostic
        {
            Code = code,
            Severity = severity,
            Stage = stage,
            Message = message,
            Range = range,
            Suggestion = suggestion,
            HelpLink = helpLink,
        });

    private static string? SuggestField(string name, ICollection<string> allowed)
    {
        var best = allowed
            .OrderBy(candidate => Distance(name, candidate))
            .ThenBy(candidate => candidate, StringComparer.Ordinal)
            .FirstOrDefault();
        return best is null || Distance(name, best) > 5
            ? null
            : $"Did you mean '{best}'?";

        // Not Levenshtein, and deliberately not shared with the other two "did you mean" helpers:
        // this scores a field-name hint by length difference plus positional mismatches, while the
        // graph and lint rules use an edit distance with their own budgets. Replacing either with the
        // other would change which suggestion (if any) a reader is shown.
        static int Distance(string left, string right) =>
            Math.Abs(left.Length - right.Length) + left.Zip(right).Count(pair => pair.First != pair.Second);
    }

    /// <summary>
    /// Finds raw nodes for a key known to be valid, looking through <c>&lt;&lt;</c> merge lists, for the
    /// two matrix keys that are not dimensions.
    /// </summary>
    private static IReadOnlyList<YamlNode>? FindRaw(YamlMappingNode map, string name)
    {
        foreach (var child in map.Children)
        {
            if (child.Key is YamlScalarNode { Value: "<<" } && child.Value is YamlSequenceNode sequence)
            {
                foreach (var item in sequence.Children.OfType<YamlMappingNode>())
                {
                    var nested = FindRaw(item, name);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }
            }
        }

        var direct = map.Children.FirstOrDefault(candidate =>
            candidate.Key is YamlScalarNode scalar && (scalar.Value ?? string.Empty) == name);
        return direct.Value is null ? null : new[] { direct.Value };
    }
}
