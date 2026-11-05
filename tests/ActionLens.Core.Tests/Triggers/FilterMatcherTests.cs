using ActionLens.Core.Text;
using ActionLens.Core.Triggers;
using ActionLens.Core.Values;
using ActionLens.Core.Workflows;

namespace ActionLens.Core.Tests.Triggers;

/// <summary>
/// The filter-pattern grammar, one case per documented token, plus the ordering rules that decide a list.
/// </summary>
/// <remarks>
/// <para>
/// Every expectation here was written from the table in [Workflow syntax] ("Filter pattern syntax") and the
/// negation note in [Events that trigger workflows], not from what the matcher returns: <c>*</c> stops at
/// <c>/</c>, <c>**</c> crosses it, <c>?</c> and <c>+</c> quantify the <em>preceding</em> character (so
/// <c>ab?c</c> is two characters wide at most, not one wildcard), <c>[]</c> takes one alphanumeric
/// character or an <c>a-z</c>/<c>A-Z</c>/<c>0-9</c> range, <c>\</c> escapes, and a leading <c>!</c> negates.
/// </para>
/// <para>
/// Matching is case-sensitive and anchored at both ends of the whole value — the two conservative choices
/// recorded in the "Ambiguities" section of <c>docs/expression-semantics.md</c> — and both are pinned here
/// so a change of mind has to be a decision, not an accident.
/// </para>
/// </remarks>
public sealed class FilterMatcherTests
{
    private static bool Matches(string pattern, string value) => FilterPattern.Compile(pattern).IsMatch(value);

    private static string? ErrorOf(string pattern) => FilterPattern.Compile(pattern).Error;

    // ------------------------------------------------------------------ exact text

    [Fact]
    public void ExactPatternMatchesTheWholeValueOnly()
    {
        Assert.True(Matches("main", "main"));

        // Anchored at both ends: a pattern is not a substring search.
        Assert.False(Matches("main", "maintenance"));
        Assert.False(Matches("main", "themain"));
        Assert.False(Matches("main", "release/main"));
    }

    // ------------------------------------------------------------------ single star

    [Fact]
    public void SingleStarMatchesWithinOneSegmentButNeverCrossesASlash()
    {
        Assert.True(Matches("feature*", "feature"));
        Assert.True(Matches("feature*", "feature-login"));
        Assert.False(Matches("feature*", "feature/login"));

        // `*` is zero or more, so it can stand for nothing at all.
        Assert.True(Matches("release*/10", "release/10"));
        Assert.True(Matches("*", "anything"));
        Assert.True(Matches("*", string.Empty));
        Assert.False(Matches("*", "two/segments"));
    }

    [Fact]
    public void SingleStarInsideAPatternBoundsTheSegmentItSitsIn()
    {
        Assert.True(Matches("release/*", "release/10"));
        Assert.False(Matches("release/*", "release/10/1"));
        Assert.False(Matches("release/*", "release-a/10"));
    }

    // ------------------------------------------------------------------ globstar

    [Fact]
    public void GlobStarCrossesSlashesButADoubleStarPatternStillNeedsItsSlash()
    {
        Assert.True(Matches("feature/**", "feature/a"));
        Assert.True(Matches("feature/**", "feature/a/b/c"));

        // The `/` after `**` is literal, so `feature/**` does not match `feature` alone. This is why the
        // docs write `releases/**` rather than `releases` for a subtree.
        Assert.False(Matches("feature/**", "feature"));

        Assert.True(Matches("**", string.Empty));
        Assert.True(Matches("**", "a/b/c"));

        // `**` matches zero characters too, which is what makes `**.js` reach a root-level file.
        Assert.True(Matches("**.js", "index.js"));
        Assert.True(Matches("**.js", "src/app/index.js"));
    }

    [Fact]
    public void ThreeStarsAreReadAsTheDocumentedGlobstar()
    {
        // The table has no entry for a run longer than two; the conservative reading is the same token,
        // not a rejection, and it is recorded in the doc's ambiguities section.
        Assert.True(Matches("a/***/b", "a/x/y/b"));
    }

