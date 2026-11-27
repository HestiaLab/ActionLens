using ActionLens.Core.Diagnostics;
using ActionLens.Core.Text;
using ActionLens.Core.Workflows;
using YamlDotNet.RepresentationModel;

namespace ActionLens.Core.Yaml;

internal sealed partial class WorkflowModelBuilder
{
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
}
