using System.Diagnostics;
using System.Text;

namespace EasyImageImporter.Core.Metadata;

/// <summary>What Windows Explorer shows as Title, Comments and Tags (and searches).</summary>
public sealed record PhotoMetadata(string Title, string? Description, IReadOnlyList<string> Keywords);

/// <summary>
/// Writes metadata with ExifTool, kept running in "-stay_open" mode so a thousand photos don't cost
/// a thousand process starts on an old PC.
///
/// A photo is never edited in place: ExifTool writes a new file next to it, and that file only
/// replaces the photo if its image data is bit-for-bit the same as before. After the card is
/// erased the archive holds the only copy, so tagging must never be able to damage it.
/// </summary>
public sealed class ExifTool : IDisposable
{
    private const string Ready = "{ready}";

    /// <summary>A command that takes longer than this is treated as ExifTool having hung.</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private readonly Process _process;
    private readonly Lock _lock = new();
    private bool _broken;

    public ExifTool(string executable)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-stay_open", "True", "-@", "-" }) start.ArgumentList.Add(arg);
        _process = Process.Start(start) ?? throw new InvalidOperationException("ExifTool did not start.");
        _process.ErrorDataReceived += (_, _) => { }; // drain, so a chatty stderr can never block ExifTool
        _process.BeginErrorReadLine();
    }

    /// <summary>
    /// Finds ExifTool: EASYIMAGEIMPORTER_EXIFTOOL, then the copy bundled next to the app, then the
    /// usual install locations and PATH. Null if there is none; saving then simply skips tagging.
    /// </summary>
    public static string? Locate()
    {
        var exe = OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool";
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("EASYIMAGEIMPORTER_EXIFTOOL"),
            Path.Combine(AppContext.BaseDirectory, "exiftool", exe),
            "/opt/homebrew/bin/exiftool",
            "/usr/local/bin/exiftool",
            "/usr/bin/exiftool",
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(dir => Path.Combine(dir, exe)));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Writes the metadata. False (and the photo untouched) if it couldn't be done safely.</summary>
    public bool Write(string path, PhotoMetadata metadata)
    {
        var temp = path + ".exiftool-tmp";
        if (File.Exists(temp)) File.Delete(temp);

        var before = ImageDataHash(path);
        if (before is null) return false;

        // No "-q": it also suppresses the "{ready}" line that ends each command in stay_open mode.
        var args = new List<string> { "-charset", "filename=utf8", "-o", temp };
        args.Add($"-XMP-dc:Title={metadata.Title}");
        args.Add($"-XPTitle={metadata.Title}");
        args.Add($"-XMP-dc:Description={metadata.Description ?? ""}");
        args.Add($"-XPComment={metadata.Description ?? ""}");
        var keywords = metadata.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        args.Add($"-XPKeywords={string.Join(";", keywords)}");
        args.Add("-XMP-dc:Subject=");
        args.AddRange(keywords.Select(k => $"-XMP-dc:Subject={k}"));
        args.Add(path);
        Run(args);

        if (!File.Exists(temp)) return false;
        if (ImageDataHash(temp) != before)
        {
            File.Delete(temp);
            return false;
        }
        File.Move(temp, path, overwrite: true);
        return true;
    }

    /// <summary>SHA-256 of just the image data (not the metadata), as ExifTool computes it.</summary>
    public string? ImageDataHash(string path)
    {
        var output = Run(["-charset", "filename=utf8", "-api", "ImageHashType=SHA256", "-s3", "-ImageDataHash", path]).Trim();
        return output.Length == 64 ? output : null;
    }

    /// <summary>Reads tags back, as "Tag: value" lines (tests and diagnostics).</summary>
    public string Read(string path, params string[] tags) =>
        Run(["-charset", "filename=utf8", "-s", .. tags.Select(t => "-" + t), path]);

    /// <summary>
    /// Runs one command. If ExifTool stops answering, it is killed and every later call returns
    /// nothing, so saving carries on without tags instead of hanging.
    /// </summary>
    private string Run(IEnumerable<string> args)
    {
        lock (_lock)
        {
            if (_broken) return "";
            var input = _process.StandardInput;
            foreach (var arg in args)
            {
                // One argument per line; a newline inside a value would split it.
                input.Write(arg.Replace('\n', ' ').Replace('\r', ' '));
                input.Write('\n');
            }
            input.Write("-execute\n");
            input.Flush();

            var output = new StringBuilder();
            using var timeout = new CancellationTokenSource(CommandTimeout);
            try
            {
                while (_process.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult() is { } line
                       && line.Trim() != Ready)
                    output.AppendLine(line);
            }
            catch (OperationCanceledException)
            {
                _broken = true;
                try { _process.Kill(); } catch (InvalidOperationException) { }
                return "";
            }
            return output.ToString();
        }
    }

    public void Dispose()
    {
        try
        {
            _process.StandardInput.Write("-stay_open\nFalse\n");
            _process.StandardInput.Flush();
            if (!_process.WaitForExit(3000)) _process.Kill();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Already gone.
        }
        _process.Dispose();
    }
}