    // ------------------------------------------------------------------ question mark

    [Fact]
    public void QuestionMarkQuantifiesThePrecedingCharacterNotTheNextPosition()
    {
        // GitHub's `?` is a regex-style quantifier: zero or one of what came before it.
        Assert.True(Matches("ab?c", "abc"));
        Assert.True(Matches("ab?c", "ac"));
        Assert.False(Matches("ab?c", "abbc"));
        Assert.False(Matches("ab?c", "abd"));

        Assert.True(Matches("v?.14", "v.14"));
        Assert.True(Matches("v?.14", ".14"));
        Assert.False(Matches("v?.14", "vv.14"));
    }

    [Fact]
    public void QuestionMarkCanQuantifyAStarOrAClassButNotAnotherQuantifier()
    {
        // `*?` is zero-or-one of a repeating token, which is the same set of strings as `*`.
        Assert.True(Matches("a*?b", "ab"));
        Assert.True(Matches("a*?b", "axxb"));
        Assert.False(Matches("a*?b", "axx/b"));

        Assert.True(Matches("[ab]?c", "c"));
        Assert.True(Matches("[ab]?c", "ac"));
        Assert.False(Matches("[ab]?c", "aac"));

        Assert.Equal("'+' follows a quantifier, and the pattern grammar has no reading for that", ErrorOf("a?+"));
    }

    // ------------------------------------------------------------------ plus

    [Fact]
    public void PlusNeedsAtLeastOneOfThePrecedingCharacter()
    {
        Assert.True(Matches("ab+c", "abc"));
        Assert.True(Matches("ab+c", "abbbc"));
        Assert.False(Matches("ab+c", "ac"));
        Assert.False(Matches("ab+c", "bc"));

        Assert.True(Matches("[0-9]+x", "42x"));
        Assert.False(Matches("[0-9]+x", "x"));
    }

    // ------------------------------------------------------------------ character classes

    [Fact]
    public void ClassMatchesOneAlphanumericCharacterOrOneDocumentedRange()
    {
        Assert.True(Matches("[a-c]d", "ad"));
        Assert.True(Matches("[a-c]d", "cd"));
        Assert.False(Matches("[a-c]d", "dd"));
        Assert.False(Matches("[abc]", "ab"));

        Assert.True(Matches("[0-9]", "7"));
        Assert.False(Matches("[0-9]", "a"));
        Assert.True(Matches("[A-Z]1", "A1"));

        // Case matters: the table gives [a-z] and [A-Z] as different ranges, so it does not fold them.
        Assert.False(Matches("[A-Z]1", "a1"));
        Assert.False(Matches("[a-z]1", "A1"));

        // Two members and a range in one class.
        Assert.True(Matches("[xy0-3]z", "2z"));
        Assert.False(Matches("[xy0-3]z", "4z"));
    }

    [Theory]
    [InlineData("[]", "the character class is empty")]
    [InlineData("[abc", "the character class has no closing ']'")]
    [InlineData("[z-a]", "the character class range 'z-a' is reversed or crosses a-z, A-Z and 0-9")]
    [InlineData("[a-9]", "the character class range 'a-9' is reversed or crosses a-z, A-Z and 0-9")]
    [InlineData("[a/]", "the character class contains '/', and '[]' matches only alphanumeric characters and ranges")]
    [InlineData("[a-]", "the character class contains '-', and '[]' matches only alphanumeric characters and ranges")]
    public void InvalidClassesAreReportedRatherThanGuessedAt(string pattern, string expected)
    {
        // A pattern ActionLens cannot read is a finding at its position, never a silent "no match" story.
        Assert.Contains(expected, ErrorOf(pattern), StringComparison.Ordinal);
        Assert.False(FilterPattern.Compile(pattern).IsValid);
    }

