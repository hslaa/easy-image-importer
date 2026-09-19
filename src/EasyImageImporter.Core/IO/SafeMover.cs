namespace EasyImageImporter.Core.IO;

/// <summary>
/// Moves one file so that at every moment at least one complete copy exists: a rename on the
/// same disk, otherwise copy → verify → rename into place → delete the original.
/// Safe to call again after a crash.
/// </summary>
public sealed class SafeMover(IFileSystem fs)
{
    private readonly VerifiedCopier _copier = new(fs);

    public void Move(string from, string to)
    {
        // Already moved before a crash, but not yet recorded.
        if (!fs.FileExists(from) && fs.FileExists(to)) return;

        var targetDir = Path.GetDirectoryName(to)!;
        fs.CreateDirectory(targetDir);

        if (fs.IsSameVolume(from, targetDir))
        {
            fs.Move(from, to);
            return;
        }

        // Crashed after the copy was in place but before the original was removed.
        if (fs.FileExists(to))
        {
            if (_copier.HashForVerify(to) != _copier.HashForVerify(from))
                throw new IOException($"{Path.GetFileName(to)} finnes allerede og er ikke lik originalen.");
            fs.Delete(from);
            return;
        }

        var temp = to + ".tmp";
        if (fs.FileExists(temp)) fs.Delete(temp);
        var result = _copier.Copy(from, temp);
        if (result.Status != CopyStatus.Verified)
            throw new IOException($"Kopien av {Path.GetFileName(to)} kunne ikke kontrolleres.");
        fs.Move(temp, to);
        fs.Delete(from);
    }
}
