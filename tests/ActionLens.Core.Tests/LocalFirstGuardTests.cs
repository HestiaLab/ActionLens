using System.Text.RegularExpressions;

namespace ActionLens.Core.Tests;

/// <summary>
/// Encodes the local-first promise (<c>docs/adr/0003-local-first-no-backend.md</c>): after the app is
/// loaded, the analysis must not reach the network.
/// </summary>
/// <remarks>
/// This is a source-level guard, deliberately. It cannot prove the absence of a request the way driving a
/// real browser and reading its request log can — that check belongs to the Playwright phase — but it does
/// catch the regression this project is actually likely to suffer: someone adding a fetch to "just get the
/// default branch name" and shipping it because every other gate stayed green.
/// <para>
/// It matches <em>construction sites</em> in code, not URLs and not prose. A help link containing
/// <c>https://docs.github.com/…</c> is data shown to the reader, and Program.cs says out loud that there is
/// no <c>HttpClient</c>; both must pass. So comments are stripped before matching — a comment cannot issue
/// a request, and matching one would train people to distrust the guard.
/// </para>
/// </remarks>
public sealed class LocalFirstGuardTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();

    private static readonly string[] AppSourceGlobs = ["*.cs", "*.razor", "*.ts", "*.js", "*.mjs", "*.css", "*.html"];

    /// <summary>
    /// Every directory the guard walks. The components moved to <c>src/ActionLens.Ui</c> when the UI
    /// became a Razor class library, and the Node build scripts live at the repository root; a guard that
    /// kept scanning only the host would have silently stopped covering most of the UI and all of the
    /// script chain while still passing, which is exactly the failure mode
    /// <see cref="GuardScansEveryConfiguredDirectoryAndFindsRealSource"/> exists to prevent.
    /// </summary>
    private static readonly string[] GuardedSourceDirectories =
    [
        Path.Combine("src", "ActionLens.App"),
        Path.Combine("src", "ActionLens.Ui"),
        "scripts",
    ];

    private static readonly Regex LineComment = new(
        @"^\s*//.*$",
        RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BlockComment = new(
        @"/\*[\s\S]*?\*/|@\*[\s\S]*?\*@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly (string Label, Regex Pattern)[] Forbidden =
    [
        ("HttpClient", new Regex(@"\bHttpClient\b|\.HttpClient\s*=|AddHttpClient\b|\bIHttpClientFactory\b|\bHttpClientHandler\b|\bSocketsHttpHandler\b|\bHttpMessageInvoker\b|\bHttpWebRequest\b|\bWebRequest\b", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        ("WebSocket", new Regex(@"\bClientWebSocket\b|\bWebSocket\b\s*=|new WebSocket\(|\bHubConnection\b|\bGrpcChannel\b|\bTcpClient\b", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        ("fetch", new Regex(@"\bfetch\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        ("XMLHttpRequest", new Regex(@"\bXMLHttpRequest\b", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        ("sendBeacon", new Regex(@"sendBeacon\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        ("EventSource", new Regex(@"\bnew EventSource\b", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        ("importScripts", new Regex(@"\bimportScripts\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        ("dynamic import", new Regex(@"\bimport\s*\(\s*[""']\s*(?:https?:)?//", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
    ];

    /// <summary>
    /// External hosts referenced from an asset attribute or a stylesheet import. Anything that is not a
    /// same-origin or <c>_content</c> relative path would be a CDN, and ADR 0003 forbids fonts, scripts
    /// and styles from outside the app. Single quotes, unquoted attributes and <c>@import url()</c> all
    /// count; <c>data:</c> URIs (the SVG namespace inside one) do not.
    /// </summary>
    private static readonly Regex ExternalAsset = new(
        """(?:(?:src|href)\s*=\s*|@import\s+(?:url\(\s*)?)(?:["']\s*)?(?:https?:)?//""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    [Fact]
    public void AppSourceNeverConstructsANetworkClient()
    {
        var offenders = new List<string>();
        foreach (var relative in GuardedSourceDirectories)
        {
            var directory = Path.Combine(RepoRoot, relative);
            Assert.True(Directory.Exists(directory), $"Expected the guarded project at {directory}.");

            foreach (var file in Enumerate(directory))
            {
                var text = WithoutComments(File.ReadAllText(file));
                foreach (var (label, pattern) in Forbidden)
                {
                    if (pattern.IsMatch(text))
                    {
                        offenders.Add($"{Relative(file)} uses {label}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Proves the guard's path list is live, not decorative: a scan whose directories are mistyped or
    /// dropped matches nothing and still passes, which is the same silent-blindness this project has
    /// already been bitten by twice. Each configured directory must exist and must actually yield source
    /// files, including at least one component from each layer (host and Ui) so neither side can quietly
    /// leave the guard's scope.
    /// </summary>
    [Fact]
    public void GuardScansEveryConfiguredDirectoryAndFindsRealSource()
    {
        var scanned = GuardedSourceDirectories
            .SelectMany(relative => Enumerate(Path.Combine(RepoRoot, relative)))
            .Select(Relative)
            .ToList();

        Assert.Contains(
            scanned,
            file => file.Replace('\\', '/') == "src/ActionLens.App/Program.cs");
        Assert.Contains(
            scanned,
            file => file.Replace('\\', '/') == "src/ActionLens.Ui/Components/TopBar.razor");
        Assert.Contains(
            scanned,
            file => file.Replace('\\', '/') == "src/ActionLens.Ui/Services/AnalysisSession.cs");

        // Adding a glob is not evidence that it matched anything. These three are the file types the
        // guard used to skip entirely (stylesheets, Node build scripts, page markup); if a path or a
        // pattern is dropped again, this fails rather than leaving a quietly narrower guard.
        Assert.Contains(scanned, file => file.EndsWith("app.css", StringComparison.Ordinal));
        Assert.Contains(scanned, file => file.EndsWith("build-editor.mjs", StringComparison.Ordinal));
        Assert.Contains(scanned, file => file.EndsWith("index.html", StringComparison.Ordinal));
    }

    /// <summary>
    /// Guards the guard. A source scan that silently matches nothing is worse than no scan, because it
    /// looks like a passing safety test: these two cases pin that prose is ignored and code is not.
    /// </summary>
    [Fact]
    public void GuardIgnoresProseButStillCatchesCode()
    {
        Assert.DoesNotContain("HttpClient", Scan(
            """
            // There is deliberately no HttpClient here.
            /* fetch( is mentioned in this block too. */
            @* <script src="https://cdn.example.com/x.js"></script> *@
            var label = "https://docs.github.com/actions";
            """));

        Assert.Contains("HttpClient", Scan("var http = new HttpClient();"));
        Assert.Contains("HttpClient", Scan("services.AddSingleton<IHttpClientFactory, Factory>();"));
        Assert.Contains("WebSocket", Scan("this.socket = new WebSocket(url);"));
        Assert.Contains("fetch", Scan("const r = await fetch('/api');"));
        Assert.Contains("dynamic import", Scan("const m = await import('https://cdn.example.com/x.js');"));
    }

    /// <summary>
    /// The asset half of the guard has its own self-test too. A regex that only understood double-quoted
    /// HTML attributes used to pass while a single-quoted script tag, an unquoted href or a CSS
    /// <c>@import</c> could point at a CDN. The data-URI case matters: the stylesheet embeds an SVG whose
    /// namespace is an <c>http://</c> URL, and that is not a request.
    /// </summary>
    [Fact]
    public void AssetGuardCatchesExternalHostsButNotLocalOnes()
    {
        Assert.Matches(ExternalAsset, """<script src="https://cdn.example.com/x.js"></script>""");
        Assert.Matches(ExternalAsset, "<link href='//cdn.example.com/x.css' />");
        Assert.Matches(ExternalAsset, "<img src=//cdn.example.com/x.png />");
        Assert.Matches(ExternalAsset, """@import url("https://cdn.example.com/x.css");""");

        Assert.DoesNotMatch(ExternalAsset, """<link href="_content/Atlas.Blazor/atlas.css" />""");
        Assert.DoesNotMatch(ExternalAsset, """<base href="/" />""");
        Assert.DoesNotMatch(ExternalAsset, """<script src="_framework/blazor.webassembly.js"></script>""");
        Assert.DoesNotMatch(ExternalAsset, "background: url(data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciPjwvc3ZnPg==);");
    }

    private static List<string> Scan(string source)
    {
        var text = WithoutComments(source);
        return Forbidden
            .Where(entry => entry.Pattern.IsMatch(text))
            .Select(entry => entry.Label)
            .ToList();
    }

    private static string WithoutComments(string source) =>
        BlockComment.Replace(LineComment.Replace(source, string.Empty), string.Empty);

    [Fact]
    public void AppHtmlAndStylesheetsReferenceNoExternalAssets()
    {
        // The original guard read only index.html. A stylesheet with `@import url(//cdn…)` or a second
        // page with a remote script would have passed, so every page and stylesheet the app ships is
        // checked here. The vendored CodeMirror bundle is third-party output and stays out of scope.
        var assets = GuardedSourceDirectories
            .SelectMany(relative => Enumerate(Path.Combine(RepoRoot, relative)))
            .Where(file => file.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Contains(assets, file => file.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(assets, file => file.EndsWith("app.css", StringComparison.OrdinalIgnoreCase));

        var offenders = new List<string>();
        foreach (var asset in assets)
        {
            foreach (var line in File.ReadAllLines(asset))
            {
                // The Blazor framework markers are template placeholders resolved to same-origin URLs.
                if (line.Contains("_framework", StringComparison.Ordinal))
                {
                    continue;
                }

                if (ExternalAsset.IsMatch(line))
                {
                    offenders.Add($"{Relative(asset)}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    private static IEnumerable<string> Enumerate(string directory) =>
        AppSourceGlobs.SelectMany(pattern => Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                // The committed CodeMirror bundle is third-party output; its internals are not this
                // project's promise, and scanning it would only ever report vendored library code.
                && !file.Contains($"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    private static string Relative(string file) => Path.GetRelativePath(RepoRoot, file);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ActionLens.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("No ActionLens.sln above the test assembly; the local-first guard cannot run.");
    }
}
