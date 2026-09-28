namespace EasyImageImporter.Core.Import;

/// <summary>
/// What the user can change. Kept in the database next to everything else, so it survives an
/// update. Both settings only affect what happens next: photos already saved stay where they are.
/// </summary>
public sealed class AppSettings(ImportStore store)
{
    private const string ArchiveKey = "archive_root", KeepDaysKey = "discarded_keep_days",
        BinKey = "discarded_to_recycle_bin"; // replaced by KeepDaysKey; still read

    /// <summary>Where new imports are saved, or null for the default (Pictures\Viltkamera).</summary>
    public string? ArchiveRoot
    {
        get => store.GetSetting(ArchiveKey);
        set => store.SetSetting(ArchiveKey, string.IsNullOrWhiteSpace(value) ? null : value);
    }

    /// <summary>
    /// How many days photos the user sorted away are kept out of sight before they go to the
    /// recycle bin, or null to keep them for good. 30 by default: long enough to change one's mind,
    /// short enough that an older PC doesn't fill up. Never less than the import can be undone.
    /// </summary>
    public int? DiscardedKeepDays
    {
        get
        {
            if (store.GetSetting(KeepDaysKey) is { } days) return int.TryParse(days, out var n) && n > 0 ? n : null;
            // Before there was a choice of days, off meant "keep them for good".
            return store.GetSetting(BinKey) == "0" ? null : DefaultKeepDays;
        }
        set => store.SetSetting(KeepDaysKey, value is { } n ? Math.Max(n, 1).ToString() : "0");
    }

    public const int DefaultKeepDays = 30;
}
