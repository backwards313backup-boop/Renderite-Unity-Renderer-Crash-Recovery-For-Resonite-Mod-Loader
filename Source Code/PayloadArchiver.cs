using System.Collections.Concurrent;
using System.Collections;
using System.Reflection;
using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;

namespace RenderiteRecovery;

internal static class PayloadArchiver
{
    private static readonly MethodInfo AllocateBlockMethod = AccessTools.Method(typeof(RenderSystem), "AllocateBlock").MakeGenericMethod(typeof(byte));

    private static long _diskLimit;
    private static long _compactionThreshold = 512L * 1024 * 1024;
    private static long _memoryLimit;
    private static volatile string? _directory;
    private static readonly Lazy<PayloadStore> Store =
        new(() => new PayloadStore(Volatile.Read(ref _diskLimit), wasteCompactionThreshold: Volatile.Read(ref _compactionThreshold),
            memoryLimit: Volatile.Read(ref _memoryLimit), directory: _directory)
        {
            WriteFailed = RecoveryCoordinator.InvalidateJournal
        });
    internal sealed record Buffer(int Id, int Capacity, int Offset, int Length, PayloadStore.Handle Storage);

    internal static long DiskLimit => Volatile.Read(ref _diskLimit);
    internal static long CompactionThreshold => Volatile.Read(ref _compactionThreshold);
    internal static long MemoryLimit => Volatile.Read(ref _memoryLimit);

    internal static string Directory => Store.IsValueCreated ? Store.Value.ActiveDirectory
        : _directory ?? Path.GetTempPath();

    internal static void Configure(long diskLimit, long compactionThreshold, long memoryLimit, string? directory)
    {
        Volatile.Write(ref _diskLimit, diskLimit);
        Volatile.Write(ref _compactionThreshold, compactionThreshold);
        Volatile.Write(ref _memoryLimit, memoryLimit);
        _directory = directory;
        if (!Store.IsValueCreated)
            return;

        Store.Value.Limit = diskLimit;
        Store.Value.WasteCompactionThreshold = compactionThreshold;
        Store.Value.Directory = directory;
        if (Store.Value.MemoryLimit != memoryLimit)
            Store.Value.SetMemoryLimit(memoryLimit);
    }

    internal static long LiveBytes => Store.IsValueCreated ? Store.Value.LiveBytes : 0;

    internal static long MemoryBytes => Store.IsValueCreated ? Store.Value.MemoryBytes : 0;

    internal static long DiskBytes => Store.IsValueCreated ? Store.Value.DiskBytes : 0;

    internal static long PendingBytes => Store.IsValueCreated ? Store.Value.PendingBytes : 0;

    internal static int SegmentCount => Store.IsValueCreated ? Store.Value.SegmentCount : 0;

    internal static void Release(IReadOnlyList<Buffer> buffers)
    {
        foreach (Buffer buffer in buffers)
            Store.Value.Release(buffer.Storage);
    }

    private static readonly FieldInfo SharedMemoryField = AccessTools.Field(typeof(RenderSystem), "_sharedMemory");
    private static readonly FieldInfo ManagersField = AccessTools.Field(typeof(SharedMemoryManager), "_managers");

    internal static List<(int Id, int Capacity, int Offset, int Length)>? FindDescriptors(object command)
    {
        if (PlanFor(command.GetType()).IsEmpty)
            return null;

        var descriptors = new List<(int Id, int Capacity, int Offset, int Length)>();
        HashSet<object>? visited = null;
        Walk(command, null, descriptors, null, ref visited);
        return descriptors.Count == 0 ? null : descriptors;
    }

    private static SharedMemoryBlockManager? FindManager(RenderSystem system, int id)
    {
        var manager = (SharedMemoryManager)SharedMemoryField.GetValue(system)!;
        foreach (SharedMemoryBlockManager candidate in (IEnumerable)ManagersField.GetValue(manager)!)
        {
            if (candidate.Id == id)
                return candidate;
        }

        return null;
    }

