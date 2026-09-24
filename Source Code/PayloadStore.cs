using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RenderiteRecovery;

internal sealed unsafe class PayloadStore : IDisposable
{
    private const long DefaultSegmentSize = 256L * 1024 * 1024;
    private const double RelocateBelowLiveFraction = 0.25;
    private const long DefaultWasteCompactionThreshold = 512L * 1024 * 1024;
    private const double WasteCompactionLiveFraction = 0.5;
    private const long DefaultPendingLimit = 256L * 1024 * 1024;

    internal readonly record struct Key(ulong Hash, int Length);

    internal sealed class Handle
    {
        internal Segment? Segment;
        internal long Offset;
        internal nint Memory;
        internal LinkedListNode<Handle>? MemoryNode;
        internal readonly int Length;
        internal readonly Key Key;
        internal bool Registered;
        internal int References;
        internal bool WritePending;
        internal bool Writing;

        internal Handle(Key key, int length)
        {
            Key = key;
            Length = length;
        }

        internal bool InMemory => Memory != 0;
        internal Span<byte> MemorySpan => new((void*)Memory, Length);
    }

    internal sealed class Segment
    {
        internal readonly SafeFileHandle File;
        internal readonly string Path;
        internal readonly HashSet<Handle> Handles = new(ReferenceEqualityComparer.Instance);
        internal long Length;
        internal long LiveBytes;
        internal int Busy;
        internal bool Sealed;
        internal bool Relocating;

        internal Segment(SafeFileHandle file, string path)
        {
            File = file;
            Path = path;
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Key, Handle> _byHash = new();
    private readonly List<Segment> _segments = new();
    private readonly LinkedList<Handle> _inMemory = new();
    private readonly Queue<Handle> _pending = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly ManualResetEventSlim _idle = new(true);
    private readonly ManualResetEventSlim _drained = new(true);
    private readonly long _segmentSize;
    private readonly long _pendingLimit;
    private Thread? _writer;
    private long _memoryBytes;
    private long _pendingBytes;
    private string? _directory;
    private string? _warnedDirectory;
    private Segment _active;
    private int _segmentNumber;
    private bool _disposed;

    internal PayloadStore(long limit = 0, long segmentSize = DefaultSegmentSize,
        long wasteCompactionThreshold = DefaultWasteCompactionThreshold,
        long memoryLimit = 0, string? directory = null, long pendingLimit = DefaultPendingLimit)
    {
        Limit = limit;
        _segmentSize = segmentSize;
        _pendingLimit = Math.Max(1, pendingLimit);
        WasteCompactionThreshold = wasteCompactionThreshold;
        MemoryLimit = Math.Max(0, memoryLimit);
        _directory = string.IsNullOrWhiteSpace(directory) ? null : directory;
        _active = OpenSegment();
    }

    internal long Limit { get; set; }

    internal long WasteCompactionThreshold { get; set; }

    internal long MemoryLimit { get; private set; }

    internal Action<string>? WriteFailed { get; set; }

    internal Action<Handle>? BeforeDiskWrite { get; set; }

    internal string? Directory
    {
        get { lock (_gate) return _directory; }
        set
        {
            lock (_gate)
            {
                string? directory = string.IsNullOrWhiteSpace(value) ? null : value;
                if (directory == _directory)
                    return;

                _directory = directory;
                Segment previous = _active;
                try { _active = OpenSegment(); }
                catch (Exception ex)
                {
                    RenderiteRecoveryMod.Warn($"Could not open a new recovery archive segment: {ex.Message}");
                    return;
                }
                previous.Sealed = true;
                CloseIfEmpty(previous);
            }
        }
    }

    internal string ActiveDirectory
    {
        get { lock (_gate) return System.IO.Path.GetDirectoryName(_active.Path) ?? ""; }
    }

    internal long LiveBytes
    {
        get { lock (_gate) return _memoryBytes + _pendingBytes + _segments.Sum(segment => segment.LiveBytes); }
    }

    internal long MemoryBytes
    {
        get { lock (_gate) return _memoryBytes + _pendingBytes; }
    }

    internal long PendingBytes => Interlocked.Read(ref _pendingBytes);

    internal void SetMemoryLimit(long memoryLimit)
    {
        lock (_gate)
        {
            MemoryLimit = Math.Max(0, memoryLimit);
            try
            {
                while (_memoryBytes > MemoryLimit && _inMemory.First is { } node)
                {
                    CheckDiskLimit(node.Value.Length);
                    Spill(node.Value);
                }
            }
            catch (Exception ex)
            {
                RenderiteRecoveryMod.Warn($"Could not move recovery payloads from RAM to disk: {ex.Message}");
            }
        }
    }

    internal int SegmentCount
    {
        get { lock (_gate) return _segments.Count; }
    }

    internal long DiskBytes
    {
        get { lock (_gate) return DiskBytesLocked(); }
    }

    internal Handle Save(ReadOnlySpan<byte> bytes)
    {
        var key = new Key(PayloadHash.Compute(bytes), bytes.Length);
        WaitForBacklog();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _byHash.TryGetValue(key, out Handle? existing);
            if (existing is not null && existing.InMemory && existing.MemorySpan.SequenceEqual(bytes))
            {
                existing.References++;
                return existing;
            }
            var handle = new Handle(key, bytes.Length) { References = 1, Registered = existing is null };
            bool resident = bytes.Length <= MemoryLimit;
            CheckDiskLimit(resident ? SpilledBytes(MemoryLimit - bytes.Length) : bytes.Length);

            handle.Memory = (nint)NativeMemory.Alloc((nuint)Math.Max(1, bytes.Length));
            bytes.CopyTo(handle.MemorySpan);
            if (resident)
            {
                while (_memoryBytes > Math.Max(0, MemoryLimit - bytes.Length) && _inMemory.First is { } node)
                    Spill(node.Value);

                handle.MemoryNode = _inMemory.AddLast(handle);
                _memoryBytes += bytes.Length;
            }
            else
                Enqueue(handle);

            if (handle.Registered)
                _byHash.Add(key, handle);

            return handle;
        }
    }

