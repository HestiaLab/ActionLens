using ActionLens.Core.Diagnostics;
using ActionLens.Core.Text;

namespace ActionLens.Core.Expressions;

/// <summary>
/// One piece of a templated value: either literal text or one <c>${{ }}</c> substitution. A condition
/// produces a single segment with <see cref="LiteralText"/> null; embedded text alternates literal,
/// expression, literal in source order.
/// </summary>
/// <param name="LiteralText">
/// The text exactly as written, when the segment is not an expression. Null for an expression segment.
/// </param>
/// <param name="Expression">The parsed expression, or null for a literal segment.</param>
/// <param name="Range">
/// The span of the whole segment. For an expression segment that includes the <c>${{</c> and <c>}}</c>
/// when they are written; the expression node itself spans only the body.
/// </param>
public sealed record TemplateSegment(string? LiteralText, ExpressionNode? Expression, SourceRange Range);

/// <summary>
/// What parsing produced: the segments of the value, and everything that was wrong with it.
/// </summary>
/// <remarks>
/// A non-empty <see cref="Diagnostics"/> means the value cannot be trusted. Each diagnostic already
/// carries an absolute document position, so no caller has to re-base it against the YAML span the
/// value came from.
/// </remarks>
public sealed record ExpressionParseOutcome(
    IReadOnlyList<TemplateSegment> Segments,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>
    /// The root of the one parsed expression, or null when the text held no expression at all, when it
    /// held more than one, or when parsing failed.
    /// </summary>
    /// <remarks>
    /// Syntax findings (<see cref="DiagnosticCodes.ExpressionSyntax"/> and
    /// <see cref="DiagnosticCodes.ExpressionTooComplex"/>) always leave this null, because the tree they
    /// describe is incomplete. A <see cref="DiagnosticCodes.FunctionArity"/> finding does not: the tree is
    /// complete and legal syntax, only the call is wrong, so the evaluator can still walk it.
    /// </remarks>
    public ExpressionNode? Root
    {
        get
        {
            ExpressionNode? root = null;
            foreach (var segment in Segments)
            {
                if (segment.Expression is { } expression)
                {
                    if (root is not null)
                    {
                        return null;
                    }

                    root = expression;
                }
            }

            return root;
        }
    }
}

/// <summary>
/// Parses the GitHub Actions expression language into <see cref="ExpressionNode"/> trees.
/// </summary>
/// <remarks>
/// <para>
/// Parsing is total: no input, however broken, throws. Anything that does not parse becomes at least one
/// positioned diagnostic and a null <see cref="ExpressionParseOutcome.Root"/>.
/// </para>
/// <para>
/// This stage is syntax only. It resolves no contexts, coerces no values and applies no three-state
/// logic, and an unknown function name stays legal syntax here for that reason: only the evaluator can
/// say whether a name is available in the position where it was written.
/// </para>
/// <para>
/// Positions are absolute inside the text handed in. A <see cref="SourceRange"/> therefore reports line 1
/// for a value passed on its own, and the real workflow line when the caller passes the whole document
/// with an offset - which is how the YAML and Workflows layers are expected to call this.
/// </para>
/// </remarks>
public static class ExpressionParser
{
    /// <summary>
    /// The longest expression body either reference implementation accepts. This limit is not in the
    /// public documentation: it comes from <c>MaxExpressionLength</c> in the actions/runner SDK and the
    /// matching constant in actions/languageservices, which agree on the number.
    /// </summary>
    public const int MaxExpressionLength = 21000;

    /// <summary>
    /// The deepest parser recursion either reference implementation accepts. It counts nesting rather
    /// than tokens — one unit for a prefix <c>!</c>, for a member or index link, for a function call, for
    /// a group and for each operand of a logical chain. Also undocumented; both implementations agree, and
    /// the official corpus tests it as 49 nested <c>!</c> passing while 50 fails, and 49 nested calls
    /// passing while 50 fails.
    /// </summary>
    public const int MaxDepth = 50;

