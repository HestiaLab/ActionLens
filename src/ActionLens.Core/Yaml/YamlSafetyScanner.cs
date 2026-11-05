using System.Text;
using ActionLens.Core.Diagnostics;
using ActionLens.Core.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace ActionLens.Core.Yaml;

/// <summary>
/// First pass over the workflow text. It runs on the raw event stream, before any object graph is
/// built, so that hostile input is rejected while it is still just a sequence of tokens: alias
/// bombs, unbounded nesting, multi-document files and duplicate keys are all answered here with a
/// position, and the model builder never has to defend itself against them.
/// </summary>
internal static class YamlSafetyScanner
{
    /// <summary>A workflow that needs more than this many aliases is doing something YAML-bomb-shaped.</summary>
    internal const int MaxAliasCount = 128;

    /// <summary>Guards the recursive walkers downstream, which are only safe up to this depth.</summary>
    internal const int MaxDepth = 64;

    /// <summary>
    /// A ceiling on tokens rather than on size: 200k events is far beyond any hand-written workflow,
    /// and it bounds the scan itself against a file that is 1 MB of pure nesting.
    /// </summary>
    internal const int MaxEventCount = 200_000;

    internal sealed record ScanResult(bool CanContinue, IReadOnlyList<Diagnostic> Diagnostics);

    internal static ScanResult Scan(string yaml)
    {
        var diagnostics = new List<Diagnostic>();
        var frames = new Stack<Frame>();
        var documents = 0;
        var aliases = 0;
        var duplicates = 0;
        var events = 0;
        var contentDepth = 0;
        var tooDeep = false;
        var stoppedEarly = false;

        // A mapping's children alternate key, value, key, value. Sequences do not need the bookkeeping
        // because duplicate detection only applies to mapping keys, so a frame remembers whether it is
        // one or the other and the next leaf or container fills whichever slot it lands in.
        void HandleLeaf(string? keyText, SourceRange range)
        {
            if (frames.Count == 0)
            {
                return;
            }

            var frame = frames.Peek();
            if (!frame.IsMapping)
            {
                return;
            }

            if (frame.ExpectKey)
            {
                if (keyText is not null)
                {
                    if (frame.Keys.TryGetValue(keyText, out var first))
                    {
                        duplicates++;
                        diagnostics.Add(new Diagnostic
                        {
                            Code = DiagnosticCodes.YamlDuplicateKey,
                            Severity = DiagnosticSeverity.Error,
                            Stage = DiagnosticStage.Yaml,
                            Message = $"Duplicate key '{keyText}'.",
                            Range = range,
                            Suggestion = first.IsKnown
                                ? $"Keep only one '{keyText}'; the first was defined at {first}."
                                : $"Keep only one '{keyText}'.",
                        });
                    }
                    else
                    {
                        frame.Keys.Add(keyText, range);
                    }
                }

                frame.ExpectKey = false;
            }
            else
            {
                frame.ExpectKey = true;
            }
        }

        void EnterContainer(bool isMapping, SourceRange range)
        {
            var childWasKey = false;
            if (frames.Count > 0)
            {
                var parent = frames.Peek();
                if (parent.IsMapping)
                {
                    childWasKey = parent.ExpectKey;
                    parent.ExpectKey = false;
                }
            }

            frames.Push(new Frame(isMapping, range) { ChildWasKey = childWasKey });
        }

        void ExitContainer()
        {
            if (frames.Count == 0)
            {
                return;
            }

            var frame = frames.Pop();
            if (frames.Count > 0)
            {
                var parent = frames.Peek();
                if (parent.IsMapping)
                {
                    // A key container is followed by a value; a value container is followed by a key.
                    parent.ExpectKey = !frame.ChildWasKey;
                }
            }
        }

        try
        {
            var parser = new Parser(new StringReader(yaml));
            while (parser.MoveNext())
            {
                if (++events > MaxEventCount)
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Code = DiagnosticCodes.YamlSizeLimit,
                        Severity = DiagnosticSeverity.Error,
                        Stage = DiagnosticStage.Yaml,
                        Message = $"The workflow contains more than {MaxEventCount} YAML tokens.",
                        Suggestion = "Split the workflow into smaller files, or check whether it was generated.",
                    });
                    stoppedEarly = true;
                    break;
                }

