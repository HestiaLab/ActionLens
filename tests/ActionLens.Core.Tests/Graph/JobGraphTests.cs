using ActionLens.Core.Diagnostics;
using ActionLens.Core.Graph;
using ActionLens.Core.Text;
using ActionLens.Core.Workflows;

namespace ActionLens.Core.Tests.Graph;

/// <summary>
/// The graph stage answers three questions only: what depends on what, is that legal, and where does
/// each job sit on the grid. Whether a job <em>will</em> run belongs to the scenario engine, so no
/// test here talks about conditions or status.
/// </summary>
/// <remarks>
/// Layouts are compared as one string of <c>id:layer:slot</c> triples and id lists as comma-joined
/// text: a single comparison shows the whole order, so a regression says which job moved.
/// </remarks>
public sealed class JobGraphTests
{
    // ---------------------------------------------------------------- accepted shapes

    [Fact]
    public void Build_WithoutNeeds_PutsEveryJobInLayerZero()
    {
        var result = JobGraph.Build(Spec("lint:;test:;package:"));
        var graph = result.Graph;

        Assert.Equal(3, graph.JobCount);
        Assert.Empty(graph.Edges);
        Assert.Empty(result.Diagnostics);
        Assert.True(graph.IsAcyclic);
        Assert.True(result.IsTrustworthy);
        Assert.Equal("lint,test,package", Ids(graph.TopologicalOrder));
        Assert.Equal("lint:0:0|test:0:1|package:0:2", Signature(result.Layout));

        // One layer holding every job: width is the vertical span the drawing needs.
        Assert.Equal(1, result.Layout.LayerCount);
        Assert.Equal(3, result.Layout.MaxLayerWidth);
        Assert.Equal("3", Widths(result.Layout));
    }

    [Theory]
    // A chain, a diamond, a long path with a shortcut edge, and two chains that rejoin.
    [InlineData("a:;b=a;c=b;d=c", "a:0:0|b:1:0|c:2:0|d:3:0", 4)]
    [InlineData("a:;b=a;c=a;d=b c", "a:0:0|b:1:0|c:1:1|d:2:0", 3)]
    [InlineData("a:;b=a;c=b;d=c a", "a:0:0|b:1:0|c:2:0|d:3:0", 4)]
    [InlineData("a:;b=a;c=b;x=a;y=x", "a:0:0|b:1:0|x:1:1|c:2:0|y:2:1", 3)]
    public void Build_LayersByLongestPath(string spec, string expected, int expectedLayers)
    {
        var result = JobGraph.Build(Spec(spec));

        Assert.Equal(expected, Signature(result.Layout));
        Assert.Equal(expectedLayers, result.Layout.LayerCount);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Build_Diamond_DoesNotUseTheShortEdgeForDepth()
    {
        var result = JobGraph.Build(Spec("a:;b=a;c=a;d=b c"));

        Assert.Equal(2, result.Layout.NodeFor("d")?.Layer);
        Assert.Equal(0, result.Layout.NodeFor("b")?.Slot);
        Assert.Equal(1, result.Layout.NodeFor("c")?.Slot);
        Assert.Equal(4, result.Layout.Edges.Count);
    }

    [Fact]
    public void Build_ShortcutEdge_KeepsTheJobAtTheEndOfTheLongPath()
    {
        // A breadth-first depth would read the direct a -> d edge and place d in layer 1, to the left
        // of c, which is exactly the mistake longest-path layering exists to avoid.
        var result = JobGraph.Build(Spec("a:;b=a;c=b;d=c a"));

        Assert.Equal(3, result.Layout.NodeFor("d")?.Layer);
        Assert.Equal(2, result.Layout.NodeFor("c")?.Layer);
    }

    [Fact]
    public void Build_JoinsTwoDisconnectedChains()
    {
        var result = JobGraph.Build(Spec("a:;b=a;c=b;x:;y=x;final=c y"));

        Assert.Equal("a:0:0|x:0:1|b:1:0|y:1:1|c:2:0|final:3:0", Signature(result.Layout));
        Assert.Equal(4, result.Layout.LayerCount);
        Assert.Equal("2,2,1,1", Widths(result.Layout));
        Assert.Equal(3, result.Layout.NodeFor("final")?.Layer);
        Assert.Equal("c,y", Ids(result.Layout.NodeFor("final")!.Dependencies));
    }

    [Fact]
    public void Build_KeepsWrittenNeedsOrderAndFlipsEdgeDirectionForDrawing()
    {
        var result = JobGraph.Build(Spec("first:;second:;gate=second first"));
        var graph = result.Graph;

        Assert.Equal("second,first", Ids(graph.DependenciesOf("gate")));
        Assert.Equal(2, graph.Edges.Count);
        Assert.Equal("gate", graph.Edges[0].Job);
        Assert.Equal("second", graph.Edges[0].Needs);
        Assert.Equal("first", graph.Edges[1].Needs);

        // The chart reads left to right, so a layout edge runs from the job that finishes first.
        Assert.Equal("second", result.Layout.Edges[0].From);
        Assert.Equal("gate", result.Layout.Edges[0].To);
        Assert.Equal("first", result.Layout.Edges[1].From);
        Assert.Equal("first:0:0|second:0:1|gate:1:0", Signature(result.Layout));
    }

    [Fact]
    public void Build_ReusableWorkflowCall_HasOrdinaryNeedsEdges()
    {
        var jobs = new[]
        {
            Job(1, "build"),
            Job(2, "call", "build") with { ReusableWorkflow = "./reusable.yml" },
        };

        var result = JobGraph.Build(jobs);

        Assert.Single(result.Graph.Edges);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, result.Layout.NodeFor("call")?.Layer);
        Assert.True(result.IsTrustworthy);
    }