    [Fact]
    public void ABrokenPatternMatchesNothingAndKeepsItsPosition()
    {
        var range = new SourceRange(new SourcePosition(7, 9));
        var pattern = FilterPattern.Compile("[z-a]", range);

        Assert.NotNull(pattern.Error);
        Assert.False(pattern.IsMatch("anything"));
        Assert.Equal(range, pattern.Range);

        // A quantifier with nothing to quantify is the other way a pattern cannot be read.
        Assert.Equal("'?' has to follow a character, but it is at the start of the pattern", ErrorOf("?abc"));
    }

    // ------------------------------------------------------------------ escaping

    [Fact]
    public void BackslashMakesASpecialCharacterLiteral()
    {
        Assert.True(Matches(@"a\*b", "a*b"));
        Assert.False(Matches(@"a\*b", "axb"));
        Assert.True(Matches(@"v\+1", "v+1"));
        Assert.False(Matches(@"v\+1", "vv1"));
        Assert.True(Matches(@"x\?y", "x?y"));
        Assert.True(Matches(@"\!\!urgent", "!!urgent"));
        Assert.True(Matches(@"a\\b", @"a\b"));

        // Escaping is also how a literal leading `!` is kept out of the negation marker.
        var escaped = FilterPattern.Compile(@"\!important");
        Assert.False(escaped.Negated);
        Assert.True(escaped.IsMatch("!important"));

        var negated = FilterPattern.Compile("!important");
        Assert.True(negated.Negated);
        Assert.True(negated.IsMatch("important"));

        Assert.StartsWith("the pattern ends with a backslash", ErrorOf(@"oops\"), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyALeadingExclamationMarkNegates()
    {
        Assert.False(FilterPattern.Compile("a!b").Negated);
        Assert.True(Matches("a!b", "a!b"));
        Assert.False(Matches("a!b", "ab"));
    }

    // ------------------------------------------------------------------ the YAML quoting trap

    [Fact]
    public void QuotedGlobPatternIsStillJustAPatternAndMatchesWholePaths()
    {
        // `'**/README.md'` is what an author has to write: quoted, because a plain scalar may not start
        // with `*`, and the `/` after the `**` is literal, so the root-level file is NOT covered.
        Assert.True(Matches("**/README.md", "src/README.md"));
        Assert.True(Matches("**/README.md", "docs/en/README.md"));
        Assert.False(Matches("**/README.md", "README.md"));

        // Writing the intent instead: `README.md` plus `**/README.md` covers both places.
        Assert.True(Matches("README.md", "README.md"));
    }

    [Fact]
    public void PathPatternsAreMatchedAgainstTheWholePathFromTheRepositoryRoot()
    {
        Assert.True(Matches("src/*.cs", "src/Program.cs"));
        Assert.False(Matches("src/*.cs", "Program.cs"));
        Assert.False(Matches("src/*.cs", "src/nested/Program.cs"));
        Assert.True(Matches("src/**/*.cs", "src/nested/Program.cs"));

        // A pattern that happens to name a suffix still needs the whole path: GitHub does not search.
        Assert.False(Matches("Program.cs", "src/Program.cs"));
    }

    // ------------------------------------------------------------------ list ordering

    private static FilterPattern[] Patterns(params string[] texts) =>
        [.. texts.Select(text => FilterPattern.Compile(text))];

    [Fact]
    public void IncludeListAdmitsOnlyWhatItMatchesAndNamesTheDecidingPattern()
    {
        var list = Patterns("main", "release/**");

        var admitted = FilterMatcher.Match(list, "main", FilterListMode.Include);
        Assert.True(admitted.Satisfied);
        Assert.Equal("main", admitted.DecidingPattern?.RawText);

        var nested = FilterMatcher.Match(list, "release/10", FilterListMode.Include);
        Assert.True(nested.Satisfied);
        Assert.Equal("release/**", nested.DecidingPattern?.RawText);

        // Nothing matched, so the list's own sense decides — and no pattern is blamed for it.
        var rejected = FilterMatcher.Match(list, "dev", FilterListMode.Include);
        Assert.False(rejected.Satisfied);
        Assert.Null(rejected.DecidingPattern);
    }

    [Fact]
    public void ANegativePatternAfterAPositiveMatchExcludes()
    {
        var list = Patterns("main", "!ma*");
        var match = FilterMatcher.Match(list, "main", FilterListMode.Include);

        Assert.False(match.Satisfied);
        Assert.Equal("!ma*", match.DecidingPattern?.RawText);
    }

    [Fact]
    public void APositivePatternAfterANegativeMatchIncludesAgain()
    {
        // Order is the whole point of the rule: the same two patterns the other way round exclude.
        var list = Patterns("!feature/**", "feature/hotfix");
        var match = FilterMatcher.Match(list, "feature/hotfix", FilterListMode.Include);

        Assert.True(match.Satisfied);
        Assert.Equal("feature/hotfix", match.DecidingPattern?.RawText);

        var reversed = FilterMatcher.Match(Patterns("feature/hotfix", "!feature/**"), "feature/hotfix", FilterListMode.Include);
        Assert.False(reversed.Satisfied);
        Assert.Equal("!feature/**", reversed.DecidingPattern?.RawText);
    }

    [Fact]
    public void IgnoreListLetsThroughWhatItDoesNotMatch()
    {
        var list = Patterns("renovate/**");

        Assert.True(FilterMatcher.Match(list, "main", FilterListMode.Ignore).Satisfied);

        var excluded = FilterMatcher.Match(list, "renovate/10", FilterListMode.Ignore);
        Assert.False(excluded.Satisfied);
        Assert.Equal("renovate/**", excluded.DecidingPattern?.RawText);
    }

    [Fact]
    public void ANegatedPatternInsideAnIgnoreListPutsTheValueBackIn()
    {
        var list = Patterns("renovate/**", "!renovate/keep");

        Assert.False(FilterMatcher.Match(list, "renovate/drop", FilterListMode.Ignore).Satisfied);
        Assert.True(FilterMatcher.Match(list, "renovate/keep", FilterListMode.Ignore).Satisfied);
    }

    [Fact]
    public void MatchAnyFileStopsAtTheFirstFileTheListAdmits()
    {
        var list = Patterns("src/**", "!src/vendor/**");
        var files = new[] { "docs/readme.md", "src/vendor/jquery.js", "src/App.cs" };

        var match = FilterMatcher.MatchAnyFile(list, files, FilterListMode.Include);

        Assert.True(match.Satisfied);
        Assert.Equal("src/App.cs", match.Value);
        Assert.Equal(3, match.ValuesTested);

        // Every file excluded is every file refused: the last matching pattern of the last file tested.
        var refusedFiles = new[] { "docs/readme.md", "src/vendor/jquery.js" };
        var refused = FilterMatcher.MatchAnyFile(list, refusedFiles, FilterListMode.Include);
        Assert.False(refused.Satisfied);
        Assert.Equal("src/vendor/jquery.js", refused.Value);
        Assert.Equal("!src/vendor/**", refused.DecidingPattern?.RawText);
    }

    [Fact]
    public void AnIgnoreListOfNothingAdmitsAndAnIncludeListOfNothingRefuses()
    {
        Assert.False(FilterMatcher.Match([], "main", FilterListMode.Include).Satisfied);
        Assert.True(FilterMatcher.Match([], "main", FilterListMode.Ignore).Satisfied);
    }

    [Fact]
    public void CompileAllKeepsOrderTextAndPositions()
    {
        var values = new[]
        {
            new WorkflowValue(GitHubValue.FromString("main"), "main", new SourceRange(new SourcePosition(4, 9))),
            new WorkflowValue(GitHubValue.FromString("!dev"), "!dev", new SourceRange(new SourcePosition(5, 9))),
        };

        var patterns = FilterMatcher.CompileAll(values);

        Assert.Equal(2, patterns.Count);
        Assert.False(patterns[0].Negated);
        Assert.True(patterns[1].Negated);
        Assert.Equal("dev", patterns[1].Text);
        Assert.Equal(5, patterns[1].Range.Start.Line);
    }
}
