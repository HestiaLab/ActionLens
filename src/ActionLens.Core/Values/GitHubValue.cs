using System.Globalization;
using System.Text;

namespace ActionLens.Core.Values;

/// <summary>The value types a GitHub Actions expression can hold.</summary>
public enum GitHubValueKind
{
    Null,
    Boolean,
    Number,
    String,
    Object,
    Array,
}

/// <summary>
/// An immutable workflow value, modelling exactly the six types GitHub's expression language has and
/// nothing else. It carries no "unknown" state: whether a value is knowable at all is decided by the
/// evaluator (<c>ActionLens.Core.Expressions</c>), which keeps this type a faithful picture of what
/// GitHub itself would hold.
/// </summary>
public sealed class GitHubValue : IEquatable<GitHubValue>
{
    private readonly double _number;
    private readonly string? _text;
    private readonly IReadOnlyDictionary<string, GitHubValue>? _object;
    private readonly IReadOnlyList<GitHubValue>? _array;

    private GitHubValue(GitHubValueKind kind, bool boolean, double number, string? text,
        IReadOnlyDictionary<string, GitHubValue>? objectValue, IReadOnlyList<GitHubValue>? arrayValue)
    {
        Kind = kind;
        Boolean = boolean;
        _number = number;
        _text = text;
        _object = objectValue;
        _array = arrayValue;
    }

    public static GitHubValue Null { get; } = new(GitHubValueKind.Null, false, 0, null, null, null);

    public static GitHubValue True { get; } = new(GitHubValueKind.Boolean, true, 0, null, null, null);

    public static GitHubValue False { get; } = new(GitHubValueKind.Boolean, false, 0, null, null, null);

    public GitHubValueKind Kind { get; }

    public bool Boolean { get; }

    public static GitHubValue FromBoolean(bool value) => value ? True : False;

    public static GitHubValue FromNumber(double value) =>
        new(GitHubValueKind.Number, false, value, null, null, null);

