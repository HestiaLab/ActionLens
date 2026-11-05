using ActionLens.Core.Values;

namespace ActionLens.Core.Triggers;

/// <summary>
/// The facts about one event that a trigger filter is read against — the ref it touched, the files it
/// changed, the inputs it carried — as far as the payload actually states them.
/// </summary>
/// <remarks>
/// <para>
/// Every field is nullable, and null means "the payload does not say", which is a different answer from a
/// field that says nothing (an empty list of changed files is a real fact: nothing changed). That
/// distinction is the whole content of this record: a filter that cannot be consulted must produce an
/// <c>unknown</c> with a reason, and a filter consulted against an empty list produces a definite answer.
/// </para>
/// <para>
/// It is read from the already-derived <c>github</c> context (<see cref="FromContext"/>), so the payload
/// keys behave exactly as <see cref="ActionLens.Core.Scenarios.AnalysisInput.FromEventPayload"/> documents
/// them: the webhook body's own keys arrive under <c>github.event.*</c>. On top of those, ActionLens
/// recognises a handful of keys that no webhook body carries but a caller can supply, in the same way
/// <c>vars</c> is ActionLens's own way of supplying repository variables:
/// </para>
/// <list type="table">
///   <item><term><c>changed_files</c> (array of text)</term><description>the complete list of paths the
///   event changed. This is what makes a <c>paths</c> filter decidable.</description></item>
///   <item><term><c>changed_files</c> (number)</term><description>a count only, which GitHub's own
///   <c>pull_request</c> payload is what it gives: enough to report the size, not enough to match.</description></item>
///   <item><term><c>commits_over_limit</c> (boolean)</term><description>true when the push carried more
///   than 1,000 commits, in which case GitHub runs the workflow whatever the filters say.</description></item>
///   <item><term><c>diff_timed_out</c> (boolean)</term><description>true when GitHub did not manage to
///   generate the diff, which has the same documented effect.</description></item>
///   <item><term><c>diff_truncated</c> (boolean)</term><description>true when the diff held more files
///   than GitHub's filter reads (3,000 on the hosted service, 300 on GHES), so a match may sit past the
///   end of what was considered.</description></item>
///   <item><term><c>ghes</c> (boolean)</term><description>which of the two file limits the reason names.</description></item>
///   <item><term><c>commit_count</c> (number)</term><description>how many commits the push carried, when
///   the caller knows it; the webhook's own <c>commits</c> list stops at 20 entries and so proves nothing.</description></item>
/// </list>
/// </remarks>
public sealed record TriggerPayload
{
    /// <summary>The payload that knows nothing, which is what an analysis without an event payload uses.</summary>
    public static TriggerPayload Empty { get; } = new();

    /// <summary>The per-commit arrays a webhook push body uses to name the files a commit touched.</summary>
    private static readonly string[] CommitFileKeys = ["added", "modified", "removed"];

    /// <summary>The documented commit count above which GitHub stops filtering and always runs.</summary>
    public const int CommitsThatForceAnUnfilteredRun = 1_000;

    /// <summary>True when a payload was supplied at all, so the event's own fields are worth asking for.</summary>
    public bool HasPayload { get; init; }

    /// <summary><c>github.event_name</c>: which event the payload says it is.</summary>
    public string? EventName { get; init; }

    /// <summary><c>github.ref</c>, the full ref name — <c>refs/heads/main</c>, <c>refs/tags/v1.2</c>.</summary>
    public string? Ref { get; init; }

    /// <summary>The ref without its <c>refs/heads/</c> or <c>refs/tags/</c> prefix, which is what patterns match.</summary>
    public string? RefName { get; init; }

    /// <summary><c>branch</c> or <c>tag</c> when the payload says; null when neither the type nor a full ref tells it.</summary>
    public string? RefType { get; init; }

    /// <summary>The base branch of a pull request: what <c>branches</c> filters for that event.</summary>
    public string? BaseRef { get; init; }

