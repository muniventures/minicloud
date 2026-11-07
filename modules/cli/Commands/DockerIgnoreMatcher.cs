// SPDX-License-Identifier: MPL-2.0

using System.Text;
using System.Text.RegularExpressions;

namespace Minicloud.Cli.Commands;

// Implements Moby's ordered MatchesOrParentMatches contract, not Git ignore semantics.
// Reference: https://github.com/moby/patternmatcher (Apache-2.0).
internal sealed class DockerIgnoreMatcher
{
    private const string AnyRune = "(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^\\uD800-\\uDFFF])";
    private const string NonSeparatorRune = "(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^/\\uD800-\\uDFFF])";
    private readonly List<(bool Include, Regex Pattern)> _patterns = [];

    public DockerIgnoreMatcher(IEnumerable<string> lines)
    {
        var first = true;
        foreach (var raw in lines)
        {
            var line = first ? raw.TrimStart('\uFEFF') : raw;
            first = false;
            if (line.StartsWith('#')) { continue; }
            line = line.Trim();
            if (line.Length == 0) { continue; }
            var include = line.StartsWith('!');
            if (include) { line = line[1..].Trim(); }
            if (OperatingSystem.IsWindows()) { line = line.Replace('\\', '/'); }
            var rooted = line.StartsWith('/');
            var parts = new List<string>();
            foreach (var part in line.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == "." || (rooted && part == ".." && parts.Count == 0)) { continue; }
                if (part == ".." && parts.Count > 0 && parts[^1] != "..") { parts.RemoveAt(parts.Count - 1); }
                else { parts.Add(part); }
            }
            line = string.Join('/', parts);
            if (line.Length == 0 && !include) { continue; }
            if (line.Length == 0) { throw InvalidPattern(); }
            _patterns.Add((include, Compile(line)));
        }
    }

    public bool Excludes(string path)
    {
        var excluded = false;
        path = path.Replace(Path.DirectorySeparatorChar, '/');
        var parents = new List<string> { path };
        for (var index = path.IndexOf('/'); index >= 0; index = path.IndexOf('/', index + 1))
        {
            parents.Add(path[..index]);
        }
        foreach (var (include, pattern) in _patterns)
        {
            if (include != excluded) { continue; }
            if (parents.Any(parent => pattern.IsMatch(parent))) { excluded = !include; }
        }
        return excluded;
    }

    private static Regex Compile(string pattern)
    {
        if (pattern.StartsWith("**", StringComparison.Ordinal) && pattern.Length > 2 && !pattern[2..].Any(c => "*?[]\\".Contains(c)))
        {
            var suffix = pattern[2..];
            var expression = suffix.StartsWith('/') ? "(?:.*" + Regex.Escape(suffix) + "|" + Regex.Escape(suffix[1..]) + ")" : ".*" + Regex.Escape(suffix);
            return new Regex("\\A" + expression + "\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        var result = new StringBuilder("\\A");
        for (var index = 0; index < pattern.Length; index++)
        {
            var value = pattern[index];
            switch (value)
            {
                case '*':
                    if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                    {
                        index++;
                        if (index + 1 < pattern.Length && pattern[index + 1] == '/') { index++; }
                        result.Append(index == pattern.Length - 1 ? ".*" : "(?:.*/)?");
                    }
                    else { result.Append(NonSeparatorRune).Append('*'); }
                    break;
                case '?': result.Append(NonSeparatorRune); break;
                case '\\':
                    if (++index == pattern.Length) { throw InvalidPattern(); }
                    result.Append(Escaped(pattern, ref index));
                    break;
                case '[':
                    result.Append(CharacterClass(pattern, ref index));
                    break;
                default: result.Append(Regex.Escape(value.ToString())); break;
            }
        }
        result.Append("\\z");
        try { return new Regex(result.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException) { throw InvalidPattern(); }
    }

    private static string CharacterClass(string pattern, ref int index)
    {
        var position = index + 1;
        var negate = position < pattern.Length && pattern[position] == '^';
        if (negate) { position++; }
        var ranges = new List<string>();
        while (position < pattern.Length && pattern[position] != ']')
        {
            if (pattern.AsSpan(position).StartsWith("[:", StringComparison.Ordinal))
            {
                var end = pattern.IndexOf(":]", position + 2, StringComparison.Ordinal);
                if (end < 0) { throw InvalidPattern(); }
                ranges.Add(AsciiClass(pattern[(position + 2)..end]));
                position = end + 2;
                continue;
            }
            if (pattern[position] == '\\' && position + 1 < pattern.Length && "dDsSwW".Contains(pattern[position + 1]))
            {
                ranges.Add(Shorthand(pattern[position + 1]));
                position += 2;
                if (position < pattern.Length && pattern[position] == '-') { throw InvalidPattern(); }
                continue;
            }
            var low = ClassRune(pattern, ref position);
            var high = low;
            if (position < pattern.Length && pattern[position] == '-')
            {
                position++;
                high = ClassRune(pattern, ref position);
            }
            if (high < low) { throw InvalidPattern(); }
            AddRuneRange(ranges, low, high);
        }
        if (ranges.Count == 0 || position >= pattern.Length) { throw InvalidPattern(); }
        index = position;
        var included = "(?:" + string.Join('|', ranges) + ")";
        return negate ? "(?!" + included + ")" + AnyRune : included;
    }

    private static int ClassRune(string pattern, ref int position)
    {
        if (position >= pattern.Length || pattern[position] is '-' or ']') { throw InvalidPattern(); }
        if (pattern[position] == '\\')
        {
            if (++position >= pattern.Length) { throw InvalidPattern(); }
            var control = Control(pattern[position]);
            if (control is not null) { position++; return control.Value; }
            if (char.IsAsciiLetter(pattern[position])) { throw InvalidPattern(); }
        }
        var rune = Rune.GetRuneAt(pattern, position);
        position += rune.Utf16SequenceLength;
        return rune.Value;
    }

    private static string Escaped(string pattern, ref int position)
    {
        var value = pattern[position];
        if ("dDsSwW".Contains(value)) { return Shorthand(value); }
        var control = Control(value);
        if (control is not null) { return "\\u" + control.Value.ToString("X4"); }
        if (value is 'A' or 'z') { return "\\" + value; }
        const string word = "[A-Za-z0-9_]";
        if (value == 'b') { return "(?:(?<!" + word + ")(?=" + word + ")|(?<=" + word + ")(?!" + word + "))"; }
        if (value == 'B') { return "(?:(?<!" + word + ")(?!" + word + ")|(?<=" + word + ")(?=" + word + "))"; }
        if (value == 'x')
        {
            if (position + 2 >= pattern.Length || !Uri.IsHexDigit(pattern[position + 1]) || !Uri.IsHexDigit(pattern[position + 2])) { throw InvalidPattern(); }
            var hex = pattern.Substring(position + 1, 2);
            position += 2;
            return "\\u00" + hex;
        }
        if (char.IsAsciiLetter(value)) { throw InvalidPattern(); }
        return Regex.Escape(value.ToString());
    }

    private static int? Control(char value) => value switch
    {
        'a' => 7,
        'f' => 12,
        't' => 9,
        'n' => 10,
        'r' => 13,
        'v' => 11,
        _ => null
    };

    private static string Shorthand(char value) => value switch
    {
        'd' => AsciiClass("digit"),
        'D' => AsciiClass("^digit"),
        's' => "[\\u0009\\u000A\\u000C\\u000D ]",
        'S' => "(?![\\u0009\\u000A\\u000C\\u000D ])" + AnyRune,
        'w' => AsciiClass("word"),
        'W' => AsciiClass("^word"),
        _ => throw InvalidPattern()
    };

    private static string AsciiClass(string name)
    {
        var negate = name.StartsWith('^');
        if (negate) { name = name[1..]; }
        var ranges = name switch
        {
            "alnum" => new[] { (48, 57), (65, 90), (97, 122) },
            "alpha" => [(65, 90), (97, 122)],
            "ascii" => [(0, 127)],
            "blank" => [(9, 9), (32, 32)],
            "cntrl" => [(0, 31), (127, 127)],
            "digit" => [(48, 57)],
            "graph" => [(33, 126)],
            "lower" => [(97, 122)],
            "print" => [(32, 126)],
            "punct" => [(33, 47), (58, 64), (91, 96), (123, 126)],
            "space" => [(9, 13), (32, 32)],
            "upper" => [(65, 90)],
            "word" => [(48, 57), (65, 90), (95, 95), (97, 122)],
            "xdigit" => [(48, 57), (65, 70), (97, 102)],
            _ => throw InvalidPattern()
        };
        var expressions = new List<string>();
        foreach (var (low, high) in ranges) { AddRuneRange(expressions, low, high); }
        var included = "(?:" + string.Join('|', expressions) + ")";
        return negate ? "(?!" + included + ")" + AnyRune : included;
    }

    private static void AddRuneRange(List<string> ranges, int low, int high)
    {
        static string Characters(int first, int last) => first == last
            ? "\\u" + first.ToString("X4")
            : "[\\u" + first.ToString("X4") + "-\\u" + last.ToString("X4") + "]";
        if (low <= 0xffff)
        {
            ranges.Add(Characters(low, Math.Min(high, 0xffff)));
            low = 0x10000;
        }
        if (high < low) { return; }
        var firstHigh = 0xd800 + ((low - 0x10000) >> 10);
        var lastHigh = 0xd800 + ((high - 0x10000) >> 10);
        var firstLow = 0xdc00 + ((low - 0x10000) & 0x3ff);
        var lastLow = 0xdc00 + ((high - 0x10000) & 0x3ff);
        if (firstHigh == lastHigh) { ranges.Add(Characters(firstHigh, firstHigh) + Characters(firstLow, lastLow)); return; }
        ranges.Add(Characters(firstHigh, firstHigh) + Characters(firstLow, 0xdfff));
        if (lastHigh > firstHigh + 1) { ranges.Add(Characters(firstHigh + 1, lastHigh - 1) + Characters(0xdc00, 0xdfff)); }
        ranges.Add(Characters(lastHigh, lastHigh) + Characters(0xdc00, lastLow));
    }

    private static CliCommandException InvalidPattern() => new(CliExitCodes.ValidationError, "Invalid Docker ignore pattern.");
}
