using ActionLens.Core.Text;

namespace ActionLens.Core.Linting.Rules;

/// <summary>
/// AL007 — a <c>runs-on</c> label that looks like a misspelling of a documented GitHub-hosted label.
/// </summary>
/// <remarks>
/// This is a typo check, not a whitelist check, and the difference is the whole rule. Actions puts no value
/// list on <c>runs-on</c>: a self-hosted runner class is an arbitrary string its owner invented, and the
/// documented hosted set churns under new images (<c>-vs2026</c>-style variants), ARM spellings and larger
/// runners whose names come from the runner itself. "Not in my list, so warn" therefore flags every legitimate
/// self-hosted configuration in the world — the single most annoying false positive available in this space,
/// and the reason the semantics document records the label set as an ambiguity rather than a schema.
/// <para>
/// So a label only earns a finding when it is one edit away from a documented hosted label and nothing else
/// is equally close: <c>ubuntu-latests</c> is a typo of <c>ubuntu-latest</c>, while <c>gpu-large</c>,
/// <c>ARM64</c> or <c>arms-class</c> are simply unknown and stay quiet. Comparison is case-insensitive, which
/// is how the runner matches labels.
/// </para>
/// <para>
/// <c>runs-on</c> as an expression (<c>${{ matrix.os }}</c>) and the <c>group:</c>/<c>labels:</c> mapping
/// form are not read at all: the first names no label, and the second addresses a runner group, whose labels
/// belong to the organisation.
/// </para>
/// </remarks>
internal sealed class RunnerLabelTypoRule : ILintRule
{
    /// <summary>
    /// The documented hosted labels, kept small on purpose: the rule only judges near-misses, so an entry the
    /// set is missing costs silence, not a false alarm. Larger-runner and image-variant suffixes are excluded
    /// for the same reason they are excluded from the check — they are far from every name here.
    /// </summary>
    private static readonly string[] HostedLabels =
    [
        "ubuntu-latest", "ubuntu-24.04", "ubuntu-22.04", "ubuntu-20.04",
        "ubuntu-24.04-arm", "ubuntu-22.04-arm",
        "windows-latest", "windows-11", "windows-11-arm", "windows-2025", "windows-2022", "windows-2019",
        "macos-latest", "macos-15", "macos-14", "macos-13",
    ];

    /// <summary>The label every self-hosted runner class carries, which makes the rest of the list user-defined.</summary>
    private const string SelfHosted = "self-hosted";

    /// <summary>One insertion, deletion or substitution: the most a typo can be and still be recognised as one.</summary>
    private const int TypoBudget = 1;

    public LintRuleDescriptor Descriptor => LintRuleTable.RunnerLabelTypo;

    public IReadOnlyList<LintFinding> Evaluate(LintContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<LintFinding>();
        foreach (var job in context.Workflow.Jobs)
        {
            var runsOn = job.RunsOn;
            if (!runsOn.IsPresent || runsOn.IsExpression || runsOn.UsesGroupMapping
                || runsOn.Labels.Any(label => string.Equals(label.Text.Trim(), SelfHosted, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var label in runsOn.Labels)
            {
                var text = label.Text.Trim();
                if (text.Length == 0 || text.Contains("${{", StringComparison.Ordinal) || IsHosted(text))
                {
                    continue;
                }

                if (NearestTypoOf(text) is not { } intended)
                {
                    continue;
                }

                findings.Add(new LintFinding
                {
                    Message = $"The runner label '{text}' matches no GitHub-hosted image and is one edit from " +
                        $"'{intended}', so a hosted job asked for will never be picked up.",
                    Range = label.Range.IsKnown ? label.Range : job.Sources.Get("runs-on"),
                    Suggestion = $"Write '{intended}', or keep '{text}' only if a runner you control registers " +
                        "that label — add `self-hosted` to the list if that is what it is.",
                    Scope = job.Id,
                });
            }
        }

        return findings;
    }

    private static bool IsHosted(string label) =>
        HostedLabels.Any(hosted => string.Equals(hosted, label, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The documented label this one is a single edit from, or null. A tie is answered with null on purpose:
    /// with two equally plausible readings, naming one is a guess about what the author meant.
    /// </summary>
    private static string? NearestTypoOf(string label)
    {
        var best = int.MaxValue;
        var tied = false;
        string? intended = null;

        foreach (var hosted in HostedLabels)
        {
            var distance = EditDistance.LevenshteinIgnoreCase(label, hosted);
            if (distance > TypoBudget)
            {
                continue;
            }

            if (distance < best)
            {
                best = distance;
                intended = hosted;
                tied = false;
            }
            else if (distance == best)
            {
                tied = true;
            }
        }

        return tied ? null : intended;
    }
}