    internal void Release(Handle handle)
    {
        Segment? relocate = null;
        lock (_gate)
        {
            if (--handle.References > 0)
                return;

            if (handle.Registered)
                _byHash.Remove(handle.Key);

            if (handle.Writing)
                return;

            if (handle.WritePending)
            {
                handle.WritePending = false;
                FreeMemory(handle);
                AddPending(-handle.Length);
                _signal.Set();
                return;
            }
            if (handle.MemoryNode is not null)
            {
                _inMemory.Remove(handle.MemoryNode);
                handle.MemoryNode = null;
                _memoryBytes -= handle.Length;
                FreeMemory(handle);
                return;
            }
            if (handle.Segment is not Segment segment)
                return;

            segment.Handles.Remove(handle);
            segment.LiveBytes -= handle.Length;
            handle.Segment = null;
            if (!segment.Sealed || CloseIfEmpty(segment))
                return;

            if (!segment.Relocating && segment.LiveBytes < segment.Length * RelocateBelowLiveFraction)
            {
                segment.Relocating = true;
                relocate = segment;
            }
        }
        if (relocate is not null)
            _ = Task.Run(() => Relocate(relocate));
    }

    internal void Load(Handle handle, Span<byte> destination)
    {
        if (destination.Length != handle.Length)
            throw new ArgumentException("Payload destination length does not match archive reference.");

        Segment segment;
        long offset;
        lock (_gate)
        {
            if (handle.References <= 0)
                throw new InvalidDataException("Recovery payload was already released.");

            if (handle.InMemory)
            {
                handle.MemorySpan.CopyTo(destination);
                return;
            }
            segment = handle.Segment ?? throw new InvalidDataException("Recovery payload has no stored copy.");
            offset = handle.Offset;
            segment.Busy++;
        }
        try { ReadExactly(segment.File, destination, offset); }
        finally
        {
            lock (_gate)
            {
                segment.Busy--;
                CloseIfEmpty(segment);
            }
        }
    }

    internal bool WaitForWrites(TimeSpan timeout) => _idle.Wait(timeout);

    private void WaitForBacklog()
    {
        while (Interlocked.Read(ref _pendingBytes) > _pendingLimit && !Volatile.Read(ref _disposed))
            _drained.Wait(100);
    }

    private long SpilledBytes(long target)
    {
        long memory = _memoryBytes, spilled = 0;
        for (LinkedListNode<Handle>? node = _inMemory.First; node is not null && memory > Math.Max(0, target); node = node.Next)
        {
            memory -= node.Value.Length;
            spilled += node.Value.Length;
        }
        return spilled;
    }