    public static GitHubValue FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new GitHubValue(GitHubValueKind.String, false, 0, value, null, null);
    }

    public static GitHubValue FromObject(IReadOnlyDictionary<string, GitHubValue> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new GitHubValue(GitHubValueKind.Object, false, 0, null, value, null);
    }

    public static GitHubValue FromArray(IReadOnlyList<GitHubValue> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new GitHubValue(GitHubValueKind.Array, false, 0, null, null, value);
    }

    public static GitHubValue EmptyObject { get; } =
        FromObject(new Dictionary<string, GitHubValue>(StringComparer.Ordinal));

    public static GitHubValue EmptyArray { get; } = FromArray(System.Array.Empty<GitHubValue>());

    /// <summary>The text of a string value; <see cref="InvalidOperationException"/> for other kinds.</summary>
    public string String => Kind == GitHubValueKind.String
        ? _text!
        : throw WrongKind(GitHubValueKind.String);

    /// <summary>The payload of a number value; <see cref="InvalidOperationException"/> for other kinds.</summary>
    public double Number => Kind == GitHubValueKind.Number
        ? _number
        : throw WrongKind(GitHubValueKind.Number);

    public IReadOnlyDictionary<string, GitHubValue> Object => Kind == GitHubValueKind.Object
        ? _object!
        : throw WrongKind(GitHubValueKind.Object);

    public IReadOnlyList<GitHubValue> Array => Kind == GitHubValueKind.Array
        ? _array!
        : throw WrongKind(GitHubValueKind.Array);

    public bool TryAsString(out string value)
    {
        value = _text ?? string.Empty;
        return Kind == GitHubValueKind.String;
    }

    public bool TryAsNumber(out double value)
    {
        value = _number;
        return Kind == GitHubValueKind.Number;
    }

    /// <summary>
    /// Children of an object or array value, in document order, for walkers that do not care which
    /// kind they are looking at.
    /// </summary>
    public IEnumerable<GitHubValue> Children => Kind switch
    {
        GitHubValueKind.Object => _object!.Values,
        GitHubValueKind.Array => _array!,
        _ => Enumerable.Empty<GitHubValue>(),
    };

    /// <summary>
    /// Reads a property of an object, or an element of an array by numeric index. The caller picks
    /// <paramref name="keyComparison"/> because how GitHub resolves <c>a.b</c> for a differently
    /// cased key is specified once, in the evaluator, and pinned by tests.
    /// </summary>
    public bool TryGetMember(GitHubValue member, StringComparison keyComparison, out GitHubValue value)
    {
        if (Kind == GitHubValueKind.Object && member.Kind == GitHubValueKind.String)
        {
            return TryGetProperty(_object!, member._text!, keyComparison, out value);
        }

        if (Kind == GitHubValueKind.Array && member.Kind == GitHubValueKind.Number)
        {
            return TryGetElement(_array!, member._number, out value);
        }

        value = Null;
        return false;
    }

    /// <summary>
    /// Reads what a bracket index <c>a[i]</c> selects, which is not the same question as <c>a.b</c>: the
    /// index is any value and Actions converts it to the kind the target has. An object is keyed by the
    /// index as text — <c>foo[5]</c> reads the property <c>"5"</c> and <c>foo[null]</c> the property
    /// <c>""</c> — while an array is indexed by the coerced number, so <c>foo[true]</c> is element 1 and
    /// <c>foo[' 0x02 ']</c> is element 2. A composite index matches nothing at all, even though it has
    /// text: <c>foo[fromJSON('{}')]</c> does not read a property named <c>Object</c>, while
    /// <c>foo[format('{0}', fromJSON('{}'))]</c> does, because the second index really is text.
    /// </summary>
    /// <remarks>
    /// Pinned by <c>op_idx.json</c> (<c>object-index-coercion</c>, <c>array-index-coercion</c>) and, for
    /// the same rules applied to each element of a wildcard path, by <c>op_idx_star.json</c>.
    /// </remarks>
    public bool TryGetIndex(GitHubValue index, StringComparison keyComparison, out GitHubValue value)
    {
        if (IsComposite(index))
        {
            value = Null;
            return false;
        }

        if (Kind == GitHubValueKind.Object)
        {
            return TryGetProperty(_object!, index.Stringify(), keyComparison, out value);
        }

        if (Kind == GitHubValueKind.Array)
        {
            var position = index.ToCoercedNumber();
            if (!double.IsFinite(position) || Math.Truncate(position) != position)
            {
                // NaN, ±Infinity and a fraction are not array positions in JavaScript either, so they
                // select nothing rather than the nearest whole element.
                value = Null;
                return false;
            }

            return TryGetElement(_array!, position, out value);
        }

        value = Null;
        return false;
    }

    private static bool TryGetProperty(
        IReadOnlyDictionary<string, GitHubValue> properties,
        string name,
        StringComparison keyComparison,
        out GitHubValue value)
    {
        if (properties.TryGetValue(name, out var exact))
        {
            value = exact!;
            return true;
        }

        foreach (var pair in properties)
        {
            if (string.Equals(pair.Key, name, keyComparison))
            {
                value = pair.Value;
                return true;
            }
        }

        value = Null;
        return false;
    }

    private static bool TryGetElement(IReadOnlyList<GitHubValue> elements, double position, out GitHubValue value)
    {
        var index = (int)position;
        if (index >= 0 && index < elements.Count)
        {
            value = elements[index];
            return true;
        }

        value = Null;
        return false;
    }

    private static bool IsComposite(GitHubValue value) =>
        value.Kind is GitHubValueKind.Array or GitHubValueKind.Object;

    /// <summary>
    /// Concatenates an array's elements with <paramref name="separator"/>, for <c>join()</c>.
    /// </summary>
    public string JoinWith(string separator) => string.Join(separator, Children.Select(child => child.Stringify()));

    /// <summary>
    /// Converts to text the way Actions' string coercion does. Two details are easy to get wrong and
    /// both are pinned by the official corpus: <c>null</c> becomes the empty string rather than
    /// <c>"null"</c>, and an array or object becomes the literal name of its kind instead of a
    /// rendering of its contents.
    /// </summary>
    public string Stringify() => Kind switch
    {
        GitHubValueKind.Null => string.Empty,
        GitHubValueKind.Boolean => Boolean ? "true" : "false",
        GitHubValueKind.Number => FormatNumber(_number),
        GitHubValueKind.String => _text!,
        GitHubValueKind.Array => "Array",
        _ => "Object",
    };

    /// <summary>
    /// Actions' truthiness: <c>false</c>, <c>0</c>, <c>-0</c>, <c>NaN</c>, <c>""</c> and <c>null</c> are
    /// falsy and everything else is truthy — including the strings <c>"false"</c> and <c>"0"</c>, and
    /// every array and object. That a non-empty <c>"false"</c> is truthy is exactly why
    /// <c>if: false</c> and <c>if: 'false'</c> behave differently.
    /// </summary>
    public bool IsTruthy() => Kind switch
    {
        GitHubValueKind.Null => false,
        GitHubValueKind.Boolean => Boolean,
        GitHubValueKind.Number => _number != 0d && !double.IsNaN(_number),
        GitHubValueKind.String => _text!.Length > 0,
        _ => true,
    };

    /// <summary>
    /// Converts to a number the way Actions' comparison coercion does: <c>null</c> is 0, booleans are 0
    /// and 1, text is parsed as a number and becomes <c>NaN</c> when it is not one, and arrays and
    /// objects are never numbers. Blank text counts as 0.
    /// </summary>
    /// <remarks>
    /// The text conversion is JavaScript's <c>Number()</c>, not a JSON number: <see cref="NumberGrammar"/>
    /// also reads the radix spellings and the <c>Infinity</c>/<c>NaN</c> words, which is how
    /// <c>' 0xff ' == 255</c> and <c>' Infinity ' == Infinity</c> come out true, and — importantly — how
    /// <c>' abc ' == NaN</c> comes out <c>false</c> rather than undecidable. Text that is not a number
    /// converts <em>to</em> <c>NaN</c>; it is not a failed conversion.
    /// </remarks>
    public double ToCoercedNumber() => Kind switch
    {
        GitHubValueKind.Null => 0d,
        GitHubValueKind.Boolean => Boolean ? 1d : 0d,
        GitHubValueKind.Number => _number,
        GitHubValueKind.String => NumberGrammar.TryConvertText(_text!, out var parsed)
            ? parsed
            : double.NaN,
        _ => double.NaN,
    };

    /// <summary>Number formatting is <c>G15</c>, matching the C# runner, with negative zero printing as 0.</summary>
    internal static string FormatNumber(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "Infinity" : "-Infinity";
        }

        // Covers both 0 and -0; Actions renders -0 as 0.
        if (value == 0d)
        {
            return "0";
        }

        return value.ToString("G15", CultureInfo.InvariantCulture);
    }


    /// <summary>
    /// Structural equality over the value graph. Numbers compare by their double value, so 1 and 1.0
    /// are the same value, and object comparison is order-insensitive.
    /// </summary>
    public bool Equals(GitHubValue? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            GitHubValueKind.Null => true,
            GitHubValueKind.Boolean => Boolean == other.Boolean,
            GitHubValueKind.Number => _number.Equals(other._number),
            GitHubValueKind.String => string.Equals(_text, other._text, StringComparison.Ordinal),
            GitHubValueKind.Array => _array!.SequenceEqual(other._array!),
            GitHubValueKind.Object => _object!.Count == other._object!.Count
                && _object.All(pair => other._object.TryGetValue(pair.Key, out var peer) && pair.Value.Equals(peer)),
            _ => false,
        };
    }

    public override bool Equals(object? obj) => Equals(obj as GitHubValue);

    public override int GetHashCode() => Kind switch
    {
        GitHubValueKind.Null => 0,
        GitHubValueKind.Boolean => Boolean.GetHashCode(),
        GitHubValueKind.Number => _number.GetHashCode(),
        GitHubValueKind.String => StringComparer.Ordinal.GetHashCode(_text!),
        GitHubValueKind.Array => _array!.Aggregate(17, (hash, item) => (hash * 31) + item.GetHashCode()),
        // Equals() compares objects irrespective of the order their keys were inserted in, so the hash
        // has to be commutative too. The old Aggregate mixed each pair into a running (order-sensitive)
        // hash, which let two equal objects land in different buckets.
        _ => unchecked(_object!.Aggregate(
            17,
            (hash, pair) => hash + (StringComparer.Ordinal.GetHashCode(pair.Key) * 397) + pair.Value.GetHashCode())),
    };

    public override string ToString() => Kind switch
    {
        GitHubValueKind.Object => DescribeObject(),
        GitHubValueKind.Array => $"[{string.Join(", ", _array!.Select(child => child.ToString()))}]",
        _ => Stringify(),
    };

    private string DescribeObject()
    {
        var builder = new StringBuilder("{");
        var first = true;
        foreach (var pair in _object!)
        {
            if (!first)
            {
                builder.Append(", ");
            }

            builder.Append(pair.Key).Append(": ").Append(pair.Value);
            first = false;
        }

        return builder.Append('}').ToString();
    }

    private InvalidOperationException WrongKind(GitHubValueKind expected) =>
        new($"Expected a {expected} value but found {Kind}.");
}
