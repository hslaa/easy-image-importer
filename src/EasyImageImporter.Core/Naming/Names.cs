using System.Globalization;
using System.Text;

namespace EasyImageImporter.Core.Naming;

/// <summary>
/// Folder and file names: readable, predictable, and valid on Windows. Everything is normalised to
/// Unicode NFC, because macOS hands out decomposed æøå that would otherwise not match on Windows.
/// </summary>
public static class Names
{
    private static readonly CultureInfo Norwegian = CultureInfo.GetCultureInfo("nb-NO");
    private static readonly char[] Invalid = "<>:\"/\\|?*".ToCharArray();
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Makes text safe as (part of) a Windows file or folder name. Empty if nothing usable is left.</summary>
    public static string Clean(string? text, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormC))
            sb.Append(char.IsControl(c) || Invalid.Contains(c) ? ' ' : c);
        var cleaned = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length > maxLength) cleaned = cleaned[..maxLength];
        cleaned = cleaned.TrimEnd('.', ' ');
        return Reserved.Contains(cleaned) ? cleaned + "_" : cleaned;
    }

    /// <summary>"Juni 2026", "Juni–Juli 2026", "Desember 2025–Januar 2026"; empty for a reset camera clock.</summary>
    public static string Period(DateTime start, DateTime end)
    {
        if (start.Year < 2010) return "";
        string Month(DateTime d) => Capitalise(d.ToString("MMMM", Norwegian));
        if (start.Year == end.Year && start.Month == end.Month) return $"{Month(start)} {start.Year}";
        if (start.Year == end.Year) return $"{Month(start)}–{Month(end)} {end.Year}";
        return $"{Month(start)} {start.Year}–{Month(end)} {end.Year}";
    }

    /// <summary>
    /// The suggested folder name for a place: "Høgfjellåsen Juni 2026 – Kongeørn, Ravn".
    /// At most three animals, most photographed first, so the name stays readable.
    /// </summary>
    public static string FolderSuggestion(string place, DateTime start, DateTime end, IEnumerable<string> animals)
    {
        var name = string.Join(' ', new[] { Clean(place, 50), Period(start, end) }.Where(s => s.Length > 0));
        var top = animals.Select(a => Clean(a, 30)).Where(a => a.Length > 0).Take(3).ToList();
        if (top.Count > 0) name += " – " + string.Join(", ", top);
        return Clean(name, 120);
    }

    /// <summary>"2026-09-18_Høgfjellåsen_0712_Nøtteskrike_001.jpg": date, place, visit start, animal, counter.</summary>
    public static string FileName(DateTime visitStart, string place, string? animal, int counter, string extension)
    {
        var parts = new List<string>();
        if (visitStart.Year >= 2010) parts.Add(visitStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        parts.Add(Clean(place, 40));
        parts.Add(visitStart.ToString("HHmm", CultureInfo.InvariantCulture));
        if (Clean(animal, 30) is { Length: > 0 } a) parts.Add(a);
        parts.Add(counter.ToString("000", CultureInfo.InvariantCulture));
        return string.Join('_', parts.Where(p => p.Length > 0)) + extension.ToLowerInvariant();
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Norwegian) + s[1..];
}