    private void Spill(Handle handle)
    {
        _inMemory.Remove(handle.MemoryNode!);
        handle.MemoryNode = null;
        _memoryBytes -= handle.Length;
        Enqueue(handle);
    }

    private void Enqueue(Handle handle)
    {
        handle.WritePending = true;
        _pending.Enqueue(handle);
        AddPending(handle.Length);
        _idle.Reset();
        if (_writer is null)
        {
            _writer = new Thread(RunWriter) { IsBackground = true, Name = "RenderiteRecovery archive writer" };
            _writer.Start();
        }
        _signal.Set();
    }

    private void AddPending(long bytes)
    {
        long pending = Interlocked.Add(ref _pendingBytes, bytes);
        if (pending > _pendingLimit)
            _drained.Reset();
        else
            _drained.Set();
    }

    private void RunWriter()
    {
        while (!Volatile.Read(ref _disposed))
        {
            _signal.WaitOne();
            try
            {
                while (WriteNext()) { }
            }
            catch (Exception ex)
            {
                RenderiteRecoveryMod.Warn($"The recovery archive writer stopped a pass: {ex.Message}");
            }
        }
    }

    private bool WriteNext()
    {
        Handle? handle = null;
        Segment segment;
        long offset;
        Exception? failure = null;
        lock (_gate)
        {
            if (_disposed)
                return false;

            while (_pending.TryDequeue(out Handle? next))
            {
                if (next.WritePending && next.InMemory)
                {
                    handle = next;
                    break;
                }
            }
            if (handle is null)
            {
                _idle.Set();
                return false;
            }
            try { segment = Reserve(handle.Length, out offset); }
            catch (Exception ex)
            {
                KeepInMemory(handle);
                failure = ex;
                segment = null!;
                offset = 0;
            }
            if (failure is null)
            {
                handle.Writing = true;
                segment.Busy++;
            }
        }
        if (failure is null)
        {
            try
            {
                BeforeDiskWrite?.Invoke(handle);
                RandomAccess.Write(segment.File, new ReadOnlySpan<byte>((void*)handle.Memory, handle.Length), offset);
            }
            catch (Exception ex) { failure = ex; }

            lock (_gate)
            {
                handle.Writing = false;
                segment.Busy--;
                if (handle.References <= 0)
                {
                    handle.WritePending = false;
                    FreeMemory(handle);
                    AddPending(-handle.Length);
                }
                else if (failure is null)
                {
                    handle.WritePending = false;
                    handle.Segment = segment;
                    handle.Offset = offset;
                    segment.Handles.Add(handle);
                    segment.LiveBytes += handle.Length;
                    FreeMemory(handle);
                    AddPending(-handle.Length);
                }
                else
                    KeepInMemory(handle);

                CloseIfEmpty(segment);
            }
        }
        if (failure is not null)
        {
            string message = $"Could not write a recovery payload to disk: {failure.Message}";
            RenderiteRecoveryMod.Warn(message);
            WriteFailed?.Invoke(message);
        }
        return true;
    }

    private void KeepInMemory(Handle handle)
    {
        handle.WritePending = false;
        AddPending(-handle.Length);
        handle.MemoryNode = _inMemory.AddLast(handle);
        _memoryBytes += handle.Length;
    }

    private Segment Reserve(int length, out long offset)
    {
        if (_active.Length > 0 && _active.Length + length > _segmentSize)
        {
            Segment previous = _active;
            _active = OpenSegment();
            previous.Sealed = true;
            if (!CloseIfEmpty(previous))
                CompactIfWasteful();
        }
        offset = _active.Length;
        _active.Length += length;
        return _active;
    }