    /// <summary>
    /// Parses an <c>if:</c> value, where the whole text is one expression and the <c>${{ }}</c> wrapper is
    /// optional: <c>success()</c> and <c>${{ success() }}</c> are the same condition.
    /// </summary>
    /// <remarks>
    /// Text with no expression syntax at all is still parsed as an expression, because that is what a
    /// condition is: <c>if: github.ref == 'refs/heads/main'</c> carries no wrapper and must still produce a
    /// tree. A value that is empty after trimming yields no segments and no diagnostics - that is a missing
    /// value, which is the schema stage's business, not an expression problem. An empty <c>${{ }}</c>
    /// wrapper is an expression problem and gets <see cref="DiagnosticCodes.ExpressionSyntax"/>.
    /// <para>
    /// <paramref name="origin"/> is where the value's first character sits in the workflow file. Callers
    /// that parsed a standalone expression leave it null and get positions relative to that expression;
    /// callers reading an <c>if:</c> pass the YAML position so every token in the reason trace points at a
    /// real place in the file instead of at <c>1:1</c>.
    /// </para>
    /// </remarks>
    public static ExpressionParseOutcome ParseCondition(string text, SourcePosition? origin = null)
    {
        var source = text ?? string.Empty;
        var positions = SourcePositionMap.Build(source, origin);
        var parser = new ExpressionBodyParser(source, positions);
        var diagnostics = new List<Diagnostic>();
        var (start, end) = TrimSpan(source, 0, source.Length);

        if (start == end)
        {
            return new ExpressionParseOutcome([], []);
        }

        var bodyStart = start;
        var bodyEnd = end;
        var segmentEnd = end;

        if (source.AsSpan(start, end - start).StartsWith("${{", StringComparison.Ordinal))
        {
            var close = end - bodyStart >= 2
                ? source.IndexOf("}}", bodyStart, end - bodyStart, StringComparison.Ordinal)
                : -1;
            if (close < 0)
            {
                return new ExpressionParseOutcome([], [
                    ExpressionErrors.Syntax(
                        "The expression is missing its closing '}}'.",
                        new SourceRange(positions.At(start), positions.At(Math.Min(start + 3, source.Length))),
                        "End the expression with '}}'."),
                ]);
            }

            bodyStart = start + 3;
            bodyEnd = close;
            segmentEnd = close + 2;

            var (tailStart, tailEnd) = TrimSpan(source, close + 2, end);
            if (tailStart != tailEnd)
            {
                return new ExpressionParseOutcome([], [
                    ExpressionErrors.Syntax(
                        "Unexpected text after the closing '}}' of the expression.",
                        new SourceRange(positions.At(tailStart), positions.At(tailEnd)),
                        "A condition is one expression; put the rest inside it or drop the wrapper."),
                ]);
            }
        }

        var (trimmedStart, trimmedEnd) = TrimSpan(source, bodyStart, bodyEnd);
        if (LengthFinding(source, trimmedStart, trimmedEnd, positions) is { } tooLong)
        {
            return new ExpressionParseOutcome([], [tooLong]);
        }

        var root = parser.ParseBody(trimmedStart, trimmedEnd);
        Collect(parser, 0, diagnostics);
        if (root is null)
        {
            return new ExpressionParseOutcome([], diagnostics);
        }

        var segment = new TemplateSegment(
            null,
            root,
            new SourceRange(positions.At(start), positions.At(segmentEnd)));
        return new ExpressionParseOutcome([segment], diagnostics);
    }

