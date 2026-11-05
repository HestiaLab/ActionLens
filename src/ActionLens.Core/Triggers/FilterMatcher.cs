using ActionLens.Core.Text;
using ActionLens.Core.Workflows;

namespace ActionLens.Core.Triggers;

/// <summary>
/// Which sense a filter list works in.
/// </summary>
public enum FilterListMode
{
    /// <summary>
    /// A plain list (<c>branches</c>, <c>tags</c>, <c>paths</c>): the value has to be let through by it.
    /// A value that matches no pattern is excluded, so a bare <c>-</c> list of nothing is a closed door.
    /// </summary>
    Include,

    /// <summary>
    /// An <c>-ignore</c> list (<c>branches-ignore</c>, <c>paths-ignore</c>): matching it is what stops the
    /// event, so a value that matches nothing gets through.
    /// </summary>
    Ignore,
}

/// <summary>
/// One compiled filter pattern: the author's text, whether it is negated, and the token engine that
/// decides what it matches.
/// </summary>
/// <remarks>
/// The grammar is the documented filter-pattern grammar ([Workflow syntax], "Filter pattern syntax"):
/// <list type="table">
///   <item><term><c>*</c></term><description>zero or more characters, but never a <c>/</c>.</description></item>
///   <item><term><c>**</c></term><description>zero or more characters, <c>/</c> included.</description></item>
///   <item><term><c>?</c></term><description>zero or one of the <em>preceding</em> character — not a
///   single-character wildcard, which is what makes <c>ab?c</c> match both <c>abc</c> and <c>ac</c>.</description></item>
///   <item><term><c>+</c></term><description>one or more of the preceding character.</description></item>
///   <item><term><c>[]</c></term><description>one alphanumeric character or range; the ranges GitHub
///   allows are <c>a-z</c>, <c>A-Z</c> and <c>0-9</c>, and a <c>/</c> or <c>-</c> can never be in one.</description></item>
///   <item><term><c>!</c> at the start</term><description>negates the pattern (see <see cref="Negated"/>).</description></item>
///   <item><term><c>\</c></term><description>makes the next character literal, which is how a branch
///   genuinely named <c>v+1</c> is written.</description></item>
/// </list>
/// <para>
/// The matcher is written out as a backtracking walk over the tokens rather than translated into a
/// <see cref="System.Text.RegularExpressions.Regex"/>: every translation would have to re-derive the
/// "does not cross <c>/</c>" rule, the quantified-preceding-character rule and the alphanumeric-only
/// ranges, and a regex that silently differs from the table is the failure mode this module exists to
/// avoid. There is no <c>Regex</c> on this path at all.
/// </para>
/// <para>
/// Two choices the sources do not settle, both recorded in the "Ambiguities" section of
/// <c>docs/expression-semantics.md</c>: matching is <b>case-sensitive</b> (the documented character
/// classes distinguish <c>[a-z]</c> from <c>[A-Z]</c>, so the table never grants a case-folded match, and
/// ActionLens does not claim one it cannot cite), and a pattern is anchored at <b>both ends</b> of the
/// whole value (which is why <c>releases/**</c>, not <c>releases</c>, is how the docs ask for a subtree,
/// and why path patterns match from the repository root).
/// </para>
/// </remarks>
public sealed class FilterPattern
{
    /// <summary>Standing for "repeat without end" in an atom's bounds.</summary>
    private const int Unbounded = int.MaxValue;

    /// <summary>
    /// The backtracking budget for one match. A pattern of <c>*</c> and <c>**</c> atoms over a long value
    /// can explore exponentially many splits; the budget turns that into a refusal rather than a hang,
    /// which a workflow a hostile author wrote on purpose can otherwise reach.
    /// </summary>
    private const int MaxMatchSteps = 250_000;

    private readonly Atom[] _atoms;

    private FilterPattern(string rawText, string text, bool negated, SourceRange range, Atom[] atoms, string? error)
    {
        RawText = rawText;
        Text = text;
        Negated = negated;
        Range = range;
        _atoms = atoms;
        Error = error;
    }

    /// <summary>The author's text exactly as written, negation marker included.</summary>
    public string RawText { get; }