    /// <summary>The head branch of a pull request, reported in a reason because a filter cannot reach it.</summary>
    public string? HeadRef { get; init; }

    /// <summary><c>github.event.action</c>: the activity type a <c>types</c> filter reads.</summary>
    public string? Action { get; init; }

    /// <summary>The changed files, when the payload states the list; null when it does not.</summary>
    public IReadOnlyList<string>? ChangedFiles { get; init; }

    /// <summary>How many files changed, when the payload states a count but not the names.</summary>
    public int? ChangedFileCount { get; init; }

    /// <summary>True when the file list was assembled from a webhook's per-commit lists, which are capped.</summary>
    public bool ChangedFilesFromCommits { get; init; }

    /// <summary>The number of commits in the push, when the payload states one.</summary>
    public int? CommitCount { get; init; }

    /// <summary>True when the payload states the push carried more commits than GitHub filters.</summary>
    public bool? CommitsOverLimit { get; init; }

    /// <summary>True when the payload states GitHub's diff generation timed out.</summary>
    public bool? DiffTimedOut { get; init; }

    /// <summary>True when the payload states the diff held more files than GitHub's filter reads.</summary>
    public bool? DiffTruncated { get; init; }

    /// <summary>True when the payload says the repository is on GitHub Enterprise Server, which changes the file limit.</summary>
    public bool? GitHubEnterprise { get; init; }

    /// <summary>The <c>workflow_dispatch</c> / <c>workflow_call</c> inputs the run carried, by name.</summary>
    public IReadOnlyDictionary<string, string>? Inputs { get; init; }

    /// <summary>The file ceiling GitHub's path filter reads up to: 3,000 hosted, 300 on GHES.</summary>
    public int DiffFileLimit => GitHubEnterprise == true ? 300 : 3_000;

    /// <summary>True when the payload states this very event, so the event happening is a supplied fact.</summary>
    public bool OccurrenceIsPinnedBy(string eventName) =>
        HasPayload && EventName is { } name && string.Equals(name, eventName, StringComparison.Ordinal);

    /// <summary>True when the commit count or the diff state means GitHub filtered nothing at all.</summary>
    public bool ForcesAnUnfilteredRun =>
        CommitsOverLimit == true || DiffTimedOut == true || CommitCount > CommitsThatForceAnUnfilteredRun;

    /// <summary>
    /// Reads the facts a trigger filter needs out of the derived <c>github</c> context and the dispatch
    /// inputs. Both null, which is what an analysis with no payload produces, gives <see cref="Empty"/>.
    /// </summary>
    /// <remarks>
    /// Payload keys are read with <see cref="StringComparison.Ordinal"/> because that is how the JSON body
    /// is keyed, unlike the expression language's case-insensitive property access: <c>Changed_Files</c> in
    /// a payload is not the key ActionLens reads.
    /// </remarks>
    public static TriggerPayload FromContext(GitHubValue? github, GitHubValue? inputs)
    {
        if (github is null)
        {
            return Empty;
        }

        var @event = Member(github, "event");
        var refValue = Text(github, "ref");
        var declaredName = Text(github, "ref_name") ?? Text(@event, "ref_name");
        var declaredType = Text(github, "ref_type") ?? Text(@event, "ref_type");
        var (derivedName, derivedType) = SplitRef(refValue);
        var commits = Member(@event, "commits");
        var explicitFiles = TextList(Member(@event, "changed_files"));
        var commitFiles = CommitFileList(commits);
        var fromCommits = explicitFiles is null && commitFiles.Count > 0;
        var pullRequest = Member(@event, "pull_request");

        return new TriggerPayload
        {
            HasPayload = true,
            EventName = Text(github, "event_name"),
            Ref = refValue,
            RefName = declaredName ?? derivedName,
            RefType = declaredType ?? derivedType,
            BaseRef = Text(github, "base_ref") ?? Text(Member(pullRequest, "base"), "ref"),
            HeadRef = Text(github, "head_ref") ?? Text(Member(pullRequest, "head"), "ref"),
            Action = Text(@event, "action"),
            // A list assembled from the webhook's per-commit arrays is capped at 20 commits, so it is a
            // list ActionLens holds but not a diff GitHub filtered against; the flag keeps those apart.
            ChangedFiles = explicitFiles ?? (fromCommits ? commitFiles : null),
            ChangedFileCount = explicitFiles?.Count ?? Count(Member(@event, "changed_files")),
            ChangedFilesFromCommits = fromCommits,
            CommitCount = Count(Member(@event, "commit_count")) ?? Count(commits),
            CommitsOverLimit = Flag(Member(@event, "commits_over_limit")),
            DiffTimedOut = Flag(Member(@event, "diff_timed_out")),
            DiffTruncated = Flag(Member(@event, "diff_truncated")),
            GitHubEnterprise = Flag(Member(@event, "ghes")),
            Inputs = InputMap(inputs),
        };
    }