                switch (parser.Current)
                {
                    case DocumentStart:
                        documents++;
                        break;

                    case AnchorAlias alias:
                        aliases++;
                        HandleLeaf(null, ToRange(alias));
                        break;

                    case MappingStart start:
                        tooDeep |= ++contentDepth > MaxDepth;
                        EnterContainer(isMapping: true, ToRange(start));
                        break;

                    case SequenceStart start:
                        tooDeep |= ++contentDepth > MaxDepth;
                        EnterContainer(isMapping: false, ToRange(start));
                        break;

                    case MappingEnd or SequenceEnd:
                        contentDepth = Math.Max(contentDepth - 1, 0);
                        ExitContainer();
                        break;

                    case Scalar scalar:
                        HandleLeaf(scalar.Value, ToRange(scalar));
                        break;
                }
            }
        }
        catch (YamlException ex)
        {
            diagnostics.Add(ToDiagnostic(ex));
            return new ScanResult(false, diagnostics);
        }

        if (documents > 1)
        {
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.YamlMultipleDocuments,
                Severity = DiagnosticSeverity.Error,
                Stage = DiagnosticStage.Yaml,
                Message = $"The file contains {documents} YAML documents; a workflow file must contain exactly one.",
                Suggestion = "Remove the '---' separator so that only one workflow remains in the file.",
            });
        }

        if (aliases > MaxAliasCount)
        {
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.YamlAliasLimit,
                Severity = DiagnosticSeverity.Error,
                Stage = DiagnosticStage.Yaml,
                Message = $"The workflow resolves {aliases} anchors aliases, above the {MaxAliasCount} ActionLens accepts.",
                Suggestion = "Reduce reuse with anchors, or analyse the jobs that use them separately.",
            });
        }

        if (tooDeep)
        {
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.YamlDepthLimit,
                Severity = DiagnosticSeverity.Error,
                Stage = DiagnosticStage.Yaml,
                Message = $"The workflow nests deeper than {MaxDepth} levels.",
                Suggestion = "Flatten the structure; nesting this deep usually means the file was generated.",
            });
        }

        // A duplicate key stops the pass because the object loader will not build a graph from it, not
        // because ActionLens chooses to be strict: GitHub rejects such a workflow too, and reporting
        // every duplicate found (rather than only the first the loader trips on) is the useful answer.
        var canContinue = documents <= 1 && aliases <= MaxAliasCount && !tooDeep && !stoppedEarly && duplicates == 0;
        return new ScanResult(canContinue, diagnostics);
    }

    /// <summary>Turns a YamlDotNet failure into a positioned diagnostic; parsing must never escape as an exception.</summary>
    internal static Diagnostic ToDiagnostic(YamlException ex) => new()
    {
        Code = DiagnosticCodes.YamlSyntax,
        Severity = DiagnosticSeverity.Error,
        Stage = DiagnosticStage.Yaml,
        Message = $"The file is not valid YAML: {new StringBuilder(ex.Message.Replace('\n', ' ').Trim())}.",
        Range = ToRange(ex),
        Suggestion = "Fix the indentation or quoting at the reported position.",
    };

    private static SourceRange ToRange(ParsingEvent evt) => new(ToPosition(evt.Start), ToPosition(evt.End));

    private static SourceRange ToRange(YamlException ex) => new(ToPosition(ex.Start), ToPosition(ex.End));

    /// <summary>YamlDotNet marks are already 1-based, which is what an editor shows.</summary>
    private static SourcePosition ToPosition(Mark mark) =>
        new((int)Math.Max(mark.Line, 1), (int)Math.Max(mark.Column, 1));

    private sealed class Frame(bool isMapping, SourceRange range)
    {
        public bool IsMapping { get; } = isMapping;

        public SourceRange Range { get; } = range;

        public bool ExpectKey { get; set; } = true;

        /// <summary>Whether this container filled the key slot of its parent mapping.</summary>
        public bool ChildWasKey { get; init; }

        public Dictionary<string, SourceRange> Keys { get; } = new(StringComparer.Ordinal);
    }
}