    internal static IReadOnlyList<Buffer> Capture(RendererCommand command, RenderSystem system)
    {
        if (FindDescriptors(command) is not { } descriptors)
            return Array.Empty<Buffer>();

        var result = new List<Buffer>(descriptors.Count);
        try
        {
            foreach (var descriptor in descriptors)
            {
                if (descriptor.Length <= 0)
                    continue;

                if (FindManager(system, descriptor.Id) is not { } source)
                    throw new InvalidOperationException($"Shared-memory manager {descriptor.Id} is unavailable.");

                if (descriptor.Offset < 0 || (long)descriptor.Offset + descriptor.Length > source.Capacity)
                    throw new InvalidOperationException($"Shared-memory descriptor {descriptor.Id}@{descriptor.Offset}+{descriptor.Length} exceeds capacity {source.Capacity}.");

                result.Add(new Buffer(descriptor.Id, descriptor.Capacity, descriptor.Offset,
                    descriptor.Length, Store.Value.Save(source.RawData.Slice(descriptor.Offset, descriptor.Length))));
            }
        }
        catch
        {
            Release(result);
            throw;
        }
        return result;
    }

    internal static Span<byte> Access(RenderSystem system, int bufferId, int offset, int length)
    {
        SharedMemoryBlockManager source = FindManager(system, bufferId) ?? throw new InvalidOperationException($"Shared-memory manager {bufferId} is unavailable.");
        if (offset < 0 || length < 0 || (long)offset + length > source.Capacity)
            throw new InvalidOperationException($"Shared-memory descriptor {bufferId}@{offset}+{length} exceeds capacity {source.Capacity}.");

        return source.RawData.Slice(offset, length);
    }

    internal static IReadOnlyList<IDisposable> Rehydrate(
        RendererCommand command, RenderSystem system, IReadOnlyList<Buffer> buffers)
    {
        var replacements = new Dictionary<(int, int, int, int), Queue<Buffer>>();
        foreach (Buffer buffer in buffers)
        {
            var key = (buffer.Id, buffer.Capacity, buffer.Offset, buffer.Length);
            if (!replacements.TryGetValue(key, out var queue))
                replacements[key] = queue = new();

            queue.Enqueue(buffer);
        }
        var leases = new List<IDisposable>();
        HashSet<object>? visited = null;
        try
        {
        Walk(command, system, null, (type, descriptor) =>
        {
            if (descriptor.Length == 0)
                return null;

            var key = (descriptor.Id, descriptor.Capacity, descriptor.Offset, descriptor.Length);
            if (!replacements.TryGetValue(key, out var queue) || queue.Count == 0)
                throw new InvalidDataException($"Missing archived payload for {command.GetType().Name}: {key}.");

            Buffer archived = queue.Dequeue();
            var lease = (SharedMemoryBlockLease<byte>)AllocateBlockMethod.Invoke(
                system, new object?[] { archived.Length, false, command })!;
            try { Store.Value.Load(archived.Storage, lease.Data); }
            catch { lease.Dispose(); throw; }
            leases.Add(lease);
            var fresh = lease.Descriptor;
            return CreateDescriptor(type, fresh.bufferId, fresh.bufferCapacity, fresh.offset, fresh.length);
        }, ref visited);
        return leases;
        }
        catch
        {
            foreach (IDisposable lease in leases)
                lease.Dispose();

            throw;
        }
    }

