namespace EasyImageImporter.Core.Import;

/// <summary>
/// What the user can change. Kept in the database next to everything else, so it survives an
/// update. Both settings only affect what happens next: photos already saved stay where they are.
/// </summary>
public sealed class AppSettings(ImportStore store)
{
    private const string ArchiveKey = "archive_root", BinKey = "discarded_to_recycle_bin";

    /// <summary>Where new imports are saved, or null for the default (Pictures\Viltkamera).</summary>
    public string? ArchiveRoot
    {
        get => store.GetSetting(ArchiveKey);
        set => store.SetSetting(ArchiveKey, string.IsNullOrWhiteSpace(value) ? null : value);
    }

    /// <summary>
    /// Whether photos the user sorted away go to the recycle bin once the import can no longer be
    /// undone. On by default: they are still there to be restored, and the system clears the bin
    /// in its own time, which keeps a PC short of space from filling up. Off keeps them for good
    /// in the "Sortert bort" folder.
    /// </summary>
    public bool DiscardedToRecycleBin
    {
        get => store.GetSetting(BinKey) != "0";
        set => store.SetSetting(BinKey, value ? "1" : "0");
    }
}
