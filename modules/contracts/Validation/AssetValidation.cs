// SPDX-License-Identifier: MPL-2.0

using System.Text.RegularExpressions;

namespace Minicloud.Common.Validation;

public static class AssetValidation
{
    public static bool SafeName(string? value) => value is not null && Regex.IsMatch(value, "\\A[a-z][a-z0-9-]{0,39}\\z", RegexOptions.CultureInvariant);
    public static bool SafeMountPath(string? value) => value is not null && value.StartsWith('/') && value.Length <= 200 &&
        value.Split('/').Skip(1).All(x => Regex.IsMatch(x, "\\A[A-Za-z0-9_.-]+\\z", RegexOptions.CultureInvariant) && x is not "." and not "..");
}
