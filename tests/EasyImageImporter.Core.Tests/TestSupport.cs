using System.Text;
using EasyImageImporter.Core.Import;
using EasyImageImporter.Core.IO;
using EasyImageImporter.Core.Storage;

namespace EasyImageImporter.Core.Tests;

/// <summary>Real files in a temp folder, with a fault-injecting file system on top.</summary>
public sealed class TestEnv : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "easyimageimporter-tests", Guid.NewGuid().ToString("N"));
    public string Card => Path.Combine(Root, "card");
    public AppPaths Paths { get; }
    public FaultyFileSystem Fs { get; } = new();
    public ImportStore Store { get; }
    public FakeTime Time { get; } = new(new DateTimeOffset(2026, 9, 19, 10, 15, 0, TimeSpan.Zero));

    private readonly Random _random = new(42);

    public TestEnv()
    {
        Paths = new AppPaths(Path.Combine(Root, "data"), Path.Combine(Root, "Pictures", "Viltkamera"));
        Directory.CreateDirectory(Card);
        Directory.CreateDirectory(Paths.DataRoot);
        Store = new ImportStore(new Database(Paths.DatabasePath), Time);
    }

    public CardScanner Scanner => new(Fs, Store);
    public CopyEngine Copier => new(Fs, Store, Paths) { RetryDelay = TimeSpan.Zero };
    public Finalizer Finalizer => new(Fs, Store, Paths, Time);
    public CardEraser Eraser => new(Fs, Store);
    public ImportUndo Undo => new(Fs, Store, Paths, Time);
    public Recovery Recovery => new(Store, Finalizer, Undo);

    /// <summary>Writes a random "image" to the card and returns its bytes.</summary>
    public byte[] AddCardFile(string relPath, int size = 50_000)
    {
        var bytes = new byte[size];
        _random.NextBytes(bytes);
        var path = Path.Combine(Card, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return bytes;
    }

    public void AddCardImages(int count, string folder = "DCIM/100MEDIA")
    {
        for (var i = 1; i <= count; i++) AddCardFile($"{folder}/IMAG{i:0000}.JPG");
    }

    public Session OpenSession() => Scanner.OpenSession(Scanner.Scan(Card), "SDCARD")!;

    public void PullCard() => Fs.PulledCard = Card;
    public void ReinsertCard() => Fs.PulledCard = null;

    /// <summary>Copy → finalize, asserting each step completes.</summary>
    public (Session Session, ImportRecord? Import) ImportCard()
    {
        var session = OpenSession();
        Assert.Equal(CopyOutcomeKind.Completed, Copier.Run(session.Id).Kind);
        var import = Finalizer.Run(session.Id);
        return (Store.GetSession(session.Id), import);
    }

    public IEnumerable<string> ArchivedFiles() =>
        Directory.Exists(Paths.ArchiveRoot)
            ? Directory.EnumerateFiles(Paths.ArchiveRoot, "*", SearchOption.AllDirectories)
            : [];

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
    }
}

public sealed class FakeTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

/// <summary>
/// The real file system, plus switches for the failures the spec says we must survive.
/// </summary>
public sealed class FaultyFileSystem : IFileSystem
{
    private readonly PhysicalFileSystem _inner = new();

    /// <summary>Called with the number of bytes read so far from a card file. Throw to simulate a failure.</summary>
    public Action<string, long>? OnSourceRead { get; set; }

    /// <summary>How many upcoming writes to corrupt (one flipped byte each).</summary>
    public int CorruptNextWrites { get; set; }

    /// <summary>Throw a disk-full error on the next N writes.</summary>
    public int DiskFullOnNextWrites { get; set; }

    /// <summary>Throw (simulated crash) when the Nth move from now happens. 0 = off.</summary>
    public int CrashOnMove { get; set; }

    public long? FreeSpaceOverride { get; set; }

    /// <summary>Everything under this folder behaves as if the card had been pulled out.</summary>
    public string? PulledCard { get; set; }

    private bool IsPulled(string path) =>
        PulledCard is not null && path.StartsWith(PulledCard, StringComparison.Ordinal);

    private void ThrowIfPulled(string path)
    {
        if (IsPulled(path)) throw new IOException("The device is not ready.");
    }

    public IEnumerable<string> EnumerateFiles(string root)
    {
        ThrowIfPulled(root);
        return _inner.EnumerateFiles(root);
    }

    public bool FileExists(string path) => !IsPulled(path) && _inner.FileExists(path);
    public bool DirectoryExists(string path) => !IsPulled(path) && _inner.DirectoryExists(path);
    public void CreateDirectory(string path) => _inner.CreateDirectory(path);
    public FileMeta GetFileMeta(string path) => _inner.GetFileMeta(path);

    public Stream OpenReadForVerify(string path)
    {
        ThrowIfPulled(path);
        return _inner.OpenReadForVerify(path);
    }

    public void Delete(string path)
    {
        ThrowIfPulled(path);
        _inner.Delete(path);
    }
    public bool DeleteDirectoryIfEmpty(string path) => _inner.DeleteDirectoryIfEmpty(path);
    public void WriteAllText(string path, string contents, Encoding encoding) => _inner.WriteAllText(path, contents, encoding);
    public long GetAvailableFreeSpace(string path) => FreeSpaceOverride ?? _inner.GetAvailableFreeSpace(path);
    public bool IsSameVolume(string pathA, string pathB) => _inner.IsSameVolume(pathA, pathB);

    public Stream OpenRead(string path)
    {
        ThrowIfPulled(path);
        return new HookedReadStream(_inner.OpenRead(path), bytes =>
        {
            OnSourceRead?.Invoke(path, bytes);
            ThrowIfPulled(path);
        });
    }

    public Stream CreateNew(string path)
    {
        var stream = _inner.CreateNew(path);
        if (DiskFullOnNextWrites > 0)
        {
            DiskFullOnNextWrites--;
            stream.Dispose();
            throw new IOException("There is not enough space on the disk.",
                OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28);
        }
        if (CorruptNextWrites > 0)
        {
            CorruptNextWrites--;
            return new CorruptingWriteStream(stream);
        }
        return stream;
    }

    public void Move(string from, string to)
    {
        if (CrashOnMove > 0 && --CrashOnMove == 0) throw new SimulatedCrashException();
        _inner.Move(from, to);
    }

    private sealed class HookedReadStream(Stream inner, Action<long> onRead) : Stream
    {
        private long _total;

        public override int Read(byte[] buffer, int offset, int count)
        {
            onRead(_total);
            var read = inner.Read(buffer, offset, Math.Min(count, 16_384));
            _total += read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => _total; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CorruptingWriteStream(Stream inner) : Stream
    {
        private bool _corrupted;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!_corrupted && count > 0)
            {
                var copy = buffer.AsSpan(offset, count).ToArray();
                copy[count / 2] ^= 0xFF;
                inner.Write(copy, 0, count);
                _corrupted = true;
                return;
            }
            inner.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

public sealed class SimulatedCrashException : Exception;