    private static object? Walk(object? value, RenderSystem? system,
        List<(int Id, int Capacity, int Offset, int Length)>? capture,
        Func<Type, (int Id, int Capacity, int Offset, int Length), object?>? replace,
        ref HashSet<object>? visited)
    {
        if (value is null)
            return null;

        Type type = value.GetType();
        if (IsDescriptor(type))
        {
            var descriptor = ReadDescriptor(value, type);
            capture?.Add(descriptor);
            return replace?.Invoke(type, descriptor) ?? value;
        }
        Plan plan = PlanFor(type);
        if (plan.IsEmpty)
            return value;

        if (plan.Recurses && !type.IsValueType)
        {
            visited ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
            if (!visited.Add(value))
                return value;
        }
        if (plan.IsList)
        {
            var list = (IList)value;
            for (int i = 0; i < list.Count; i++)
            {
                object? updated = Walk(list[i], system, capture, replace, ref visited);
                if (replace is not null && updated is not null)
                    list[i] = updated;
            }
            return value;
        }
        foreach (FieldInfo field in plan.Fields)
        {
            object? child;
            try { child = field.GetValue(value); } catch { continue; }
            object? updated = Walk(child, system, capture, replace, ref visited);
            if (replace is not null && updated is not null && !ReferenceEquals(child, updated))
            {
                field.SetValue(value, updated);
            }
        }
        return value;
    }

    private sealed class Plan(FieldInfo[] fields, bool isList, bool recurses)
    {
        internal static readonly Plan Empty = new([], false, false);
        internal static readonly Plan List = new([], true, true);
        internal FieldInfo[] Fields { get; } = fields;
        internal bool IsList { get; } = isList;
        internal bool Recurses { get; } = recurses;
        internal bool IsEmpty => !IsList && Fields.Length == 0;
    }

    private static readonly ConcurrentDictionary<Type, Plan> Plans = new();

    private static Plan PlanFor(Type type) => Plans.GetOrAdd(type, static type =>
    {
        if (typeof(IList).IsAssignableFrom(type))
            return ElementType(type) is Type element && !MayHoldDescriptor(element, new HashSet<Type>()) ? Plan.Empty : Plan.List;

        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal))
            return Plan.Empty;

        FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => MayHoldDescriptor(field.FieldType, new HashSet<Type> { type }))
            .ToArray();
        return fields.Length == 0 ? Plan.Empty
            : new Plan(fields, false, fields.Any(field => !IsDescriptor(field.FieldType)));
    });

    private static bool MayHoldDescriptor(Type type, HashSet<Type> visiting)
    {
        if (IsDescriptor(type))
            return true;

        if (type.IsPointer || type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal))
            return false;

        if (typeof(IList).IsAssignableFrom(type))
            return ElementType(type) is not Type element || MayHoldDescriptor(element, visiting);

        if (!type.IsValueType && !type.IsSealed)
            return true;

        if (!visiting.Add(type))
            return false;

        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (MayHoldDescriptor(field.FieldType, visiting))
                return true;
        }

        return false;
    }

    private static Type? ElementType(Type listType) => listType.IsArray ? listType.GetElementType()
        : listType.GetInterfaces().FirstOrDefault(face => face.IsGenericType && face.GetGenericTypeDefinition() == typeof(IList<>))
            ?.GetGenericArguments()[0];

    private static bool IsDescriptor(Type type) => type == typeof(SharedMemoryBufferDescriptor)
        || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SharedMemoryBufferDescriptor<>);

    private static (int Id, int Capacity, int Offset, int Length) ReadDescriptor(object value, Type type)
    {
        if (type == typeof(SharedMemoryBufferDescriptor))
        {
            var descriptor = (SharedMemoryBufferDescriptor)value;
            return (descriptor.bufferId, descriptor.bufferCapacity, descriptor.offset, descriptor.length);
        }
        int Read(string name) => (int)type.GetProperty(name)!.GetValue(value)!;
        return (Read("bufferId"), Read("bufferCapacity"), Read("offset"), Read("length"));
    }

    private static object CreateDescriptor(Type type, int id, int capacity, int offset, int length)
    {
        if (type != typeof(SharedMemoryBufferDescriptor))
            return Activator.CreateInstance(type, id, capacity, offset, length)!;

        object descriptor = new SharedMemoryBufferDescriptor();
        type.GetField("bufferId")!.SetValue(descriptor, id);
        type.GetField("bufferCapacity")!.SetValue(descriptor, capacity);
        type.GetField("offset")!.SetValue(descriptor, offset);
        type.GetField("length")!.SetValue(descriptor, length);
        return descriptor;
    }
}