    /// <summary>The pattern with the leading <c>!</c> removed: what the token engine was built from.</summary>
    public string Text { get; }

    /// <summary>True when the pattern starts with <c>!</c>, which excludes rather than includes.</summary>
    public bool Negated { get; }

    /// <summary>Where the pattern sits in the YAML, so a finding can point at it.</summary>
    public SourceRange Range { get; }

    /// <summary>Why the pattern could not be compiled; null when it is a usable pattern.</summary>
    public string? Error { get; }

    public bool IsValid => Error is null;

    /// <summary>Compiles the pattern an author wrote, keeping its position and raw text.</summary>
    public static FilterPattern Compile(WorkflowValue value) => Compile(value.Text, value.Range);

    /// <summary>Compiles a pattern with no position, for callers that are not reading a workflow file.</summary>
    public static FilterPattern Compile(string text) => Compile(text, SourceRange.Unknown);

    /// <summary>
    /// Compiles <paramref name="text"/>, always answering with a pattern: a broken one carries its
    /// <see cref="Error"/> and matches nothing, so a caller reports the finding instead of guessing.
    /// </summary>
    public static FilterPattern Compile(string? text, SourceRange range)
    {
        var raw = text ?? string.Empty;
        var negated = raw.Length > 0 && raw[0] == '!';
        var body = negated ? raw[1..] : raw;
        var atoms = new List<Atom>();
        var error = TryBuild(body, atoms);
        return new FilterPattern(raw, body, negated, range, [.. atoms], error);
    }

    /// <summary>True when this pattern matches the whole of <paramref name="value"/>.</summary>
    public bool IsMatch(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!IsValid)
        {
            // A pattern ActionLens could not read says nothing. The finding about it is the answer.
            return false;
        }

