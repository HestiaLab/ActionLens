using System.Text.RegularExpressions;

namespace ActionLens.Core.Tests;

/// <summary>
/// Facts the documentation asserts about the repository, checked against the repository. The review in
/// <c>docs/review-v1-grill.md</c> (decision Q4) found ~15 stale claims — test counts, a corpus file
/// count, an ADR describing unbuilt features as accepted decisions, and four broken relative links.
/// A one-time edit fixes them; this test stops them drifting again.
/// </summary>
public sealed class DocFactsTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static readonly Regex MarkdownLink = new(
        @"\[[^\]]*\]\(([^)]+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Markdown files that are part of the project's own documentation. Vendored package docs under
    /// <c>node_modules/</c> and generated build output are not ours to keep consistent.
    /// </summary>
    private static string[] MarkdownFiles() =>
        Directory.EnumerateFiles(RepoRoot, "*.md", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void EveryRelativeMarkdownLinkResolves()
    {
        var files = MarkdownFiles();
        Assert.True(files.Length >= 15, $"Only {files.Length} Markdown files were found; the scan has gone blind.");

        var broken = new List<string>();
        foreach (var file in files)
        {
            var directory = Path.GetDirectoryName(file)!;
            foreach (Match match in MarkdownLink.Matches(File.ReadAllText(file)))
            {
                var target = match.Groups[1].Value.Trim().Split('#')[0].Trim();
                if (target.Length == 0
                    || target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var resolved = Path.GetFullPath(Path.Combine(directory, target));
                // Directory links such as `docs/adr/` are legitimate Markdown targets.
                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                {
                    broken.Add($"{Path.GetRelativePath(RepoRoot, file)} -> {target}");
                }
            }
        }

        Assert.Empty(broken);
    }

    [Fact]
    public void Adr0003DescribesOnlyTheNoBackendDecision()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "docs", "adr", "0003-local-first-no-backend.md"));

        Assert.Contains("Status: accepted", text, StringComparison.Ordinal);
        Assert.Contains("## Deferred — not implemented", text, StringComparison.Ordinal);

        // These were the phrasings that made an accepted ADR claim three features that do not exist.
        Assert.DoesNotContain("Autosave goes to", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Sharing compresses", text, StringComparison.Ordinal);
        Assert.DoesNotContain("an automated test asserts that no request leaves the page", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDeferredFeaturesAreActuallyAbsentFromTheSources()
    {
        // Guard the other direction too: an ADR that says "deferred" while the feature is quietly
        // present is as misleading as one that says "accepted" while it is absent.
        var sources = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal)
                || file.EndsWith(".razor", StringComparison.Ordinal)
                || file.EndsWith(".ts", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToArray();

        Assert.DoesNotContain(sources, text => text.Contains("localStorage", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sources, text => text.Contains("lz-string", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DocumentationDoesNotPinATestCountThatWillDrift()
    {
        // The old 1145 / 1377 / 1380 figures were historical snapshots written in the present tense.
        // Test totals belong to the test runner's output, not to prose that nobody updates.
        var offenders = new List<string>();
        foreach (var file in MarkdownFiles())
        {
            var name = Path.GetFileName(file);
            if (name.Equals("gate1-report.md", StringComparison.Ordinal)
                || name.Equals("w4-verification.md", StringComparison.Ordinal)
                || name.Equals("review-v1-grill.md", StringComparison.Ordinal))
            {
                continue; // explicit historical records, each labelled as such
            }

            var text = File.ReadAllText(file);
            foreach (var stale in new[] { "1145", "1377", "1380" })
            {
                if (text.Contains(stale, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(RepoRoot, file)} still writes {stale}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheCorpusReadmeAgreesWithTheVendoredFiles()
    {
        var corpus = Path.Combine(RepoRoot, "tests", "golden", "languageservices");
        var readme = File.ReadAllText(Path.Combine(corpus, "README.md"));
        var count = Directory.GetFiles(corpus, "*.json").Length;

        Assert.Equal(27, count);
        Assert.Contains($"{count} JSON files", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("join.json", readme, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ActionLens.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("No ActionLens.sln above the test assembly.");
    }
}
