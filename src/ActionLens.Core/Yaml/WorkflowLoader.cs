using System.Text;
using ActionLens.Core.Diagnostics;
using ActionLens.Core.Text;
using ActionLens.Core.Workflows;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ActionLens.Core.Yaml;

/// <summary>What reading a workflow produced: a model, diagnostics, or both.</summary>
public sealed record WorkflowLoadResult
{
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>Null when the file could not be turned into a model at all.</summary>
    public WorkflowDocument? Workflow { get; init; }

    /// <summary>True when an Error-or-worse finding means the model must not be trusted further.</summary>
    public bool IsBlocked => Workflow is null
        || Diagnostics.Any(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Error);

    public IReadOnlyList<Diagnostic> At(DiagnosticStage stage) =>
        Diagnostics.Where(diagnostic => diagnostic.Stage == stage).ToArray();
}

/// <summary>
/// Reads workflow YAML into <see cref="WorkflowDocument"/>, in two passes: a token-level safety scan
/// first, then the object graph and schema walk. Both passes answer with positioned diagnostics, so
/// malformed, hostile or merely invalid input still produces an explanation instead of an exception.
/// </summary>
public sealed class WorkflowLoader
{
    /// <summary>The size ceiling ActionLens accepts for one workflow file.</summary>
    public const int MaxInputBytes = 1_048_576;

    /// <summary>
    /// A ceiling on nodes visited when the YAML graph is walked <em>as expanded</em> — an alias pointing
    /// at a subtree is counted again, because that is exactly what a recursive reader does with it.
    /// Counting distinct node objects would miss a billion-laughs file entirely: aliases share one node.
    /// </summary>
    internal const int MaxExpandedNodes = 200_000;

    /// <summary>The depth the expanded walk will follow before it refuses to go further.</summary>
    internal const int MaxExpandedDepth = 64;

    public static WorkflowLoadResult Load(string yaml, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        if (yaml.Trim().Length == 0)
        {
            return new WorkflowLoadResult
            {
                Diagnostics = new[]
                {
                    new Diagnostic
                    {
                        Code = DiagnosticCodes.YamlSyntax,
                        Severity = DiagnosticSeverity.Error,
                        Stage = DiagnosticStage.Yaml,
                        Message = "The workflow text is empty.",
                        Range = new SourceRange(new SourcePosition(1, 1)),
                        Suggestion = "Paste a workflow, or pick one of the examples.",
                    },
                },
            };
        }

        var bytes = Encoding.UTF8.GetByteCount(yaml);
        if (bytes > MaxInputBytes)
        {
            return new WorkflowLoadResult
            {
                Diagnostics = new[]
                {
                    new Diagnostic
                    {
                        Code = DiagnosticCodes.YamlSizeLimit,
                        Severity = DiagnosticSeverity.Error,
                        Stage = DiagnosticStage.Yaml,
                        Message = $"The workflow is {bytes:N0} bytes, above the {MaxInputBytes:N0} byte limit ActionLens reads.",
                        Range = new SourceRange(new SourcePosition(1, 1)),
                        Suggestion = "Split the workflow, or analyse the job you care about on its own.",
                    },
                },
            };
        }

        var scan = YamlSafetyScanner.Scan(yaml);
        if (!scan.CanContinue)
        {
            // The scan found something that makes the file unusable as one workflow document — a
            // syntax error, several documents, or a shape designed to make a parser work too hard.
            // There is no honest model to build from it, so the diagnostics are the whole answer.
            return new WorkflowLoadResult { Diagnostics = scan.Diagnostics };
        }

        var stream = new YamlStream();
        YamlNode? root;
        try
        {
            stream.Load(new StringReader(yaml));
            root = stream.Documents.Count > 0 ? stream.Documents[0].RootNode : null;
        }
        catch (YamlException ex)
        {
            // Reached only by shapes the event scan cannot see, such as an undefined anchor. It is still
            // just a diagnostic: reading a workflow must never throw at the caller.
            return new WorkflowLoadResult
            {
                Diagnostics = scan.Diagnostics.Concat([YamlSafetyScanner.ToDiagnostic(ex)]).ToArray(),
            };
        }

        // The token pass bounds alias *references*; this pass bounds the graph those references expand
        // into, before any of the recursive model walkers see it. A self-referencing anchor produces a
        // cyclic graph, so the walk is iterative with an explicit depth and visit budget rather than
        // recursive — StackOverflowException cannot be caught, so prevention is the only option.
        if (!WithinExpandedNodeBudget(root))
        {
            return new WorkflowLoadResult
            {
                Diagnostics = scan.Diagnostics.Concat(
                [
                    new Diagnostic
                    {
                        Code = DiagnosticCodes.YamlExpansionLimit,
                        Severity = DiagnosticSeverity.Error,
                        Stage = DiagnosticStage.Yaml,
                        Message = $"The workflow expands to more than {MaxExpandedNodes:N0} nodes (or nests deeper than {MaxExpandedDepth} levels) once anchors are followed.",
                        Range = new SourceRange(new SourcePosition(1, 1)),
                        Suggestion = "Remove the recursive or repeated anchors; they make the file exponentially larger than it looks.",
                    },
                ]).ToArray(),
            };
        }

        var builder = new WorkflowModelBuilder();
        var workflow = builder.Build(root, fileName);
        return new WorkflowLoadResult
        {
            Workflow = workflow,
            Diagnostics = scan.Diagnostics.Concat(builder.Diagnostics).ToArray(),
        };
    }

    /// <summary>
    /// Walks the loaded graph with an explicit stack, counting every visit rather than every distinct
    /// node. An alias resolves to the very same node object, so a recursive reader that follows it twice
    /// does twice the work — which is what a YAML bomb exploits. A cycle therefore does not terminate
    /// this walk on its own; the visit and depth budgets do, before any recursive walker is entered.
    /// </summary>
    private static bool WithinExpandedNodeBudget(YamlNode? root)
    {
        if (root is null)
        {
            return true;
        }

        var pending = new Stack<(YamlNode Node, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;
        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            if (++visited > MaxExpandedNodes || depth > MaxExpandedDepth)
            {
                return false;
            }

            switch (node)
            {
                case YamlMappingNode map:
                    foreach (var child in map.Children)
                    {
                        pending.Push((child.Key, depth + 1));
                        pending.Push((child.Value, depth + 1));
                    }

                    break;
                case YamlSequenceNode sequence:
                    foreach (var child in sequence.Children)
                    {
                        pending.Push((child, depth + 1));
                    }

                    break;
            }
        }

        return true;
    }
}