        var budget = MaxMatchSteps;
        return MatchFrom(_atoms.AsSpan(), value.AsSpan(), ref budget);
    }

    // ------------------------------------------------------------------ token engine

    private enum AtomKind
    {
        Literal,
        Class,
        Star,
        GlobStar,
    }

    /// <summary>One element of a pattern with its repetition bounds, the <c>?</c> and <c>+</c> already folded in.</summary>
    private sealed class Atom
    {
        internal AtomKind Type;

        internal char Literal;

        internal (char Low, char High)[] Class = [];

        internal int Min = 1;

        internal int Max = 1;
    }

    private static string? TryBuild(string body, List<Atom> atoms)
    {
        var index = 0;
        while (index < body.Length)
        {
            var current = body[index];
            switch (current)
            {
                case '\\':
                    if (index + 1 >= body.Length)
                    {
                        return "the pattern ends with a backslash, so the character it escapes is missing";
                    }

                    atoms.Add(LiteralAtom(body[index + 1]));
                    index += 2;
                    break;

                case '*':
                    {
                        var run = 0;
                        while (index + run < body.Length && body[index + run] == '*')
                        {
                            run++;
                        }

                        // A run of two or more `*` is the documented `**`, which crosses `/`. Longer runs
                        // are not in the table, and the conservative reading is the same token, not an error.
                        var atom = new Atom { Type = run == 1 ? AtomKind.Star : AtomKind.GlobStar, Min = 0, Max = Unbounded };
                        index += run;
                        var quantifier = ApplyQuantifier(body, ref index, atom);
                        if (quantifier is not null)
                        {
                            return quantifier;
                        }

                        atoms.Add(atom);
                        break;
                    }

                case '?':
                case '+':
                    // A quantifier is only ever read straight after its atom, so arriving here with
                    // something already parsed means it followed another quantifier.
                    return atoms.Count == 0
                        ? $"'{current}' has to follow a character, but it is at the start of the pattern"
                        : $"'{current}' follows a quantifier, and the pattern grammar has no reading for that";

                case '[':
                    {
                        var end = body.IndexOf(']', index + 1);
                        if (end < 0)
                        {
                            return "the character class has no closing ']'";
                        }

                        if (end == index + 1)
                        {
                            return "the character class is empty";
                        }

                        var classError = TryBuildClass(body, index + 1, end, out var ranges);
                        if (classError is not null)
                        {
                            return classError;
                        }

                        var atom = new Atom { Type = AtomKind.Class, Class = ranges };
                        index = end + 1;
                        var quantifier = ApplyQuantifier(body, ref index, atom);
                        if (quantifier is not null)
                        {
                            return quantifier;
                        }

                        atoms.Add(atom);
                        break;
                    }

                default:
                    {
                        var atom = LiteralAtom(current);
                        index++;
                        var quantifier = ApplyQuantifier(body, ref index, atom);
                        if (quantifier is not null)
                        {
                            return quantifier;
                        }

                        atoms.Add(atom);
                        break;
                    }
            }
        }

        return null;
    }

    private static Atom LiteralAtom(char value) => new() { Type = AtomKind.Literal, Literal = value };

    /// <summary>
    /// Reads a <c>?</c> or <c>+</c> that follows the atom just parsed and folds it into the atom's bounds,
    /// which is what "matches zero or one / one or more of the <em>preceding</em> character" means.
    /// </summary>
    private static string? ApplyQuantifier(string body, ref int index, Atom atom)
    {
        if (index >= body.Length)
        {
            return null;
        }

        var next = body[index];
        if (next != '?' && next != '+')
        {
            return null;
        }

        // An atom that already repeats cannot be quantified again in a way the table describes: `*?` is
        // still "any number of this segment", so the marker is consumed and the bounds are left alone.
        if (atom.Type is AtomKind.Star or AtomKind.GlobStar)
        {
            index++;
            return null;
        }

        if (next == '?')
        {
            atom.Min = 0;
            atom.Max = 1;
        }
        else
        {
            atom.Min = 1;
            atom.Max = Unbounded;
        }

        index++;
        return null;
    }

    private static string? TryBuildClass(string body, int start, int end, out (char Low, char High)[] ranges)
    {
        var found = new List<(char Low, char High)>();
        var index = start;
        while (index < end)
        {
            var first = body[index];
            if (!IsClassChar(first))
            {
                ranges = [];
                return $"the character class contains '{first}', and '[]' matches only alphanumeric characters and ranges";
            }

            if (index + 2 < end && body[index + 1] == '-')
            {
                var last = body[index + 2];
                if (!IsClassChar(last))
                {
                    ranges = [];
                    return $"the character class range '{first}-{last}' is not one of a-z, A-Z or 0-9";
                }

                if (GroupOf(first) != GroupOf(last) || last < first)
                {
                    ranges = [];
                    return $"the character class range '{first}-{last}' is reversed or crosses a-z, A-Z and 0-9";
                }

                found.Add((first, last));
                index += 3;
                continue;
            }

            found.Add((first, first));
            index++;
        }

        ranges = [.. found];
        return null;
    }

    /// <summary>
    /// A class member is alphanumeric and nothing else: the documented table says <c>[]</c> matches "one
    /// alphanumeric character or range" and that a <c>/</c> or <c>-</c> can never be inside one.
    /// </summary>
    private static bool IsClassChar(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static int GroupOf(char value) => value switch
    {
        (>= 'a' and <= 'z') => 0,
        (>= 'A' and <= 'Z') => 1,
        _ => 2,
    };

    private static bool MatchFrom(ReadOnlySpan<Atom> atoms, ReadOnlySpan<char> rest, ref int budget)
    {
        if (budget-- <= 0)
        {
            // Refuse rather than answer: a wrong "it matched" is worse than no answer at all, and the
            // caller that hits this budget is holding a pathological pattern, not a real branch name.
            return false;
        }

        if (atoms.Length == 0)
        {
            return rest.IsEmpty;
        }

        var atom = atoms[0];
        var tail = atoms[1..];
        var limit = atom.Max == Unbounded ? rest.Length : Math.Min(atom.Max, rest.Length);

        for (var count = 0; count <= limit; count++)
        {
            if (count >= atom.Min && MatchFrom(tail, rest[count..], ref budget))
            {
                return true;
            }

            if (count == limit || !CharMatches(atom, rest[count]))
            {
                // This atom cannot take another character, so no longer repeat can be tried.
                return false;
            }
        }

        return false;
    }

    private static bool CharMatches(Atom atom, char value) => atom.Type switch
    {
        // `*` is the one token the docs restrict to a path segment: it never crosses a `/`.
        AtomKind.Star => value != '/',
        AtomKind.GlobStar => true,
        AtomKind.Literal => value == atom.Literal,
        _ => InClass(atom.Class, value),
    };

    private static bool InClass((char Low, char High)[] ranges, char value)
    {
        foreach (var (low, high) in ranges)
        {
            if (value >= low && value <= high)
            {
                return true;
            }
        }

        return false;
    }

    public override string ToString() => Negated ? $"!{Text}" : Text;
}

