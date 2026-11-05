using ActionLens.Core.Expressions;
using ActionLens.Core.Values;

namespace ActionLens.Core.Tests.Values;

/// <summary>
/// Coercion and comparison, pinned case by case. Every expectation here is one GitHub either documents
/// or pins in its own test corpus, and the odd ones — <c>'' == 0</c>, <c>'0' == false</c>,
/// <c>'false' != null</c> — are exactly where an implementation that "feels right" goes wrong.
/// </summary>
public sealed class ExpressionCoercionTests
{
    private static GitHubValue S(string text) => GitHubValue.FromString(text);

    private static GitHubValue N(double number) => GitHubValue.FromNumber(number);

    private static GitHubValue B(bool value) => GitHubValue.FromBoolean(value);

    [Theory]
    [InlineData("1", 1, true)]
    [InlineData("", 0, true)]
    [InlineData("   ", 0, true)]
    [InlineData("0", 0, true)]
    [InlineData(" 0.0 ", 0, true)]
    [InlineData("-1", -1, true)]
    [InlineData("a", 0, false)]
    [InlineData("false", 0, false)]
    [InlineData("Infinity", double.PositiveInfinity, true)]
    [InlineData("0o10", 8, true)]
    [InlineData("0x10", 16, true)]
    public void TextCoercesToTheNumberActionsSees(string text, double expected, bool parses)
    {
        var coerced = S(text).ToCoercedNumber();

        Assert.Equal(parses, !double.IsNaN(coerced));
        if (parses)
        {
            Assert.Equal(expected, coerced, precision: 10);
        }
    }