    // ---------------------------------------------------------------- findings about needs

    [Fact]
    public void Build_NeedsUnknownJob_ReportsAtTheNeedsRangeAndLeavesTheJobIndependent()
    {
        var result = JobGraph.Build(Spec("build:;deploy=buuld"));
        var finding = Assert.Single(result.Diagnostics);

        Assert.Equal(DiagnosticCodes.NeedsUnknownJob, finding.Code);
        Assert.Equal(DiagnosticSeverity.Error, finding.Severity);
        Assert.Equal(DiagnosticStage.Graph, finding.Stage);
        Assert.Equal("deploy", finding.Scope);
        Assert.Equal(2, finding.Range.Start.Line);
        Assert.Contains("buuld", finding.Message, StringComparison.Ordinal);
        Assert.Equal("Did you mean 'build'?", finding.Suggestion);

        // The edge is dropped rather than guessed, so deploy is still a job the layout can place.
        Assert.Empty(result.Graph.DependenciesOf("deploy"));
        Assert.Equal("build,deploy", Ids(result.Graph.TopologicalOrder));
        Assert.Equal("build:0:0|deploy:0:1", Signature(result.Layout));
        Assert.False(result.IsTrustworthy);
    }

    [Theory]
    [InlineData("build:;deploy=buuld", "Did you mean 'build'?")]
    [InlineData("build:;deploy=uild", "Did you mean 'build'?")]
    [InlineData("build:;deploy=BUILD", "Did you mean 'build'?")]
    [InlineData("test:;deploy=somethingelse", null)]
    public void Build_NeedsUnknownJob_SuggestsOnlyAnObviousTypo(string spec, string? expected)
    {
        var finding = Assert.Single(JobGraph.Build(Spec(spec)).Diagnostics);

        if (expected is null)
        {
            Assert.NotNull(finding.Suggestion);
            Assert.DoesNotContain("Did you mean", finding.Suggestion, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(expected, finding.Suggestion);
        }
    }

    [Fact]
    public void Build_NeedsUnknownJob_WithholdsSuggestionWhenTwoIdsAreEquallyClose()
    {
        var finding = Assert.Single(JobGraph.Build(Spec("buila:;builb:;deploy=build")).Diagnostics);

        // Deliberate choice: no suggestion beats a coin flip between 'buila' and 'builb'.
        Assert.DoesNotContain("Did you mean", finding.Suggestion ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DuplicateNeedsEntry_KeepsOneEdgeAndWarns()
    {
        var result = JobGraph.Build(Spec("build:;deploy=build build build"));
        Assert.Equal(2, result.Diagnostics.Count);
        Assert.All(result.Diagnostics, finding =>
        {
            Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
            Assert.Equal(DiagnosticStage.Graph, finding.Stage);
            Assert.Equal("deploy", finding.Scope);
            Assert.Contains("twice", finding.Message, StringComparison.Ordinal);
        });

        Assert.Equal("build", Ids(result.Graph.DependenciesOf("deploy")));
        Assert.Single(result.Graph.Edges);
        Assert.Equal("build:0:0|deploy:1:0", Signature(result.Layout));

        // Redundant, not illegal: GitHub ignores the extra entry, so the graph stays trustworthy.
        Assert.True(result.IsTrustworthy);
    }

    [Theory]
    [InlineData("a=a", "a -> a")]
    [InlineData("b=a;a=b", "b -> a -> b")]
    [InlineData("a=b;b=c;c=a", "a -> b -> c -> a")]
    public void Build_Cycle_NamesTheClosedPathOnce(string spec, string expectedPath)
    {
        var result = JobGraph.Build(Spec(spec));
        var graph = result.Graph;

        var cycle = Assert.Single(graph.Cycles);
        Assert.Equal(expectedPath, string.Join(" -> ", cycle));

        var finding = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.NeedsCycle, finding.Code);
        Assert.Equal(DiagnosticSeverity.Error, finding.Severity);
        Assert.Contains(expectedPath, finding.Message, StringComparison.Ordinal);

        Assert.False(graph.IsAcyclic);
        Assert.Empty(graph.TopologicalOrder);
        Assert.Equal(Ids(graph.BlockedJobs), Ids(graph.JobsInCycles));
        Assert.Empty(result.Layout.Nodes);
        Assert.Equal(Ids(graph.BlockedJobs), Ids(result.Layout.ExcludedJobs));
    }

    [Fact]
    public void Build_Cycle_BesideAHealthyChainLeavesTheChainLaidOut()
    {
        var result = JobGraph.Build(Spec("a:;b=a;c=b;x=y;y=x;after=c"));
        var graph = result.Graph;

        // Deliberate choice: the walk starts at the earliest job in document order, so the one cycle
        // found is reported once, from the edge that closed it.
        Assert.Equal("x -> y -> x", string.Join(" -> ", Assert.Single(graph.Cycles)));
        Assert.Equal(DiagnosticCodes.NeedsCycle, Assert.Single(result.Diagnostics).Code);

        Assert.Equal("a,b,c,after", Ids(graph.TopologicalOrder));
        Assert.Equal("x,y", Ids(graph.JobsInCycles));
        Assert.Equal("x,y", Ids(graph.BlockedJobs));
        Assert.False(graph.IsInCycle("after"));
        Assert.Equal("a:0:0|b:1:0|c:2:0|after:3:0", Signature(result.Layout));
        Assert.Equal("x,y", Ids(result.Layout.ExcludedJobs));
        Assert.False(result.IsTrustworthy);
    }

    [Fact]
    public void Build_JobBehindACycle_IsBlockedBecauseItsPositionIsNotDerivable()
    {
        var result = JobGraph.Build(Spec("start:;a=b;b=a;tail=a"));

        // Deliberate choice: waiting on a cycle is not the same as being in one, but neither job has a
        // layer that is not invented, so both stay out of the order and only the cycle is reported.
        Assert.Equal("start", Ids(result.Graph.TopologicalOrder));
        Assert.Equal("a,b,tail", Ids(result.Graph.BlockedJobs));
        Assert.Equal("a,b", Ids(result.Graph.JobsInCycles));
        Assert.False(result.Graph.IsInCycle("tail"));
        Assert.Single(result.Graph.Cycles);
        Assert.Equal(1, result.Layout.NodeCount);
        Assert.Equal("start:0:0", Signature(result.Layout));
    }

    [Fact]
    public void Build_TwoSeparateCycles_ReportsBothInDocumentOrder()
    {
        var result = JobGraph.Build(Spec("a=b;b=a;c=d;d=c"));

        Assert.Equal("a -> b -> a|c -> d -> c",
            string.Join("|", result.Graph.Cycles.Select(cycle => string.Join(" -> ", cycle))));
        Assert.Equal(2, result.Diagnostics.Count);
        Assert.All(result.Diagnostics, finding => Assert.Equal(DiagnosticCodes.NeedsCycle, finding.Code));
        Assert.Equal("a,b,c,d", Ids(result.Graph.JobsInCycles));
    }

    [Fact]
    public void Build_SelfAndUnknownNeeds_ReportedWithoutThrowing()
    {
        var result = JobGraph.Build(Spec("solo=solo;other=nowhere"));

        Assert.Equal(2, result.Diagnostics.Count);

        // Deliberate ordering: needs problems first, in document order, then the cycles they left behind.
        Assert.Equal(DiagnosticCodes.NeedsUnknownJob, result.Diagnostics[0].Code);
        Assert.Equal(DiagnosticCodes.NeedsCycle, result.Diagnostics[1].Code);
        Assert.Equal("solo -> solo", string.Join(" -> ", Assert.Single(result.Graph.Cycles)));
        Assert.Equal("solo", Ids(result.Graph.JobsInCycles));
        Assert.Equal("solo", Ids(result.Graph.BlockedJobs));
        Assert.Equal("other", Ids(result.Graph.TopologicalOrder));
    }

    // ---------------------------------------------------------------- determinism

    [Fact]
    public void Build_TwiceProducesIdenticalLayersSlotsAndFindings()
    {
        const string shape = "a:;b=a;c=a ghost;d=b c;e=d;loop=e x;spin=loop;spin2=spin;spin3=spin2";
        var first = JobGraph.Build(Spec(shape));
        var second = JobGraph.Build(Spec(shape));

        Assert.Equal(Signature(first.Layout), Signature(second.Layout));
        Assert.Equal(Ids(first.Graph.TopologicalOrder), Ids(second.Graph.TopologicalOrder));
        Assert.Equal(Widths(first.Layout), Widths(second.Layout));
        Assert.Equal(first.Layout.Edges.Select(edge => edge.ToString()),
            second.Layout.Edges.Select(edge => edge.ToString()));
        Assert.Equal(first.Diagnostics.Select(finding => finding.Message),
            second.Diagnostics.Select(finding => finding.Message));
    }

    [Fact]
    public void Build_ReversingTheJobOrderMovesTheSlotsWithIt()
    {
        // Slots track document order by decision, so reordering the file must reorder the drawing —
        // otherwise two authors' equivalent YAMLs would render differently for no reason.
        var forward = JobGraph.Build(Spec("shared:;alpha=shared;beta=shared"));
        var backward = JobGraph.Build(Spec("shared:;beta=shared;alpha=shared"));

        Assert.Equal("shared:0:0|alpha:1:0|beta:1:1", Signature(forward.Layout));
        Assert.Equal("shared:0:0|beta:1:0|alpha:1:1", Signature(backward.Layout));
        Assert.Equal("shared,alpha,beta", Ids(forward.Graph.TopologicalOrder));
        Assert.Equal("shared,beta,alpha", Ids(backward.Graph.TopologicalOrder));
    }

    // ---------------------------------------------------------------- reachability

    [Fact]
    public void Reachability_OnACyclicGraphTerminatesWithTheExpectedSets()
    {
        var graph = JobGraph.Build(Spec("root:;a=b;b=a;down=a;side=root")).Graph;

        Assert.Equal("a,b", Ids(graph.AncestorsOf("down")));
        Assert.Equal("root", Ids(graph.AncestorsOf("side")));
        Assert.Equal("b,down", Ids(graph.DescendantsOf("a")));
        Assert.Empty(graph.DescendantsOf("side"));

        // Deliberate choice: a job is never its own ancestor or descendant, even when a cycle makes it
        // reachable from itself — the scenario engine asks about the state of other jobs.
        Assert.Equal("b", Ids(graph.AncestorsOf("a")));
        Assert.DoesNotContain("down", graph.AncestorsOf("down"));
        Assert.DoesNotContain("a", graph.DescendantsOf("a"));

        Assert.False(graph.Contains("missing"));
        Assert.Empty(graph.AncestorsOf("missing"));
        Assert.Empty(graph.DependenciesOf("missing"));
        Assert.Empty(graph.DependentsOf("missing"));
    }

    [Fact]
    public void Reachability_UsesOnlyTheEdgesThatSurvivedValidation()
    {
        var graph = JobGraph.Build(Spec("a:;b=a ghost;c=b")).Graph;

        Assert.Equal("a", Ids(graph.AncestorsOf("b")));
        Assert.Equal("b,c", Ids(graph.DescendantsOf("a")));
        Assert.Empty(graph.DependentsOf("ghost"));
        Assert.Equal("c", Ids(graph.DependentsOf("b")));
        Assert.Equal("b", Ids(graph.DependentsOf("a")));
    }

    // ---------------------------------------------------------------- edges of the API

    [Fact]
    public void Build_EmptyWorkflow_YieldsAnEmptyGraphWithoutFindings()
    {
        var result = JobGraph.Build(Array.Empty<JobDefinition>());

        Assert.True(result.Graph.IsEmpty);
        Assert.Equal(0, result.Graph.JobCount);
        Assert.Empty(result.Diagnostics);
        Assert.True(result.IsTrustworthy);

        Assert.Empty(result.Layout.Nodes);
        Assert.Equal(0, result.Layout.LayerCount);
        Assert.Equal(0, result.Layout.MaxLayerWidth);
        Assert.Empty(result.Layout.ExcludedJobs);
        Assert.Null(result.Layout.NodeFor("anything"));
    }

    [Fact]
    public void Build_ReadsTheJobsOfAWorkflowDocumentInDocumentOrder()
    {
        var workflow = new WorkflowDocument
        {
            Name = "ci",
            Triggers = new TriggerSection { Events = new Dictionary<string, EventTrigger>(StringComparer.Ordinal) },
            Jobs = Spec("prepare:;build=prepare;publish=build"),
        };

        var result = JobGraph.Build(workflow);

        Assert.Equal(3, result.Graph.JobCount);
        Assert.Equal("prepare", Ids(result.Graph.DependenciesOf("build")));
        Assert.Equal("prepare,build,publish", Ids(result.Graph.TopologicalOrder));
        Assert.Equal("publish:2:0", Signature(result.Layout).Split('|')[2]);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Builds jobs from <c>id:needs</c> / <c>id=needs needs</c> text, one job per entry, so a whole
    /// workflow shape fits on one line. Each job gets its own source line, which is how the range
    /// assertions tell one job's finding from another's.
    /// </summary>
    private static JobDefinition[] Spec(string spec)
    {
        var entries = spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var jobs = new List<JobDefinition>(entries.Length);

        for (var i = 0; i < entries.Length; i++)
        {
            var bits = entries[i].Split('=', 2);
            var needs = bits.Length == 1 || bits[1].Length == 0
                ? Array.Empty<string>()
                : bits[1].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // 'lint:' is this DSL's spelling of a job with no needs, so the colon is punctuation.
            jobs.Add(Job(i + 1, bits[0].TrimEnd(':'), needs));
        }

        return jobs.ToArray();
    }

    private static JobDefinition Job(int line, string id, params string[] needs)
    {
        var sources = new SourceMap();
        sources.Set("job", new SourceRange(new SourcePosition(line, 3)));
        if (needs.Length > 0)
        {
            sources.Set("needs", new SourceRange(new SourcePosition(line, 5), new SourcePosition(line, 20)));
        }

        return new JobDefinition
        {
            Id = id,
            KeyRange = new SourceRange(new SourcePosition(line, 3)),
            Needs = needs,
            Sources = sources,
        };
    }

    /// <summary>The layout as one string: every placed job as <c>id:layer:slot</c>, in draw order.</summary>
    private static string Signature(LayeredLayout layout) =>
        string.Join("|", layout.Nodes.Select(node => $"{node.JobId}:{node.Layer}:{node.Slot}"));

    private static string Ids(IEnumerable<string> ids) => string.Join(",", ids);

    private static string Widths(LayeredLayout layout) => string.Join(",", layout.LayerWidths);
}
