// SPDX-License-Identifier: MPL-2.0

using System.Globalization;

namespace Minicloud.Common.Contracts;

/// <summary>
/// Customer-facing deployment identity <c>yyyyMMdd.N</c>: the UTC creation date and an unpadded
/// positive counter scoped to one organization and date. It is only meaningful together with its organization.
/// </summary>
public static class DeploymentNumber
{
    public const int MaxLength = 28;
    private const int DateLength = 8;
    private const int MaxCounterDigits = 19;

    public static string Format(DateOnly utcDate, long counter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(counter, 1);
        return utcDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "." + counter.ToString(CultureInfo.InvariantCulture);
    }

    public static bool TryParse(string? value, out DateOnly utcDate, out long counter)
    {
        utcDate = default;
        counter = 0;
        if (value is null || value.Length < DateLength + 2 || value.Length > MaxLength || value[DateLength] != '.')
        {
            return false;
        }

        var counterText = value.AsSpan(DateLength + 1);
        if (counterText.Length > MaxCounterDigits || counterText[0] == '0' ||
            !IsAsciiDigits(value.AsSpan(0, DateLength)) || !IsAsciiDigits(counterText))
        {
            return false;
        }

        return DateOnly.TryParseExact(value.AsSpan(0, DateLength), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out utcDate) &&
            long.TryParse(counterText, NumberStyles.None, CultureInfo.InvariantCulture, out counter) && counter >= 1;
    }

    public static bool IsValid(string? value) => TryParse(value, out _, out _);

    private static bool IsAsciiDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
