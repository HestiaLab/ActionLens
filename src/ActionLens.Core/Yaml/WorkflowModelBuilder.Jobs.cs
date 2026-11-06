using ActionLens.Core.Diagnostics;
using ActionLens.Core.Text;
using ActionLens.Core.Workflows;
using YamlDotNet.RepresentationModel;

namespace ActionLens.Core.Yaml;

internal sealed partial class WorkflowModelBuilder
{
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
}
