using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Viltkamera.Core.IO;

/// <summary>
/// Read-only stream that opens the file with FILE_FLAG_NO_BUFFERING, so verification reads
/// the disk rather than the copy of the data still sitting in the Windows cache.
/// Unbuffered I/O requires sector-aligned offsets, lengths and buffer addresses; the buffer
/// is carved out of a pinned array at a 4 KiB boundary.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class UnbufferedReadStream : Stream
{
    private const FileOptions NoBuffering = (FileOptions)0x20000000;
    private const int Alignment = 4096;
    private const int BufferSize = 1 << 20;

    private readonly SafeFileHandle _handle;
    private readonly byte[] _pinned;
    private readonly int _alignedOffset;
    private long _fileOffset;
    private int _bufferPos;
    private int _bufferLen;

    public UnbufferedReadStream(string path)
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            NoBuffering | FileOptions.SequentialScan);
        _pinned = GC.AllocateUninitializedArray<byte>(BufferSize + Alignment, pinned: true);
        var address = Marshal.UnsafeAddrOfPinnedArrayElement(_pinned, 0);
        _alignedOffset = (int)((Alignment - (address % Alignment)) % Alignment);
    }

    private Span<byte> Buffer => _pinned.AsSpan(_alignedOffset, BufferSize);

    public override int Read(Span<byte> destination)
    {
        if (_bufferPos == _bufferLen)
        {
            // Offsets stay aligned: every read except the last returns a full buffer.
            _bufferLen = RandomAccess.Read(_handle, Buffer, _fileOffset);
            _fileOffset += _bufferLen;
            _bufferPos = 0;
            if (_bufferLen == 0) return 0;
        }

        var count = Math.Min(destination.Length, _bufferLen - _bufferPos);
        Buffer.Slice(_bufferPos, count).CopyTo(destination);
        _bufferPos += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    protected override void Dispose(bool disposing)
    {
        if (disposing) _handle.Dispose();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