/// <summary>
/// The documented rules over one filter's <em>list</em> of patterns, on top of the single-pattern engine
/// in <see cref="FilterPattern"/>.
/// </summary>
public static class FilterMatcher
{
    /// <summary>Compiles every pattern of a filter key, keeping order, text and positions.</summary>
    public static IReadOnlyList<FilterPattern> CompileAll(IReadOnlyList<WorkflowValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var patterns = new List<FilterPattern>(values.Count);
        foreach (var value in values)
        {
            patterns.Add(FilterPattern.Compile(value));
        }

        return patterns;
    }

    /// <summary>
    /// Reads one value against a filter list and says whether the list lets it through.
    /// </summary>
    /// <remarks>
    /// The rules come from [Events that trigger workflows] and the pattern-syntax note in [Workflow syntax]:
    /// patterns are tested in the order written, a match on a <c>!</c>-prefixed pattern after a positive
    /// match excludes, and a match on a positive pattern after a negative one includes again — so the
    /// <em>last</em> pattern that matches decides, while a value nothing matches falls back to the list's
    /// own sense (<see cref="FilterListMode.Ignore"/> lets it, <see cref="FilterListMode.Include"/> does not).
    /// </remarks>
    public static FilterMatch Match(IReadOnlyList<FilterPattern> patterns, string value, FilterListMode mode)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(value);

        FilterPattern? deciding = null;
        foreach (var pattern in patterns)
        {
            if (pattern.IsMatch(value))
            {
                deciding = pattern;
            }
        }

        if (deciding is null)
        {
            return new FilterMatch(mode == FilterListMode.Ignore, null, value);
        }

        return new FilterMatch(SatisfiedBy(deciding, mode), deciding, value);
    }

    /// <summary>
    /// Tests a set of changed files against a path list, reporting the first file the list lets through.
    /// </summary>
    /// <remarks>
    /// A path filter is satisfied by <em>any</em> changed file: GitHub runs the workflow when one file in
    /// the diff matches, which is why the loop stops at the first hit and names the file that decided it.
    /// With nothing to test, an <c>-ignore</c> list has excluded nothing and a plain list has admitted
    /// nothing — the reading taken here, and the one flagged in the doc's ambiguities section for the
    /// "no files changed" sentence.
    /// </remarks>
    public static FilterMatch MatchAnyFile(IReadOnlyList<FilterPattern> patterns, IReadOnlyList<string> files, FilterListMode mode)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(files);

        FilterMatch last = new(mode == FilterListMode.Ignore, null, null);
        foreach (var file in files)
        {
            var result = Match(patterns, file, mode);
            last = result with { ValuesTested = files.Count };
            if (result.Satisfied)
            {
                return last;
            }
        }

        return last;
    }

    /// <summary>The sense one entry has inside one list: <c>branches: [!dev]</c> excludes, <c>branches-ignore: [!dev]</c> includes.</summary>
    private static bool SatisfiedBy(FilterPattern pattern, FilterListMode mode) =>
        (mode == FilterListMode.Include) != pattern.Negated;
}

/// <summary>
/// What a filter list answered for one value, and the pattern that answered it — the pair the report
/// shows, because "which filter said no" is the question the module exists to answer.
/// </summary>
/// <param name="Satisfied">True when the list lets the value through.</param>
/// <param name="DecidingPattern">The last pattern that matched, null when no pattern matched at all.</param>
/// <param name="Value">The value that was tested; null when there was nothing to test.</param>
/// <param name="ValuesTested">How many values the list was run against; one for a ref, many for paths.</param>
public readonly record struct FilterMatch(
    bool Satisfied,
    FilterPattern? DecidingPattern,
    string? Value,
    int ValuesTested = 0);