    private void Relocate(Segment segment)
    {
        using var measure = ResourceMetrics.Measure(ResourceMetrics.Area.Background);
        try
        {
            while (true)
            {
                WaitForBacklog();
                Handle? handle;
                long offset;
                lock (_gate)
                {
                    if (_disposed)
                        return;

                    handle = segment.Handles.FirstOrDefault();
                    if (handle is null)
                    {
                        CloseIfEmpty(segment);
                        return;
                    }
                    offset = handle.Offset;
                    segment.Busy++;
                }
                nint memory = (nint)NativeMemory.Alloc((nuint)Math.Max(1, handle.Length));
                bool adopted = false;
                try
                {
                    ReadExactly(segment.File, new Span<byte>((void*)memory, handle.Length), offset);
                    lock (_gate)
                    {
                        if (handle.References > 0 && handle.Segment == segment && segment.Handles.Remove(handle))
                        {
                            segment.LiveBytes -= handle.Length;
                            handle.Segment = null;
                            handle.Memory = memory;
                            Enqueue(handle);
                            adopted = true;
                        }
                    }
                }
                finally
                {
                    if (!adopted)
                        NativeMemory.Free((void*)memory);

                    lock (_gate)
                    {
                        segment.Busy--;
                        CloseIfEmpty(segment);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate) segment.Relocating = false;
            RenderiteRecoveryMod.Warn($"Could not compact the recovery payload archive: {ex.Message}");
        }
    }

    private void CheckDiskLimit(long length)
    {
        long limit = Limit;
        if (limit > 0 && length > 0 && DiskBytesLocked() + Interlocked.Read(ref _pendingBytes) + length > limit)
            throw new IOException($"Recovery payload archive would exceed its {limit / (1024 * 1024)} MB limit (archive_disk_limit_mb).");
    }

    private long DiskBytesLocked() => _segments.Sum(segment => segment.Length);

    private void CompactIfWasteful()
    {
        long live = _segments.Sum(segment => segment.LiveBytes);
        long waste = DiskBytesLocked() - live;
        if (waste < WasteCompactionThreshold || waste < live * WasteCompactionLiveFraction)
            return;

        foreach (Segment segment in _segments)
        {
            if (!segment.Sealed || segment.Relocating
                || segment.LiveBytes >= segment.Length * WasteCompactionLiveFraction)
                continue;

            segment.Relocating = true;
            _ = Task.Run(() => Relocate(segment));
        }
    }

    private bool CloseIfEmpty(Segment segment)
    {
        if (!segment.Sealed || segment.Handles.Count > 0 || segment.Busy > 0 || !_segments.Remove(segment))
            return false;

        segment.File.Dispose();
        return true;
    }

    private static void FreeMemory(Handle handle)
    {
        NativeMemory.Free((void*)handle.Memory);
        handle.Memory = 0;
    }

    private static void ReadExactly(SafeFileHandle file, Span<byte> destination, long offset)
    {
        while (destination.Length > 0)
        {
            int read = RandomAccess.Read(file, destination, offset);
            if (read <= 0)
                throw new EndOfStreamException("The recovery archive segment ended early.");

            destination = destination[read..];
            offset += read;
        }
    }

    private Segment OpenSegment()
    {
        string name = $"RenderiteRecovery-{Environment.ProcessId}-{Guid.NewGuid():N}-{_segmentNumber++}.bin";
        Segment? segment = null;
        string? directory = _directory;
        if (directory is not null)
        {
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                segment = Create(System.IO.Path.Combine(directory, name));
                _warnedDirectory = null;
            }
            catch (Exception ex)
            {
                if (_warnedDirectory != directory)
                {
                    _warnedDirectory = directory;
                    RenderiteRecoveryMod.Warn($"Cannot use archive_directory \"{directory}\" ({ex.Message}), using the temp folder instead.");
                }
            }
        }
        segment ??= Create(System.IO.Path.Combine(System.IO.Path.GetTempPath(), name));
        _segments.Add(segment);
        return segment;

        static Segment Create(string path) => new(File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, FileOptions.DeleteOnClose | FileOptions.RandomAccess), path);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
        }
        _signal.Set();
        _drained.Set();
        Thread? writer = _writer;
        if (writer is not null && writer != Thread.CurrentThread)
            writer.Join(TimeSpan.FromSeconds(5));

        lock (_gate)
        {
            foreach (Handle handle in _inMemory.Concat(_pending))
            {
                if (handle.InMemory && !handle.Writing)
                    FreeMemory(handle);
            }
            _inMemory.Clear();
            _pending.Clear();
            _memoryBytes = 0;
            Interlocked.Exchange(ref _pendingBytes, 0);
            foreach (Segment segment in _segments)
                segment.File.Dispose();

            _segments.Clear();
            _byHash.Clear();
        }
    }
}
