using System.Globalization;
using System.Text;
using ActionLens.Core.Text;
using ActionLens.Core.Values;

namespace ActionLens.Core.Expressions;

/// <summary>
/// Evaluates a parsed Actions expression into a value, or into the reason no value is knowable.
/// </summary>
/// <remarks>
/// Three rules govern everything below.
/// <list type="number">
///   <item><see cref="TriKind.Unknown"/> propagates but is never invented. An operand that needs a
///     running job makes the result unknown with a trace naming that operand, and no amount of
///     plausible guessing turns it into a value.</item>
///   <item>Logical operators return an <em>operand</em>, not a boolean, because that is what Actions
///     does: <c>false || 0</c> is the number 0. Only the outermost <c>if:</c> result is coerced to a
///     condition, in <see cref="ConditionEvaluator"/>.</item>
///   <item>Functions and contexts are restricted by position before they are restricted by knowledge.
///     <c>secrets</c> in an <c>if:</c> is not "unknown because it is secret" — it is unavailable in
///     that position, and GitHub would reject the workflow, so the reason says so.</item>
/// </list>
/// </remarks>
public sealed class ExpressionEvaluator
{
    private readonly IContextProvider _contexts;

    public ExpressionEvaluator(IContextProvider contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <summary>Evaluates one node of an expression tree.</summary>
    public Evaluated Evaluate(ExpressionNode node) => node switch
    {
        LiteralNode literal => Evaluated.Known(literal.Value),
        ContextNode context => ResolveContext(context),
        MemberAccessNode member => EvaluateMember(member),
        IndexAccessNode index => EvaluateIndex(index),
        WildcardAccessNode wildcard => EvaluateWildcard(wildcard),
        UnaryNotNode notNode => EvaluateNot(notNode),
        LogicalNode logical => EvaluateLogical(logical),
        ComparisonNode comparison => EvaluateComparison(comparison),
        FunctionCallNode call => EvaluateFunction(call),
        _ => Evaluated.Unknown(new UnknownReason(
            UnknownReasonKind.InvalidSyntax,
            "Unsupported expression node.",
            node.Range)),
    };

    /// <summary>Coerces the result to a condition, keeping the reason when the value is unknown.</summary>
    public TriValue EvaluateCondition(ExpressionNode node) => Evaluate(node).ToCondition();

    /// <summary>
    /// Evaluates one operand and, when it is unknown, wraps the reason as a propagation through the
    /// enclosing expression — the shape every link and operator shares.
    /// </summary>
    private Evaluated EvaluateOrPropagate(ExpressionNode operand, string text, SourceRange range)
    {
        var value = Evaluate(operand);
        return value.IsKnown
            ? value
            : Evaluated.Unknown(UnknownReason.Propagating(text, range, value.Reason!));
    }

    // ---------------------------------------------------------------- paths

    private Evaluated ResolveContext(ContextNode node)
    {
        if (!_contexts.IsAvailable(node.Name))
        {
            return Evaluated.Unknown(_contexts.ExplainUnavailable(node.Name) with { Symbol = node.Name });
        }

        return _contexts.TryGetContext(node.Name, out var context)
            ? Evaluated.Known(context)
            : Evaluated.Unknown(_contexts.ExplainContextUnknown(node.Name));
    }

    private Evaluated EvaluateMember(MemberAccessNode node)
    {
        var target = EvaluateOrPropagate(node.Target, ExpressionTreeText.Render(node), node.Range);
        if (!target.IsKnown)
        {
            return target;
        }

        var value = target.Value!;
        if (FollowsWildcard(node.Target) && value.Kind == GitHubValueKind.Array)
        {
            var key = GitHubValue.FromString(node.Name);
            return Evaluated.Known(MapEach(value, child =>
                child.TryGetMember(key, StringComparison.OrdinalIgnoreCase, out var member) ? member : null));
        }

        // A property that does not exist reads as null. The docs call this an empty string while both
        // reference implementations return null; the difference is visible only through toJSON() or an
        // explicit '== null', and null is what the implementations that run real workflows do.
        if (!value.TryGetMember(GitHubValue.FromString(node.Name), StringComparison.OrdinalIgnoreCase, out var element))
        {
            element = GitHubValue.Null;
        }

        return Evaluated.Known(element);
    }

    private Evaluated EvaluateIndex(IndexAccessNode node)
    {
        var target = EvaluateOrPropagate(node.Target, ExpressionTreeText.Render(node), node.Range);
        if (!target.IsKnown)
        {
            return target;
        }

        var index = EvaluateOrPropagate(node.Index, "index", node.Range);
        if (!index.IsKnown)
        {
            return index;
        }

        var value = target.Value!;
        if (FollowsWildcard(node.Target) && value.Kind == GitHubValueKind.Array)
        {
            var key = index.Value!;
            return Evaluated.Known(MapEach(value, child =>
                child.TryGetIndex(key, StringComparison.OrdinalIgnoreCase, out var mapped) ? mapped : null));
        }

        if (!value.TryGetIndex(index.Value!, StringComparison.OrdinalIgnoreCase, out var element))
        {
            element = GitHubValue.Null;
        }

        return Evaluated.Known(element);
    }

    /// <summary>
    /// <c>a.*</c> and <c>a[*]</c>: the elements of an array or the property values of an object, collected
    /// into a new array. Any other value has nothing to spread, so the answer is the empty array rather
    /// than an error or the value itself (<c>op_idx_star.json</c>, <c>filtered-array-from-literal</c>).
    /// </summary>
    private Evaluated EvaluateWildcard(WildcardAccessNode node)
    {
        var target = EvaluateOrPropagate(node.Target, ExpressionTreeText.Render(node), node.Range);
        if (!target.IsKnown)
        {
            return target;
        }

        var value = target.Value!;
        if (FollowsWildcard(node.Target) && value.Kind == GitHubValueKind.Array)
        {
            // A second wildcard in the same path spreads each entry of the filtered array and flattens
            // the result, which is how <c>foo.*.*</c> reaches the values nested one level below it.
            var spread = new List<GitHubValue>();
            foreach (var child in value.Array)
            {
                spread.AddRange(child.Children);
            }

            return Evaluated.Known(GitHubValue.FromArray(spread));
        }

        return Evaluated.Known(GitHubValue.FromArray(value.Children.ToArray()));
    }

    /// <summary>
    /// Whether a link comes after a wildcard in the same path. A wildcard is sticky for the rest of the
    /// path, which is what <c>foo.*.p.nestedp</c> needs: both links after the <c>.*</c> map, so the answer
    /// is the nested property of every element rather than nothing at all.
    /// </summary>
    private static bool FollowsWildcard(ExpressionNode node) => node switch
    {
        WildcardAccessNode => true,
        MemberAccessNode member => FollowsWildcard(member.Target),
        IndexAccessNode index => FollowsWildcard(index.Target),
        _ => false,
    };

    /// <summary>
    /// Applies one link to every entry of a filtered array and keeps only the entries the link finds.
    /// Dropping is the rule, not reading <c>null</c> for the misses: <c>foo.*.one</c> over three objects,
    /// the middle one without a <c>one</c>, is the two values that exist (<c>op_idx_star.json</c>,
    /// <c>nested-object-property-not-found</c>).
    /// </summary>
    private static GitHubValue MapEach(GitHubValue source, Func<GitHubValue, GitHubValue?> link)
    {
        var kept = new List<GitHubValue>(source.Array.Count);
        foreach (var child in source.Array)
        {
            if (link(child) is { } mapped)
            {
                kept.Add(mapped);
            }
        }

        return GitHubValue.FromArray(kept);
    }

    // ---------------------------------------------------------------- operators

    private Evaluated EvaluateNot(UnaryNotNode node)
    {
        var operand = EvaluateOrPropagate(node.Operand, "!", node.Range);
        if (!operand.IsKnown)
        {
            return operand;
        }

        return Evaluated.Known(GitHubValue.FromBoolean(!operand.Value!.IsTruthy()));
    }

    /// <summary>
    /// Evaluates <c>&amp;&amp;</c> and <c>||</c> with Actions' short-circuit and operand-returning
    /// behaviour, which is what makes three-state answers possible at all: a definite <c>false</c> on
    /// the left of <c>&amp;&amp;</c> settles the whole chain even when the right side is unknowable, and
    /// the right side is never even looked at.
    /// </summary>
    private Evaluated EvaluateLogical(LogicalNode node)
    {
        var pendingUnknowns = new List<UnknownReason>();
        Evaluated last = Evaluated.Known(GitHubValue.False);

        foreach (var operand in node.Operands)
        {
            var current = Evaluate(operand);
            if (!current.IsKnown)
            {
                pendingUnknowns.Add(current.Reason!);
                last = current;
                continue;
            }

            var truthy = current.Value!.IsTruthy();
            if (node.Operator == LogicalOperator.And && !truthy)
            {
                return current;
            }

            if (node.Operator == LogicalOperator.Or && truthy)
            {
                return current;
            }

            last = current;
        }

        if (pendingUnknowns.Count > 0)
        {
            // Nothing in the chain settled it, and at least one part is still open: whether the chain
            // takes that part or the value after it is exactly what cannot be answered yet.
            return Evaluated.Unknown(new UnknownReason(
                UnknownReasonKind.Propagated,
                $"'{Symbol(node.Operator)}' depends on a part that is not known yet.",
                node.Range,
                pendingUnknowns));
        }

        return last;
    }

    private static string Symbol(LogicalOperator op) => op == LogicalOperator.And ? "&&" : "||";

    private Evaluated EvaluateComparison(ComparisonNode node)
    {
        var left = EvaluateOrPropagate(node.Left, OperatorText(node.Operator), node.Range);
        if (!left.IsKnown)
        {
            return left;
        }

        var right = EvaluateOrPropagate(node.Right, OperatorText(node.Operator), node.Range);
        if (!right.IsKnown)
        {
            return right;
        }

        var sameInstance = ReferenceEquals(left.Value, right.Value);
        var result = node.Operator switch
        {
            ComparisonOperator.Equal => ExpressionCoercion.AreEqual(left.Value!, right.Value!, sameInstance),
            ComparisonOperator.NotEqual => ExpressionCoercion.NotEqual(left.Value!, right.Value!, sameInstance),
            ComparisonOperator.LessThan => ExpressionCoercion.LessThan(left.Value!, right.Value!),
            ComparisonOperator.LessThanOrEqual => ExpressionCoercion.LessThanOrEqual(left.Value!, right.Value!),
            ComparisonOperator.GreaterThan => ExpressionCoercion.GreaterThan(left.Value!, right.Value!),
            ComparisonOperator.GreaterThanOrEqual => ExpressionCoercion.GreaterThanOrEqual(left.Value!, right.Value!),
            _ => false,
        };

        return Evaluated.Known(GitHubValue.FromBoolean(result));
    }

    private static string OperatorText(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Equal => "==",
        ComparisonOperator.NotEqual => "!=",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "<=",
        ComparisonOperator.GreaterThan => ">",
        _ => ">=",
    };

    // ---------------------------------------------------------------- functions

    private Evaluated EvaluateFunction(FunctionCallNode node)
    {
        if (!_contexts.IsFunctionAvailable(node.Name))
        {
            return Evaluated.Unknown(_contexts.ExplainUnavailable(node.Name) with { Symbol = node.Name });
        }

        var arguments = new List<Evaluated>(node.Arguments.Count);
        foreach (var argument in node.Arguments)
        {
            var evaluated = Evaluate(argument);
            if (!evaluated.IsKnown && IsStrictlyRequired(node.Name))
            {
                return Evaluated.Unknown(UnknownReason.Propagating(node.Name + "()", node.Range, evaluated.Reason!));
            }

            arguments.Add(evaluated);
        }

        return node.Name.ToLowerInvariant() switch
        {
            "contains" => Contains(node, arguments),
            "startswith" => StartsWith(node, arguments),
            "endswith" => EndsWith(node, arguments),
            "format" => Format(node, arguments),
            "join" => Join(node, arguments),
            "tojson" => ToJson(node, arguments),
            "fromjson" => FromJson(node, arguments),
            "hashfiles" => HashFiles(node, arguments),
            "success" => Status(node, StatusFunction.Success),
            "failure" => Status(node, StatusFunction.Failure),
            "always" => Status(node, StatusFunction.Always),
            "cancelled" => Status(node, StatusFunction.Cancelled),
            "case" => Case(node, arguments),
            _ => Evaluated.Unknown(new UnknownReason(
                UnknownReasonKind.InvalidSyntax,
                $"The function '{node.Name}' is not part of the Actions expression language.",
                node.Range)),
        };
    }

    /// <summary>
    /// Whether an unknown argument makes the whole call unknown. Most do, but the status functions take
    /// no arguments and <c>hashFiles</c> is unknown for its own reason regardless of its arguments.
    /// </summary>
    private static bool IsStrictlyRequired(string name) =>
        name.ToLowerInvariant() is not ("hashfiles" or "success" or "failure" or "always" or "cancelled");

    private static Evaluated Contains(FunctionCallNode node, List<Evaluated> arguments)
    {
        if (arguments.Count < 2 || !AllKnown(arguments))
        {
            return UnknownArguments(node, arguments);
        }

        var search = arguments[0].Value!;
        var item = arguments[1].Value!;

        if (search.Kind == GitHubValueKind.Array)
        {
            return Evaluated.Known(GitHubValue.FromBoolean(
                search.Array.Any(child => ExpressionCoercion.AreEqual(child, item, ReferenceEquals(child, item)))));
        }

        if (IsComposite(search) || IsComposite(item))
        {
            return Evaluated.Known(GitHubValue.False);
        }

        return Evaluated.Known(GitHubValue.FromBoolean(
            search.Stringify().Contains(item.Stringify(), StringComparison.OrdinalIgnoreCase)));
    }

    private static Evaluated StartsWith(FunctionCallNode node, List<Evaluated> arguments)
    {
        if (arguments.Count < 2 || !AllKnown(arguments))
        {
            return UnknownArguments(node, arguments);
        }

        var search = arguments[0].Value!;
        var value = arguments[1].Value!;
        if (IsComposite(search) || IsComposite(value))
        {
            return Evaluated.Known(GitHubValue.False);
        }

        return Evaluated.Known(GitHubValue.FromBoolean(
            search.Stringify().StartsWith(value.Stringify(), StringComparison.OrdinalIgnoreCase)));
    }

    private static Evaluated EndsWith(FunctionCallNode node, List<Evaluated> arguments)
    {
        if (arguments.Count < 2 || !AllKnown(arguments))
        {
            return UnknownArguments(node, arguments);
        }

        var search = arguments[0].Value!;
        var value = arguments[1].Value!;
        if (IsComposite(search) || IsComposite(value))
        {
            return Evaluated.Known(GitHubValue.False);
        }

        return Evaluated.Known(GitHubValue.FromBoolean(
            search.Stringify().EndsWith(value.Stringify(), StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// <c>format</c> substitutes <c>{0}</c>, <c>{1}</c> and so on, with <c>{{</c> and <c>}}</c> as
    /// literal braces, and every substituted value is converted to text first.
    /// </summary>
    /// <remarks>
    /// A brace that pairs with nothing is an error rather than a literal brace: the reference treats a
    /// template as valid only when every <c>{</c> and <c>}</c> is either an escape pair or a placeholder,
    /// so <c>format('0}')</c> and <c>format('{0}}', '')</c> fail instead of quietly producing
    /// <c>0}</c> and <c>}</c> (<c>format.json</c>, <c>invalid format string</c>). Reporting it as unknown
    /// rather than guessing the author's intent matters here: the text a broken template yields is not the
    /// text the run would produce, because the run produces none at all.
    /// </remarks>
    private static Evaluated Format(FunctionCallNode node, List<Evaluated> arguments)
    {
        if (!AllKnown(arguments))
        {
            return UnknownArguments(node, arguments);
        }

        if (arguments.Count == 0)
        {
            return Evaluated.Unknown(new UnknownReason(
                UnknownReasonKind.InvalidSyntax, "format() needs a format string.", node.Range));
        }

        var template = arguments[0].Value!.Stringify();
        var builder = new StringBuilder(template.Length);

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                builder.Append('{');
                i++;
                continue;
            }

            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                builder.Append('}');
                i++;
                continue;
            }

            if (c != '{')
            {
                if (c == '}')
                {
                    return InvalidFormat(node, template);
                }

                builder.Append(c);
                continue;
            }

            var close = template.IndexOf('}', i + 1);
            if (close < 0)
            {
                return InvalidFormat(node, template);
            }

            var indexText = template[(i + 1)..close];
            if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0)
            {
                // `{x}` is not a placeholder and the braces do not pair, so it is the same failure as a
                // stray brace rather than an argument-count problem.
                return InvalidFormat(node, template);
            }

            if (index >= arguments.Count - 1)
            {
                return Evaluated.Unknown(new UnknownReason(
                    UnknownReasonKind.InvalidSyntax,
                    $"The format string '{template}' references more arguments than were supplied.",
                    node.Range));
            }

            builder.Append(arguments[index + 1].Value!.Stringify());
            i = close;
        }

        return Evaluated.Known(GitHubValue.FromString(builder.ToString()));
    }

    private static Evaluated InvalidFormat(FunctionCallNode node, string template) => Evaluated.Unknown(new UnknownReason(
        UnknownReasonKind.InvalidSyntax,
        $"The format string '{template}' has a brace that is neither an escaped pair nor a placeholder.",
        node.Range));

    private static Evaluated Join(FunctionCallNode node, List<Evaluated> arguments)
    {
        if (arguments.Count < 1 || !AllKnown(arguments))
        {
            return UnknownArguments(node, arguments);
        }

        var value = arguments[0].Value!;
        var separator = arguments.Count > 1 && !IsComposite(arguments[1].Value!)
            ? arguments[1].Value!.Stringify()
            : ",";

        if (value.Kind == GitHubValueKind.Array)
        {
            // Elements are converted to text individually, and an element that is an object becomes the
            // literal "Object" rather than its contents.
            return Evaluated.Known(GitHubValue.FromString(value.JoinWith(separator)));
        }

        return value.Kind == GitHubValueKind.Object
            ? Evaluated.Known(GitHubValue.FromString(string.Empty))
            : Evaluated.Known(GitHubValue.FromString(value.Stringify()));
    }

    private static Evaluated ToJson(FunctionCallNode node, List<Evaluated> arguments)
    {
        if (arguments.Count < 1 || !AllKnown(arguments))
        {
            return UnknownArguments(node, arguments);
        }

        return Evaluated.Known(GitHubValue.FromString(JsonRenderer.Render(arguments[0].Value!, 0)));
    }

    private static Evaluated FromJson(FunctionCallNode node, List<Evaluated> arguments)
    {
        if (arguments.Count < 1 || !AllKnown(arguments))
        {
            return UnknownArguments(node, arguments);
        }

        var text = arguments[0].Value!.Stringify();
        if (text.Trim().Length == 0)
        {
            return Evaluated.Unknown(new UnknownReason(
                UnknownReasonKind.InvalidSyntax, "fromJSON() was given empty text.", node.Range));
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(text);
            return Evaluated.Known(JsonValueConverter.ToValue(document.RootElement));
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Evaluated.Unknown(new UnknownReason(
                UnknownReasonKind.InvalidSyntax,
                $"fromJSON() could not parse its input: {ex.Message}",
                node.Range));
        }
    }

    /// <summary>
    /// <c>hashFiles</c> reads the repository checkout, which ActionLens never touches, so the answer is
    /// always unknown — and stays unknown even though the path patterns are perfectly readable.
    /// </summary>
    private static Evaluated HashFiles(FunctionCallNode node, List<Evaluated> arguments)
    {
        return Evaluated.Unknown(new UnknownReason(
            UnknownReasonKind.RuntimeFunction,
            "hashFiles() needs the files in the checkout, which ActionLens does not read.",
            node.Range));
    }

    private Evaluated Status(FunctionCallNode node, StatusFunction function)
    {
        // always() is not a question about the run: both implementations return true unconditionally,
        // including when the workflow was cancelled. Everything else depends on the scenario.
        if (function == StatusFunction.Always)
        {
            return Evaluated.Known(GitHubValue.True);
        }

        if (_contexts.TryGetStatus(function, out var value))
        {
            return value.Kind == TriKind.Unknown
                ? Evaluated.Unknown(value.Reason ?? new UnknownReason(UnknownReasonKind.ScenarioDependent, "The scenario does not decide it.", node.Range))
                : Evaluated.Known(GitHubValue.FromBoolean(value.Kind == TriKind.True));
        }

        return Evaluated.Unknown(new UnknownReason(
            UnknownReasonKind.ScenarioDependent,
            $"The scenario does not say what {function.ToString().ToLowerInvariant()}() should return here.",
            node.Range));
    }

    private static Evaluated Case(FunctionCallNode node, List<Evaluated> arguments)
    {
        if (arguments.Count < 3 || arguments.Count % 2 == 0)
        {
            return Evaluated.Unknown(new UnknownReason(
                UnknownReasonKind.InvalidSyntax,
                "case() needs an odd number of arguments: pairs of predicate and value, then a default.",
                node.Range));
        }

        var unknowns = new List<UnknownReason>();
        for (var i = 0; i < arguments.Count - 1; i += 2)
        {
            var predicate = arguments[i];
            if (!predicate.IsKnown)
            {
                unknowns.Add(predicate.Reason!);
                continue;
            }

            if (predicate.Value!.Kind != GitHubValueKind.Boolean)
            {
                return Evaluated.Unknown(new UnknownReason(
                    UnknownReasonKind.InvalidSyntax,
                    "A case() predicate has to evaluate to a boolean.",
                    node.Range));
            }

            if (predicate.Value.Boolean)
            {
                return arguments[i + 1];
            }
        }

        if (unknowns.Count > 0)
        {
            // A predicate that is still open decides which branch is returned, so the whole result is open.
            return Evaluated.Unknown(new UnknownReason(
                UnknownReasonKind.Propagated,
                "case() cannot pick a branch yet.",
                node.Range,
                unknowns));
        }

        return arguments[^1];
    }

    private static bool AllKnown(IReadOnlyList<Evaluated> arguments) =>
        arguments.All(argument => argument.IsKnown);

    private static bool IsComposite(GitHubValue value) =>
        value.Kind is GitHubValueKind.Array or GitHubValueKind.Object;

    private static Evaluated UnknownArguments(FunctionCallNode node, IReadOnlyList<Evaluated> arguments)
    {
        var causes = arguments
            .Where(argument => !argument.IsKnown)
            .Select(argument => argument.Reason!)
            .ToArray();
        return causes.Length > 0
            ? Evaluated.Unknown(new UnknownReason(
                UnknownReasonKind.Propagated,
                $"'{node.Name}()' has an argument that is not known yet.",
                node.Range,
                causes))
            : Evaluated.Unknown(new UnknownReason(
                UnknownReasonKind.InvalidSyntax,
                $"'{node.Name}()' was called with the wrong number of arguments.",
                node.Range));
    }

    /// <summary>Renders a value as the JSON text that <c>toJSON()</c> returns: two-space indentation.</summary>
    private static class JsonRenderer
    {
        internal static string Render(GitHubValue value, int _)
        {
            var builder = new StringBuilder();
            Write(builder, value, 0);
            return builder.ToString();
        }

        private static void Write(StringBuilder builder, GitHubValue value, int indent)
        {
            switch (value.Kind)
            {
                case GitHubValueKind.Null:
                    builder.Append("null");
                    break;
                case GitHubValueKind.Boolean:
                    builder.Append(value.Boolean ? "true" : "false");
                    break;
                case GitHubValueKind.Number:
                    builder.Append(GitHubValue.FormatNumber(value.Number));
                    break;
                case GitHubValueKind.String:
                    builder.Append(Escape(value.String));
                    break;
                case GitHubValueKind.Array:
                    WriteArray(builder, value, indent);
                    break;
                default:
                    WriteObject(builder, value, indent);
                    break;
            }
        }

        private static void WriteArray(StringBuilder builder, GitHubValue value, int indent)
        {
            if (value.Array.Count == 0)
            {
                builder.Append("[]");
                return;
            }

            builder.Append('[').Append('\n');
            for (var i = 0; i < value.Array.Count; i++)
            {
                Indent(builder, indent + 1);
                Write(builder, value.Array[i], indent + 1);
                if (i < value.Array.Count - 1)
                {
                    builder.Append(',').Append('\n');
                }
            }

            builder.Append('\n');
            Indent(builder, indent);
            builder.Append(']');
        }

        private static void WriteObject(StringBuilder builder, GitHubValue value, int indent)
        {
            var properties = value.Object.ToArray();
            if (properties.Length == 0)
            {
                builder.Append("{}");
                return;
            }

            builder.Append('{').Append('\n');
            for (var i = 0; i < properties.Length; i++)
            {
                Indent(builder, indent + 1);
                builder.Append(Escape(properties[i].Key)).Append(": ");
                Write(builder, properties[i].Value, indent + 1);
                if (i < properties.Length - 1)
                {
                    builder.Append(',').Append('\n');
                }
            }

            builder.Append('\n');
            Indent(builder, indent);
            builder.Append('}');
        }

        private static void Indent(StringBuilder builder, int level) => builder.Append(' ', level * 2);

        private static string Escape(string text)
        {
            var builder = new StringBuilder(text.Length + 2).Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.Append('"').ToString();
        }
    }
}