    [Theory]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("", "")]
    [InlineData("-2.0", "-2")]
    [InlineData("-0", "0")]
    [InlineData("0.5", "0.5")]
    [InlineData("255", "255")]
    [InlineData("0.012", "0.012")]
    public void NumbersAndBooleansStringifyTheWayTheRunnerFormatsThem(string input, string expected)
    {
        var value = input is "true" or "false"
            ? B(input == "true")
            : input == string.Empty
                ? GitHubValue.Null
                : N(double.Parse(input, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(expected, value.Stringify());
    }

    [Fact]
    public void NullStringifiesToEmptyAndCompositesToTheirKindName()
    {
        Assert.Equal(string.Empty, GitHubValue.Null.Stringify());
        Assert.Equal("Array", GitHubValue.FromArray([N(1)]).Stringify());
        Assert.Equal("Object", GitHubValue.EmptyObject.Stringify());
    }

    [Fact]
    public void ArraysAndObjectsAreAlwaysTruthyEvenWhenEmpty()
    {
        Assert.True(GitHubValue.FromArray([GitHubValue.False]).IsTruthy());
        Assert.True(GitHubValue.EmptyArray.IsTruthy());
        Assert.True(GitHubValue.EmptyObject.IsTruthy());
    }

    [Fact]
    public void FalsySetIsExactlyFalseZeroEmptyAndNull()
    {
        Assert.False(B(false).IsTruthy());
        Assert.False(N(0).IsTruthy());
        Assert.False(N(-0d).IsTruthy());
        Assert.False(N(double.NaN).IsTruthy());
        Assert.False(S(string.Empty).IsTruthy());
        Assert.False(GitHubValue.Null.IsTruthy());

        // The documented trap: only the empty string is falsy, so text that spells a falsy word is not.
        Assert.True(S("false").IsTruthy());
        Assert.True(S("0").IsTruthy());
        Assert.True(S(" ").IsTruthy());
    }

    [Theory]
    [InlineData("1", "1", true)]
    [InlineData("a", "A", true)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ", "abcdefghijklmnopqrstuvwxyz", true)]
    [InlineData("i", "İ", false)]
    [InlineData("ı", "I", false)]
    [InlineData("ς", "Σ", true)]
    [InlineData("a", "a ", false)]
    public void StringComparisonIsCaseInsensitiveButNotWhitespaceTolerant(string left, string right, bool expected)
    {
        Assert.Equal(expected, ExpressionCoercion.AreEqual(S(left), S(right)));
    }

    [Theory]
    [InlineData("1", 1, true)]
    [InlineData("", 0, true)]
    [InlineData("0", 0, true)]
    [InlineData("a", 1, false)]
    [InlineData("NaN", 0, false)]
    public void MixedKindsCompareNumerically(string text, double number, bool expected)
    {
        Assert.Equal(expected, ExpressionCoercion.AreEqual(S(text), N(number)));
    }

    [Fact]
    public void DocumentedEqualityPairsHold()
    {
        Assert.True(ExpressionCoercion.AreEqual(N(1), S("1")));
        Assert.True(ExpressionCoercion.AreEqual(S(string.Empty), N(0)));
        Assert.True(ExpressionCoercion.AreEqual(S("0"), B(false)));
        Assert.True(ExpressionCoercion.AreEqual(GitHubValue.Null, N(0)));
        Assert.True(ExpressionCoercion.AreEqual(GitHubValue.Null, S(string.Empty)));
        Assert.True(ExpressionCoercion.AreEqual(S("false"), GitHubValue.Null) == false);
        Assert.True(ExpressionCoercion.AreEqual(S("null"), GitHubValue.Null) == false);
        Assert.False(ExpressionCoercion.AreEqual(N(double.NaN), N(double.NaN)));
        Assert.True(ExpressionCoercion.NotEqual(N(double.NaN), N(double.NaN)));
    }

    [Fact]
    public void CompositesAreEqualOnlyToThemselves()
    {
        var array = GitHubValue.FromArray([N(1), N(2)]);
        var twin = GitHubValue.FromArray([N(1), N(2)]);

        Assert.True(ExpressionCoercion.AreEqual(array, array, sameInstance: true));
        Assert.False(ExpressionCoercion.AreEqual(array, twin, sameInstance: false));
        Assert.False(ExpressionCoercion.AreEqual(S("Array"), array));
        Assert.False(ExpressionCoercion.AreEqual(N(0), GitHubValue.EmptyObject));
        Assert.False(ExpressionCoercion.AreEqual(GitHubValue.Null, array));
    }

    [Theory]
    [InlineData("a", "b", true, false)]
    // Two strings never leave the string domain: runner `EvaluationResult.AbstractLessThan` compares
    // (String, String) with String.Compare(..., OrdinalIgnoreCase), so 'B' folds against 'a' as 'b' vs
    // 'a' and is GREATER (docs/expression-semantics.md, "Comparison"; corpus op_lt.json 'b' < 'a').
    [InlineData("B", "a", false, false)]
    // Numeric-looking text is still text, so the order is by character: '2' > '1', giving '2' > '10'
    // and '10' < '2'. Only mixed-kind operands coerce to numbers ('2' < 10 is true — MixedKindsCompareNumerically).
    [InlineData("2", "10", false, false)]
    [InlineData("10", "2", true, false)]
    [InlineData("a", "A", false, false)]
    public void RelationalOperatorsFollowTheSameCoercion(string left, string right, bool lessExpected, bool ignored)
    {
        _ = ignored;
        Assert.Equal(lessExpected, ExpressionCoercion.LessThan(S(left), S(right)));
    }

    [Fact]
    public void ComparingAgainstNaNIsAlwaysFalse()
    {
        var nan = N(double.NaN);

        Assert.False(ExpressionCoercion.LessThan(nan, N(1)));
        Assert.False(ExpressionCoercion.GreaterThan(nan, N(1)));
        Assert.False(ExpressionCoercion.LessThanOrEqual(nan, N(1)));
        Assert.False(ExpressionCoercion.GreaterThanOrEqual(nan, N(1)));
        Assert.False(ExpressionCoercion.AreEqual(S("a"), N(1)));
    }

    [Fact]
    public void BooleansOrderFalseBeforeTrue()
    {
        Assert.True(ExpressionCoercion.LessThan(B(false), B(true)));
        Assert.True(ExpressionCoercion.GreaterThan(B(true), B(false)));
        Assert.True(ExpressionCoercion.LessThanOrEqual(B(true), B(true)));
    }

    [Fact]
    public void ChainedComparisonIsLeftAssociativeLikeTheRunner()
    {
        // 3 < 2 < 1 is (3 < 2) < 1, i.e. false < 1, i.e. 0 < 1 — true. Documented by the official corpus.
        var first = ExpressionCoercion.LessThan(N(3), N(2));
        Assert.False(first);
        Assert.True(ExpressionCoercion.LessThan(GitHubValue.FromBoolean(first), N(1)));
    }

    [Fact]
    public void MemberLookupIsCaseInsensitiveAndIndexingIsNumeric()
    {
        var context = GitHubValue.FromObject(new Dictionary<string, GitHubValue>(StringComparer.Ordinal)
        {
            ["event"] = GitHubValue.FromObject(new Dictionary<string, GitHubValue>(StringComparer.Ordinal)
            {
                ["action"] = S("opened"),
            }),
        });

        Assert.True(context.TryGetMember(GitHubValue.FromString("EVENT"), StringComparison.OrdinalIgnoreCase, out var upper));
        Assert.True(upper.TryGetMember(GitHubValue.FromString("Action"), StringComparison.OrdinalIgnoreCase, out var action));
        Assert.Equal("opened", action.String);

        var list = GitHubValue.FromArray([S("a"), S("b")]);
        Assert.True(list.TryGetMember(N(1), StringComparison.OrdinalIgnoreCase, out var second));
        Assert.Equal("b", second.String);
        Assert.False(list.TryGetMember(N(5), StringComparison.OrdinalIgnoreCase, out _));
    }
}
