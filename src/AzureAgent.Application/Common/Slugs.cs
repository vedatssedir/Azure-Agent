using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AzureAgent.Application.Common;

public static partial class Slugs
{
    public static string From(string value, int maxLength = 60)
    {
        var normalized = value.Trim().ToLowerInvariant().Replace('ı', 'i').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        }
        var slug = NonAlphanumeric().Replace(builder.ToString(), "-").Trim('-');
        if (slug.Length > maxLength) slug = slug[..maxLength].Trim('-');
        return slug.Length == 0 ? "item" : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
