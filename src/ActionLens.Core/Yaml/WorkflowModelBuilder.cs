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
internal sealed class WorkflowModelBuilder
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

    // ---------------------------------------------------------------- on:

    private TriggerSection BuildTriggers(List<Entry> fields)
    {
        var sources = new SourceMap();
        var triggerEntry = fields.FirstOrDefault(field => IsTriggerSpelling(field.Name));
        if (triggerEntry is null)
        {
            Add(DiagnosticCodes.MissingRequired, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                "The workflow has no 'on:' section, so it can never be triggered.", SourceRange.Unknown,
                "Add 'on:' with at least one event, for example 'on: push'.");
            Remove(fields, "name");
            return new TriggerSection { Events = new Dictionary<string, EventTrigger>(StringComparer.Ordinal) };
        }

        // Consume the trigger key so the job/name lookups below cannot mistake it for a job.
        Remove(fields, triggerEntry.Name);
        sources.Set(triggerEntry.Name, triggerEntry.ValueRange);

        var events = new Dictionary<string, EventTrigger>(StringComparer.Ordinal);
        switch (triggerEntry.Value)
        {
            case YamlScalarNode scalar:
                AddEvent(events, (scalar.Value ?? string.Empty), value: null, scalar);
                break;
            case YamlSequenceNode sequence:
                foreach (var item in sequence.Children)
                {
                    if (item is YamlScalarNode itemScalar)
                    {
                        AddEvent(events, itemScalar.Value ?? string.Empty, value: null, itemScalar);
                    }
                    else
                    {
                        Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                            $"An 'on:' list entry must be an event name, but this one is {Describe(item)}.",
                            RangeOf(item), "Use a plain event name such as 'push', or switch 'on:' to a mapping.");
                    }
                }

                break;
            case YamlMappingNode mapping:
                foreach (var pair in ReadMapping(mapping, allowedFields: null, scope: "on"))
                {
                    AddEvent(events, pair.Name, pair.Value, pair.Key);
                }

                break;
            default:
                Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                    $"'on:' must be an event name, a list of event names, or a mapping, but it is {Describe(triggerEntry.Value)}.",
                    triggerEntry.ValueRange, "Write 'on: push' or 'on:' with one event per line.");
                break;
        }

        if (events.Count == 0)
        {
            Add(DiagnosticCodes.MissingRequired, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                "'on:' lists no events, so the workflow can never run.", triggerEntry.ValueRange,
                "Add at least one event under 'on:'.");
        }

        return new TriggerSection
        {
            Events = events,
            IsShorthand = triggerEntry.Value is YamlScalarNode or YamlSequenceNode,
            Sources = sources,
        };
    }

    private void AddEvent(Dictionary<string, EventTrigger> events, string name, YamlNode? value, YamlNode keyNode)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Add(DiagnosticCodes.InvalidIdentifier, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                "An 'on:' event name is empty.", RangeOf(keyNode), "Name the event, for example 'push'.");
            return;
        }

        if (!events.TryAdd(name, BuildEventTrigger(name, value, RangeOf(keyNode))))
        {
            Add(DiagnosticCodes.YamlDuplicateKey, DiagnosticSeverity.Error, DiagnosticStage.Yaml,
                $"The event '{name}' is listed twice under 'on:'.", RangeOf(keyNode),
                $"Keep only one '{name}' entry.");
        }
    }

    private EventTrigger BuildEventTrigger(string name, YamlNode? value, SourceRange range)
    {
        var trigger = new EventTrigger { Name = name, Range = range };
        if (value is null)
        {
            return trigger;
        }

        // `schedule:` is the one event whose value is a list of cron entries rather than a mapping of
        // filters, so the list is read as the schedule it is instead of reported as the wrong shape.
        if (name == "schedule" && value is YamlSequenceNode)
        {
            return new EventTrigger
            {
                Name = name,
                HasConfiguration = true,
                Cron = ReadSchedule([new Entry(name, value, Inherited: false)]),
                Range = range,
            };
        }

        if (value is YamlMappingNode map)
        {
            var fields = ReadMapping(map, allowedFields: null, scope: $"'{name}'");
            if (fields.Count == 0)
            {
                return trigger;
            }

            return new EventTrigger
            {
                Name = name,
                HasConfiguration = true,
                Types = ReadValueList(fields, "types", name),
                Branches = ReadValueList(fields, "branches", name),
                BranchesIgnore = ReadValueList(fields, "branches-ignore", name),
                Tags = ReadValueList(fields, "tags", name),
                TagsIgnore = ReadValueList(fields, "tags-ignore", name),
                Paths = ReadValueList(fields, "paths", name),
                PathsIgnore = ReadValueList(fields, "paths-ignore", name),
                Cron = ReadSchedule(fields),
                Inputs = ReadInputs(fields, name),
                Range = range,
            };
        }

        // `push: ~` and `workflow_dispatch: ~` mean "the event with no configuration".
        if (value is YamlScalarNode { Value: "" or "~" or "null" or "Null" or "NULL" })
        {
            return trigger;
        }

        Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
            $"The '{name}' event configuration must be a mapping of filters such as 'branches' or 'types'.",
            RangeOf(value), $"Remove the value under '{name}:' or write filters under it.");
        return new EventTrigger
        {
            Name = name,
            HasConfiguration = true,
            Range = range,
        };
    }

    private IReadOnlyList<WorkflowValue> ReadSchedule(List<Entry> fields)
    {
        var entry = Find(fields, "schedule");
        if (entry is null)
        {
            return Array.Empty<WorkflowValue>();
        }

        if (entry.Value is not YamlSequenceNode sequence)
        {
            Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                "'schedule' must be a list of cron entries.", entry.ValueRange,
                "Write 'schedule:' followed by '- cron: \"...\"' lines.");
            return Array.Empty<WorkflowValue>();
        }

        var result = new List<WorkflowValue>();
        foreach (var item in sequence.Children)
        {
            if (item is YamlMappingNode itemMap)
            {
                var cronFields = ReadMapping(itemMap, allowedFields: null, scope: "schedule entry");
                var cron = Find(cronFields, "cron");
                if (cron is null)
                {
                    Add(DiagnosticCodes.MissingRequired, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                        "A 'schedule' entry has no 'cron'.", RangeOf(item), "Add \"cron: '0 0 * * *'\" to the entry.");
                }
                else
                {
                    result.Add(ToWorkflowValue(cron.Value));
                }
            }
            else
            {
                Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                    $"A 'schedule' entry must be a mapping with a 'cron' key, but it is {Describe(item)}.",
                    RangeOf(item), "Write '- cron: \"0 0 * * *\"'.");
            }
        }

        return result;
    }

    private Dictionary<string, WorkflowInput> ReadInputs(List<Entry> fields, string scope)
    {
        var entry = Find(fields, "inputs");
        if (entry?.Value is not YamlMappingNode map)
        {
            return new Dictionary<string, WorkflowInput>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, WorkflowInput>(StringComparer.Ordinal);
        foreach (var pair in ReadMapping(map, allowedFields: null, scope: $"inputs of '{scope}'"))
        {
            var range = pair.ValueRange;
            if (pair.Value is not YamlMappingNode inputMap)
            {
                Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                    $"The input '{pair.Name}' must be a mapping with 'description', 'type', 'default' and 'required'.",
                    range, $"Write 'type: string' and 'default: ...' under '{pair.Name}:'.");
                continue;
            }

            var inputFields = ReadMapping(inputMap, allowedFields: null, scope: $"input '{pair.Name}'");
            var type = ReadOptionalString(inputFields, "type", $"input '{pair.Name}'");
            if (type is not null
                && type is not ("string" or "boolean" or "number" or "choice" or "environment")
                && !type.Contains("${{", StringComparison.Ordinal))
            {
                Add(DiagnosticCodes.InvalidValue, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                    $"The input '{pair.Name}' declares type '{type}', which is not one of string, boolean, number, choice or environment.",
                    range, "Use one of the supported input types.");
            }

            var defaultEntry = Find(inputFields, "default");
            var requiredEntry = Find(inputFields, "required");
            result[pair.Name] = new WorkflowInput
            {
                Type = type,
                Description = ReadOptionalString(inputFields, "description", $"input '{pair.Name}'"),
                Required = requiredEntry?.Value is YamlScalarNode required && IsTrue(required.Value ?? string.Empty),
                Default = defaultEntry is null ? null : ToWorkflowValue(defaultEntry.Value),
                Options = ReadValueList(inputFields, "options", $"input '{pair.Name}'"),
                Range = range,
            };
        }

        return result;
    }

    private static bool IsTrue(string text) =>
        text is "true" or "True" or "TRUE" or "yes" or "Yes" or "YES" or "on" or "On" or "ON";

    // ---------------------------------------------------------------- jobs

    private IReadOnlyList<JobDefinition> BuildJobs(List<Entry> fields)
    {
        var entry = Find(fields, "jobs");
        if (entry is null)
        {
            Add(DiagnosticCodes.MissingRequired, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                "The workflow has no 'jobs:' section.", SourceRange.Unknown, "Add 'jobs:' with at least one job.");
            return Array.Empty<JobDefinition>();
        }

        Remove(fields, "jobs");

        if (entry.Value is not YamlMappingNode jobsMap)
        {
            Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"'jobs:' must be a mapping of job ids to job definitions, but it is {Describe(entry.Value)}.",
                entry.ValueRange, "Write 'jobs:' followed by 'my-job:' and the job's properties.");
            return Array.Empty<JobDefinition>();
        }

        var jobFields = ReadMapping(jobsMap, allowedFields: null, scope: "jobs");
        if (jobFields.Count == 0)
        {
            Add(DiagnosticCodes.NoJobs, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                "'jobs:' is empty, so the workflow defines nothing to run.", entry.ValueRange,
                "Add at least one job under 'jobs:'.");
            return Array.Empty<JobDefinition>();
        }

        var jobs = new List<JobDefinition>(jobFields.Count);
        foreach (var job in jobFields)
        {
            jobs.Add(BuildJob(job));
        }

        return jobs;
    }

    private JobDefinition BuildJob(Entry jobEntry)
    {
        var id = jobEntry.Name;
        var keyRange = jobEntry.KeyRange;
        if (!JobIdPattern.IsMatch(id))
        {
            Add(DiagnosticCodes.InvalidIdentifier, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"'{id}' is not a valid job id.", keyRange,
                "A job id must start with a letter or underscore and contain only letters, digits, '-' and '_'.");
        }

        var sources = new SourceMap();
        if (jobEntry.Inherited)
        {
            sources.SetInherited("job", keyRange);
        }
        else
        {
            sources.Set("job", keyRange);
        }

        if (jobEntry.Value is not YamlMappingNode map)
        {
            Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"The job '{id}' must be a mapping, but it is {Describe(jobEntry.Value)}.",
                jobEntry.ValueRange, $"Write 'runs-on:' and 'steps:' under '{id}:'.");
            return new JobDefinition { Id = id, KeyRange = keyRange, Sources = sources };
        }

        var fields = ReadMapping(map, JobFields, $"job '{id}'", MisplacedJobFields);
        foreach (var field in fields)
        {
            if (field.Inherited)
            {
                sources.SetInherited(field.Name, field.ValueRange);
            }
            else
            {
                sources.Set(field.Name, field.ValueRange);
            }
        }

        var uses = ReadOptionalString(fields, "uses", $"job '{id}'", allowExpression: true);
        var hasSteps = Find(fields, "steps") is not null;
        if (uses is not null && hasSteps)
        {
            Add(DiagnosticCodes.InvalidValue, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"The job '{id}' has both 'uses' and 'steps', and GitHub accepts only one.",
                sources.Get("steps"), "Keep 'uses' for a reusable workflow call, or keep 'steps' for a normal job.");
        }

        if (uses is null && !hasSteps)
        {
            Add(DiagnosticCodes.MissingRequired, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"The job '{id}' has neither 'steps' nor 'uses', so it does nothing.", keyRange,
                $"Add 'steps:' to '{id}'.");
        }

        if (uses is not null)
        {
            ValidateUses(uses, sources.Get("uses"), $"job '{id}'");
        }

        var runsOnEntry = Find(fields, "runs-on");
        var runsOn = BuildRunsOn(runsOnEntry, id);
        if (uses is null && runsOnEntry is null)
        {
            Add(DiagnosticCodes.MissingRequired, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"The job '{id}' has no 'runs-on', so GitHub does not know where to run it.", keyRange,
                $"Add 'runs-on: ubuntu-latest' (or the label of one of your self-hosted runners).");
        }

        var condition = Find(fields, "if");
        return new JobDefinition
        {
            Id = id,
            KeyRange = keyRange,
            Name = ReadOptionalString(fields, "name", $"job '{id}'", allowExpression: true),
            Condition = condition is null ? null : ToWorkflowValue(condition.Value),
            Needs = ReadNeeds(fields, id),
            RunsOn = runsOn,
            Strategy = BuildStrategy(fields, id),
            Steps = BuildSteps(fields, id),
            Env = ReadStringMap(fields, "env", $"job '{id}'"),
            Outputs = ReadStringMap(fields, "outputs", $"job '{id}'"),
            Defaults = ReadDefaults(fields),
            Concurrency = ReadConcurrency(fields),
            TimeoutMinutes = ReadOptionalTimeout(fields, "timeout-minutes", $"job '{id}'", maxForGitHubHosted: 360),
            ReusableWorkflow = uses,
            With = ReadStringMap(fields, "with", $"job '{id}'"),
            HasContainer = Find(fields, "container") is not null,
            HasServices = Find(fields, "services") is not null,
            HasEnvironment = Find(fields, "environment") is not null,
            PassesAllSecrets = IsInheritSecrets(Find(fields, "secrets")),
            Sources = sources,
        };
    }

    private static bool IsInheritSecrets(Entry? entry) =>
        entry?.Value is YamlScalarNode scalar && string.Equals((scalar.Value ?? string.Empty), "inherit", StringComparison.OrdinalIgnoreCase);

    private RunsOn BuildRunsOn(Entry? entry, string jobId)
    {
        if (entry is null)
        {
            return RunsOn.Missing;
        }

        return entry.Value switch
        {
            YamlScalarNode scalar => new RunsOn
            {
                IsPresent = true,
                Labels = new[] { ToWorkflowValue(scalar) },
                IsExpression = IsExpressionText((scalar.Value ?? string.Empty)),
            },
            YamlSequenceNode sequence => new RunsOn
            {
                IsPresent = sequence.Children.Count > 0,
                Labels = sequence.Children.Select(ToWorkflowValue).ToArray(),
                IsExpression = sequence.Children.OfType<YamlScalarNode>()
                    .Any(item => IsExpressionText(item.Value ?? string.Empty)),
            },
            YamlMappingNode map => BuildRunsOnMapping(map, jobId, entry.ValueRange),
            _ => RunsOn.Missing,
        };
    }

    /// <summary>
    /// <c>runs-on</c> also accepts the <c>group</c>/<c>labels</c> mapping form used by larger runners and
    /// self-hosted runner groups. Labels from that form are kept; the group name is not modelled further
    /// because resolving it needs the organisation's runner group configuration.
    /// </summary>
    private RunsOn BuildRunsOnMapping(YamlMappingNode map, string jobId, SourceRange range)
    {
        var fields = ReadMapping(map, new(StringComparer.Ordinal) { "group", "labels" }, $"runs-on of job '{jobId}'");
        var labels = ReadValueList(fields, "labels", $"runs-on of job '{jobId}'");
        var group = ReadOptionalString(fields, "group", $"runs-on of job '{jobId}'", allowExpression: true);
        if (labels.Length == 0 && group is null)
        {
            Add(DiagnosticCodes.InvalidValue, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"'runs-on' of job '{jobId}' must use the 'group' or 'labels' key.", range,
                "Write 'group: my-group' or 'labels: [self-hosted, linux]'.");
            return new RunsOn { IsPresent = false };
        }

        return new RunsOn
        {
            IsPresent = labels.Length > 0 || group is not null,
            Labels = labels,
            IsExpression = labels.Any(label => IsExpressionText(label.Text)) || IsExpressionText(group ?? string.Empty),
            UsesGroupMapping = true,
        };
    }

    private static bool IsExpressionText(string text) => text.Contains("${{", StringComparison.Ordinal);

    private string[] ReadNeeds(List<Entry> fields, string jobId)
    {
        var entry = Find(fields, "needs");
        if (entry is null)
        {
            return Array.Empty<string>();
        }

        return entry.Value switch
        {
            YamlScalarNode scalar => new[] { (scalar.Value ?? string.Empty) },
            YamlSequenceNode sequence => sequence.Children.Select((child, index) => child switch
            {
                YamlScalarNode item => item.Value ?? string.Empty,
                _ => ReportNeedsShape(child, jobId, index),
            }).ToArray(),
            _ => WrongNeedsShape(entry.Value, jobId),
        };
    }

    private string ReportNeedsShape(YamlNode node, string jobId, int index)
    {
        Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
            $"Job '{jobId}' lists a 'needs' entry that is {Describe(node)} instead of a job id.",
            RangeOf(node), "Use plain job ids, for example 'needs: [build, test]'.");
        return $"needs[{index}]";
    }

    private string[] WrongNeedsShape(YamlNode node, string jobId)
    {
        Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
            $"'needs' of job '{jobId}' must be a job id or a list of job ids.", RangeOf(node),
            "Write 'needs: build' or 'needs: [build, test]'.");
        return Array.Empty<string>();
    }

    // ---------------------------------------------------------------- steps

    private IReadOnlyList<StepDefinition> BuildSteps(List<Entry> fields, string jobId)
    {
        var entry = Find(fields, "steps");
        if (entry is null)
        {
            return Array.Empty<StepDefinition>();
        }

        if (entry.Value is not YamlSequenceNode sequence)
        {
            Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"'steps' of job '{jobId}' must be a list, but it is {Describe(entry.Value)}.",
                entry.ValueRange, "Write '- uses: ...' or '- run: ...' lines under 'steps:'.");
            return Array.Empty<StepDefinition>();
        }

        var steps = new List<StepDefinition>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            steps.Add(BuildStep(index, sequence.Children[index], jobId));
        }

        return steps;
    }

    private StepDefinition BuildStep(int index, YamlNode node, string jobId)
    {
        var range = RangeOf(node);
        var label = $"step {index + 1} of job '{jobId}'";
        if (node is not YamlMappingNode map)
        {
            Add(DiagnosticCodes.WrongType, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"{label} is {Describe(node)}; a step must be a mapping with 'uses' or 'run'.", range,
                "Write '- run: ...' or '- uses: ...'.");
            return new StepDefinition { Index = index, KeyRange = range };
        }

        var fields = ReadMapping(map, StepFields, label);
        var sources = new SourceMap();
        foreach (var field in fields)
        {
            if (field.Inherited)
            {
                sources.SetInherited(field.Name, field.ValueRange);
            }
            else
            {
                sources.Set(field.Name, field.ValueRange);
            }
        }

        var uses = ReadOptionalString(fields, "uses", label, allowExpression: true);
        var run = ReadOptionalString(fields, "run", label, allowExpression: true);
        if (uses is null && run is null)
        {
            Add(DiagnosticCodes.MissingRequired, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"{label} has neither 'uses' nor 'run', so it runs nothing.", range,
                "Add 'run: ...' or 'uses: ...' to the step.");
        }
        else if (uses is not null && run is not null)
        {
            Add(DiagnosticCodes.InvalidValue, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"{label} has both 'uses' and 'run'; GitHub accepts only one.", range,
                "Keep 'uses' for an action, or keep 'run' for a script.");
        }

        if (uses is not null)
        {
            ValidateUses(uses, sources.Get("uses"), label);
        }

        var id = ReadOptionalString(fields, "id", label, allowExpression: false);
        if (id is not null && !StepIdPattern.IsMatch(id))
        {
            Add(DiagnosticCodes.InvalidIdentifier, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"'{id}' is not a valid step id.", sources.Get("id"),
                "A step id must start with a letter or underscore and contain only letters, digits, '-' and '_'.");
        }

        var continueOnError = Find(fields, "continue-on-error");

        return new StepDefinition
        {
            Index = index,
            KeyRange = range,
            Id = id,
            Name = ReadOptionalString(fields, "name", label, allowExpression: true),
            Condition = ToNullableWorkflowValue(Find(fields, "if")),
            Uses = uses,
            Run = run,
            Shell = ReadOptionalString(fields, "shell", label, allowExpression: true),
            WorkingDirectory = ReadOptionalString(fields, "working-directory", label, allowExpression: true),
            With = ReadStringMap(fields, "with", label),
            Env = ReadStringMap(fields, "env", label),
            ContinueOnError = continueOnError is null ? null : ToWorkflowValue(continueOnError.Value),
            TimeoutMinutes = ReadOptionalTimeout(fields, "timeout-minutes", label, maxForGitHubHosted: 360),
            Sources = sources,
        };
    }

    private WorkflowValue? ToNullableWorkflowValue(Entry? entry) => entry is null ? null : ToWorkflowValue(entry.Value);

    private void ValidateUses(string uses, SourceRange range, string scope)
    {
        if (IsExpressionText(uses))
        {
            // A reference built from an expression cannot be checked statically; saying so is the answer.
            return;
        }

        if (uses.StartsWith("./", StringComparison.Ordinal) || uses.StartsWith("~/", StringComparison.Ordinal))
        {
            if (!uses.Contains('/', StringComparison.Ordinal))
            {
                Add(DiagnosticCodes.InvalidValue, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                    $"'{uses}' is not a usable local path.", range, "Use a path such as './.github/actions/my-action'.");
            }

            return;
        }

        if (uses.StartsWith("docker://", StringComparison.Ordinal))
        {
            return;
        }

        if (!RemoteActionPattern.IsMatch(uses))
        {
            Add(DiagnosticCodes.InvalidValue, DiagnosticSeverity.Error, DiagnosticStage.Schema,
                $"'{uses}' is not a valid 'uses' reference on {scope}.", range,
                "Use 'owner/repo@ref', 'owner/repo/path@ref', './local/action.yml' or 'docker://image'.");
        }
    }

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