    private static (string? Name, string? Type) SplitRef(string? fullRef) => fullRef switch
    {
        null => (null, null),
        _ when fullRef.StartsWith("refs/heads/", StringComparison.Ordinal) =>
            (fullRef["refs/heads/".Length..], "branch"),
        _ when fullRef.StartsWith("refs/tags/", StringComparison.Ordinal) =>
            (fullRef["refs/tags/".Length..], "tag"),
        _ => (fullRef, null),
    };

    private static GitHubValue Member(GitHubValue? value, string name)
    {
        if (value is null || value.Kind != GitHubValueKind.Object)
        {
            return GitHubValue.Null;
        }

        return value.TryGetMember(GitHubValue.FromString(name), StringComparison.Ordinal, out var member)
            ? member
            : GitHubValue.Null;
    }

    private static string? Text(GitHubValue? value, string name)
    {
        var member = Member(value, name);
        return member.Kind == GitHubValueKind.String ? member.String : null;
    }

    private static bool? Flag(GitHubValue value) => value.Kind switch
    {
        GitHubValueKind.Boolean => value.Boolean,
        GitHubValueKind.String when value.String.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
        GitHubValueKind.String when value.String.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
        _ => null,
    };

    private static int? Count(GitHubValue value) => value.Kind switch
    {
        GitHubValueKind.Number when value.Number > int.MinValue && value.Number < int.MaxValue =>
            (int)value.Number,
        GitHubValueKind.Array => value.Array.Count,
        _ => null,
    };

    private static List<string>? TextList(GitHubValue value)
    {
        if (value.Kind != GitHubValueKind.Array)
        {
            return null;
        }

        var texts = new List<string>(value.Array.Count);
        foreach (var element in value.Array)
        {
            if (element.Kind == GitHubValueKind.String)
            {
                texts.Add(element.String);
            }
        }

        return texts.Count == value.Array.Count ? texts : null;
    }

    /// <summary>
    /// The union of the per-commit <c>added</c>, <c>modified</c> and <c>removed</c> arrays of a webhook push
    /// body. Order is payload order, so two reads of one body list the same files the same way.
    /// </summary>
    private static List<string> CommitFileList(GitHubValue commits)
    {
        var files = new List<string>();
        if (commits.Kind != GitHubValueKind.Array)
        {
            return files;
        }

        foreach (var commit in commits.Array)
        {
            foreach (var key in CommitFileKeys)
            {
                var list = TextList(Member(commit, key));
                if (list is null)
                {
                    // A commit that states none of the three is a commit whose files are unknown, so the
                    // whole list would be incomplete and must not be read as if it were the diff.
                    return [];
                }

                foreach (var file in list)
                {
                    if (!files.Contains(file, StringComparer.Ordinal))
                    {
                        files.Add(file);
                    }
                }
            }
        }

        return files;
    }

    private static Dictionary<string, string>? InputMap(GitHubValue? inputs)
    {
        if (inputs is null || inputs.Kind != GitHubValueKind.Object)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in inputs.Object)
        {
            map[pair.Key] = pair.Value.Stringify();
        }

        return map;
    }
}
