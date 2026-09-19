using System.Security.Cryptography;

namespace EasyImageImporter.Core.IO;

public enum CopyStatus { Verified, Mismatch }

public readonly record struct CopyResult(CopyStatus Status, string SourceHash, long BytesCopied);

/// <summary>
/// Copies one file and proves the copy is byte-identical: the source is hashed while it is
/// read (so the card is only read once), the destination is flushed to disk, then read back
/// from the device and hashed again.
/// </summary>
public sealed class VerifiedCopier(IFileSystem fs)
{
    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/>, which must not exist.
    /// On mismatch the destination is deleted. IO exceptions propagate; the caller decides
    /// whether they mean "card pulled" or "disk full".
    /// </summary>
    public CopyResult Copy(string source, string destination, CancellationToken ct = default)
    {
        string sourceHash;
        long bytes = 0;
        var created = false;
        try
        {
            using (var input = fs.OpenRead(source))
            using (var output = fs.CreateNew(destination))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                created = true;
                var buffer = new byte[1 << 20];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                    bytes += read;
                }

                if (output is FileStream file) file.Flush(flushToDisk: true);
                else output.Flush();
                sourceHash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }

            var destinationHash = HashForVerify(destination, ct);
            if (destinationHash == sourceHash)
                return new CopyResult(CopyStatus.Verified, sourceHash, bytes);
        }
        catch
        {
            // Only clean up a file we created; CreateNew fails on an existing file that isn't ours.
            if (created) TryDelete(destination);
            throw;
        }

        TryDelete(destination);
        return new CopyResult(CopyStatus.Mismatch, sourceHash, bytes);
    }

    /// <summary>SHA-256 of the file as stored on the device (bypassing the OS cache where possible).</summary>
    public string HashForVerify(string path, CancellationToken ct = default)
    {
        using var stream = fs.OpenReadForVerify(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, read);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private void TryDelete(string path)
    {
        try
        {
            if (fs.FileExists(path)) fs.Delete(path);
        }
        catch (IOException)
        {
            // Leftover .tmp files are cleaned up by recovery on next start.
        }
    }
}
