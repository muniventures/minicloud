using System.Text;
using System.Text.RegularExpressions;

namespace Minicloud.Cli.Rendering;

public static partial class TerminalTextHelper
{
    [GeneratedRegex(@"\x1b\[[0-9;?]*[a-zA-Z]|\x1b\]8;;[^\x1b]*\x1b\\|\x1b\]8;;\x1b\\")]
    private static partial Regex AnsiEscapeRegex();

    public static string StripControlCharacters(string value) =>
        new(value.Where(c => !char.IsControl(c) || c is '\t').ToArray());

    public static string StripAnsi(string value) =>
        AnsiEscapeRegex().Replace(value, "");

    public static int VisualWidth(string value) =>
        StripAnsi(value).Length;

    public static string Truncate(string text, int maxWidth)
    {
        if (maxWidth <= 0) return "";
        if (VisualWidth(text) <= maxWidth) return text;
        if (maxWidth <= 3) return new string('.', maxWidth);

        var plain = StripAnsi(text);
        return plain[..(maxWidth - 3)] + "...";
    }

    public static IReadOnlyList<string> Wrap(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [""];
        }

        if (maxWidth <= 0)
        {
            return [text];
        }

        var result = new List<string>();
        var logicalLines = text.Replace("\r\n", "\n").Split('\n');

        foreach (var logicalLine in logicalLines)
        {
            if (VisualWidth(logicalLine) <= maxWidth)
            {
                result.Add(logicalLine);
                continue;
            }

            var words = logicalLine.Split(' ');
            var currentLine = new StringBuilder();

            foreach (var word in words)
            {
                var wordWidth = VisualWidth(word);

                if (currentLine.Length == 0)
                {
                    if (wordWidth <= maxWidth)
                    {
                        currentLine.Append(word);
                    }
                    else
                    {
                        // Word exceeds entire maxWidth, break it by characters
                        var offset = 0;
                        while (offset < word.Length)
                        {
                            var chunkLength = Math.Min(maxWidth, word.Length - offset);
                            result.Add(word.Substring(offset, chunkLength));
                            offset += chunkLength;
                        }
                    }
                    continue;
                }

                if (VisualWidth(currentLine.ToString()) + 1 + wordWidth <= maxWidth)
                {
                    currentLine.Append(' ').Append(word);
                }
                else
                {
                    result.Add(currentLine.ToString());
                    currentLine.Clear();

                    if (wordWidth <= maxWidth)
                    {
                        currentLine.Append(word);
                    }
                    else
                    {
                        var offset = 0;
                        while (offset < word.Length)
                        {
                            var chunkLength = Math.Min(maxWidth, word.Length - offset);
                            var chunk = word.Substring(offset, chunkLength);
                            if (offset + chunkLength >= word.Length)
                            {
                                currentLine.Append(chunk);
                            }
                            else
                            {
                                result.Add(chunk);
                            }
                            offset += chunkLength;
                        }
                    }
                }
            }

            if (currentLine.Length > 0)
            {
                result.Add(currentLine.ToString());
            }
        }

        return result;
    }

    public static string FormatTerminalLink(string url, string text, bool supportsAnsi)
    {
        if (!supportsAnsi || string.IsNullOrWhiteSpace(url))
        {
            return text;
        }

        var safeUrl = StripControlCharacters(url);
        var safeText = StripControlCharacters(text);
        return $"\x1b]8;;{safeUrl}\x1b\\{safeText}\x1b]8;;\x1b\\";
    }

    public static string FormatByteCount(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{(bytes / (1024.0 * 1024.0)):F1} MB";
        return $"{(bytes / (1024.0 * 1024.0 * 1024.0)):F1} GB";
    }

    public static IReadOnlyList<string> FormatBox(
        IReadOnlyList<string> rawContentLines,
        bool isSuccess,
        int terminalWidth,
        bool isInteractive,
        bool supportsAnsi,
        bool noColor)
    {
        var sanitizedLines = rawContentLines
            .Select(StripControlCharacters)
            .ToList();

        if (!isInteractive || !supportsAnsi)
        {
            // Plain-text delimited format
            var maxContentWidth = sanitizedLines.Count > 0 ? sanitizedLines.Max(VisualWidth) : 40;
            var delimiterLen = Math.Min(terminalWidth, Math.Max(40, maxContentWidth));
            var delimiter = new string('-', delimiterLen);

            var plainBox = new List<string> { delimiter };
            plainBox.AddRange(sanitizedLines);
            plainBox.Add(delimiter);
            return plainBox;
        }

        // Interactive bordered box
        // Content-sized: inner width fits the content up to terminalWidth - 4
        var maxAllowedInnerWidth = Math.Max(20, terminalWidth - 4);
        var wrappedLines = new List<string>();
        foreach (var line in sanitizedLines)
        {
            var wrapped = Wrap(line, maxAllowedInnerWidth);
            wrappedLines.AddRange(wrapped);
        }

        var contentWidth = wrappedLines.Count > 0 ? wrappedLines.Max(VisualWidth) : 20;
        var innerWidth = Math.Min(contentWidth, maxAllowedInnerWidth);
        // Ensure at least 30 cols unless terminal is smaller
        innerWidth = Math.Max(innerWidth, Math.Min(30, maxAllowedInnerWidth));

        var useColor = supportsAnsi && !noColor;
        var borderColor = useColor
            ? (isSuccess ? "\x1b[32m" : "\x1b[31m")
            : "";
        var reset = useColor ? "\x1b[0m" : "";

        var lines = new List<string>();
        var topBorder = $"{borderColor}┌{new string('─', innerWidth + 2)}┐{reset}";
        var bottomBorder = $"{borderColor}└{new string('─', innerWidth + 2)}┘{reset}";

        lines.Add(topBorder);
        foreach (var line in wrappedLines)
        {
            var vWidth = VisualWidth(line);
            var padding = Math.Max(0, innerWidth - vWidth);
            lines.Add($"{borderColor}│{reset} {line}{new string(' ', padding)} {borderColor}│{reset}");
        }
        lines.Add(bottomBorder);

        return lines;
    }
}
