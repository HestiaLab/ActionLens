namespace ActionLens.Core.Text;

/// <summary>
/// Where each field of a workflow node came from in the source text. Keys are the YAML field names
/// as written (<c>runs-on</c>, <c>if</c>, <c>uses</c>, ...) so that a lint rule can ask for the
/// position of exactly the thing it is complaining about.
/// </summary>
/// <remarks>
/// A field's range is its <b>value</b> node, from its start: <c>Get("runs-on")</c> on
/// <c>    runs-on: ubuntu-latest</c> is the position of <c>ubuntu-latest</c>, not of the key. That is what
/// a finding about a value needs; findings about a name that GitHub does not accept point at the key
/// instead (see <c>WorkflowModelBuilder</c>).
/// <para>
/// Entries produced by a YAML merge key (<c>&lt;&lt;: *anchor</c>) point at the anchor definition,
/// not at the use site; <see cref="IsInherited"/> records which field was reused so the UI can say
/// so instead of looking like it jumped to the wrong line.
/// </para>
/// </remarks>
public sealed class SourceMap
{
    public static SourceMap Empty { get; } = new();

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public void Set(string field, SourceRange range) => _entries[field] = new Entry(range, InheritedFrom: false);

    public void SetInherited(string field, SourceRange range) => _entries[field] = new Entry(range, InheritedFrom: true);

    public bool TryGet(string field, out SourceRange range)
    {
        if (_entries.TryGetValue(field, out var entry))
        {
            range = entry.Range;
            return true;
        }

        range = default;
        return false;
    }

    /// <summary>Returns the position of <paramref name="field"/>, or <see cref="SourceRange.Unknown"/>.</summary>
    public SourceRange Get(string field) => _entries.TryGetValue(field, out var entry) ? entry.Range : SourceRange.Unknown;

    public bool IsInherited(string field) => _entries.TryGetValue(field, out var entry) && entry.InheritedFrom;

    public IEnumerable<string> Fields => _entries.Keys.OrderBy(key => key, StringComparer.Ordinal);

    private readonly record struct Entry(SourceRange Range, bool InheritedFrom);
}