    /// <summary>
    /// Parses a <c>run:</c>, <c>env:</c>, <c>with:</c> or <c>name:</c> value: literal text that may hold any
    /// number of <c>${{ }}</c> substitutions. Literal text is never parsed as an expression, so a stray
    /// <c>}}</c> or a <c>+</c> in a shell command is not a finding.
    /// </summary>
    /// <remarks>
    /// Text with no <c>${{ }}</c> produces exactly one literal segment and no diagnostics. A body that fails
    /// to parse contributes its diagnostics but no segment: the diagnostic already carries the position, and
    /// half a tree would be worse than none.
    /// </remarks>
    public static ExpressionParseOutcome ParseEmbedded(string text)
    {
        var source = text ?? string.Empty;
        var positions = SourcePositionMap.Build(source);
        var parser = new ExpressionBodyParser(source, positions);
        var segments = new List<TemplateSegment>();
        var diagnostics = new List<Diagnostic>();
        var consumed = 0;
        var cursor = 0;

        while (true)
        {
            var open = source.IndexOf("${{", cursor, source.Length - cursor, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            AddLiteral(segments, source, cursor, open, positions);
            consumed = Collect(parser, consumed, diagnostics);

            var bodyFrom = open + 3;
            var close = source.Length - bodyFrom >= 2
                ? source.IndexOf("}}", bodyFrom, source.Length - bodyFrom, StringComparison.Ordinal)
                : -1;
            if (close < 0)
            {
                // Keep the remainder as literal text so the value is still whole, and blame the opening.
                AddLiteral(segments, source, open, source.Length, positions);
                diagnostics.Add(ExpressionErrors.Syntax(
                    "The expression is missing its closing '}}'.",
                    new SourceRange(positions.At(open), positions.At(Math.Min(bodyFrom, source.Length))),
                    "End the expression with '}}'."));
                return new ExpressionParseOutcome(segments, diagnostics);
            }

            var segmentRange = new SourceRange(positions.At(open), positions.At(close + 2));
            var (bodyStart, bodyEnd) = TrimSpan(source, bodyFrom, close);
            if (LengthFinding(source, bodyStart, bodyEnd, positions) is { } tooLong)
            {
                diagnostics.Add(tooLong);
                cursor = close + 2;
                continue;
            }

            var root = parser.ParseBody(bodyStart, bodyEnd);

            // A non-null root means the tree is complete: the parser only returns null on a syntax or
            // depth finding. An arity finding is therefore kept as a diagnostic next to a usable segment.
            if (root is not null)
            {
                segments.Add(new TemplateSegment(null, root, segmentRange));
            }

            consumed = Collect(parser, consumed, diagnostics);
            cursor = close + 2;
        }

        AddLiteral(segments, source, cursor, source.Length, positions);
        return new ExpressionParseOutcome(segments, diagnostics);
    }

    /// <summary>
    /// The length gate. It measures the body after trimming its outer whitespace, so padding around the
    /// expression does not count while every character inside it - quotes included - does.
    /// </summary>
    private static Diagnostic? LengthFinding(string source, int start, int end, SourcePositionMap positions)
    {
        var length = end - start;
        if (length <= MaxExpressionLength)
        {
            return null;
        }

        return ExpressionErrors.TooComplex(
            $"Exceeded max expression length {MaxExpressionLength}.",
            new SourceRange(positions.At(start), positions.At(end)),
            "Split the expression into smaller parts, or move the logic into the script.");
    }

    private static void AddLiteral(
        List<TemplateSegment> segments,
        string source,
        int start,
        int end,
        SourcePositionMap positions)
    {
        if (end > start)
        {
            segments.Add(new TemplateSegment(
                source.AsSpan(start, end - start).ToString(),
                null,
                new SourceRange(positions.At(start), positions.At(end))));
        }
    }

    /// <summary>Moves the diagnostics the parser added since <paramref name="consumed"/> into the sink.</summary>
    private static int Collect(ExpressionBodyParser parser, int consumed, List<Diagnostic> sink)
    {
        while (consumed < parser.Diagnostics.Count)
        {
            sink.Add(parser.Diagnostics[consumed]);
            consumed++;
        }

        return consumed;
    }

    /// <summary>The span without its surrounding whitespace. Positions stay absolute.</summary>
    private static (int Start, int End) TrimSpan(string source, int start, int end)
    {
        while (start < end && IsWhitespace(source[start]))
        {
            start++;
        }

        while (end > start && IsWhitespace(source[end - 1]))
        {
            end--;
        }

        return (start, end);
    }

    private static bool IsWhitespace(char value) =>
        value is ' ' or '\t' or '\n' or '\r' or '\f' or '\v';
}
