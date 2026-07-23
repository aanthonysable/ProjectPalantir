using System.Text.RegularExpressions;

namespace Palantir.Application.Ai;

/// <summary>
/// Normalize model prose for UI: strip markdown headings, keep light emphasis for rich rendering.
/// </summary>
public static class AiTextSanitizer
{
    public static string SanitizeProse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var cleaned = text.Replace("\r\n", "\n");

        // "# Heading" → plain label (UI treats short lines as section labels)
        cleaned = Regex.Replace(
            cleaned,
            @"^\s*#{1,6}\s+",
            string.Empty,
            RegexOptions.Multiline);

        // Normalize "* " bullets to "- " (UI renders these as disc bullets)
        cleaned = Regex.Replace(cleaned, @"^(\s*)\*\s+", "$1- ", RegexOptions.Multiline);

        // Leave **bold** / *italic* intact — the web client renders them as rich text.
        // Strip only unpaired leftover ** markers that would show as raw junk.
        cleaned = Regex.Replace(cleaned, @"\*{3,}", string.Empty);

        var lines = cleaned.Split('\n').Select(static line => line.TrimEnd());
        return string.Join('\n', lines).Trim();
    }
}
