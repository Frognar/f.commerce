using System.Globalization;
using System.Text;

namespace FCommerce.Search.Core.Analysis;

/// <summary>
/// Case and diacritic folding: lowercase, combining marks removed (ż → z) and letters that are not decomposable
/// mapped to what people type without a Polish/European keyboard (ł → l, ø → o, ß → ss). Indexed terms and query
/// terms are always folded, so "Łożysko", "lozysko" and "LOŻYSKO" are the same term.
/// </summary>
public static class Folding
{
    public static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            switch (char.ToLowerInvariant(c))
            {
                case 'ł': builder.Append('l'); break;
                case 'ø': builder.Append('o'); break;
                case 'đ': builder.Append('d'); break;
                case 'ß': builder.Append("ss"); break;
                case 'æ': builder.Append("ae"); break;
                case 'œ': builder.Append("oe"); break;
                case 'ı': builder.Append('i'); break;
                case var lower: builder.Append(lower); break;
            }
        }

        return builder.ToString();
    }
}
