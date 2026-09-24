using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;

namespace RenderiteRecovery;

internal static class PayloadHash
{
    internal static ulong Compute(ReadOnlySpan<byte> data)
    {
        if (Sse42.X64.IsSupported)
            return Crc32CX64(data);

        if (Crc32.Arm64.IsSupported)
            return Crc32CArm64(data);

        return MemoryMarshal.Read<ulong>(SHA256.HashData(data));
    }

    private static ulong Crc32CX64(ReadOnlySpan<byte> data)
    {
        ref byte start = ref MemoryMarshal.GetReference(data);
        int length = data.Length, i = 0;
        ulong a = 0xFFFFFFFF, b = 0x9E3779B9, c = 0x7F4A7C15;
        for (; i + 24 <= length; i += 24)
        {
            a = Sse42.X64.Crc32(a, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, i)));
            b = Sse42.X64.Crc32(b, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, i + 8)));
            c = Sse42.X64.Crc32(c, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, i + 16)));
        }
        for (; i + 8 <= length; i += 8)
            a = Sse42.X64.Crc32(a, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, i)));

        uint tail = (uint)a;
        for (; i < length; i++)
            tail = Sse42.Crc32(tail, Unsafe.Add(ref start, i));

        return Combine(tail, (uint)b, (uint)c);
    }

    private static ulong Crc32CArm64(ReadOnlySpan<byte> data)
    {
        ref byte start = ref MemoryMarshal.GetReference(data);
        int length = data.Length, i = 0;
        uint a = 0xFFFFFFFF, b = 0x9E3779B9, c = 0x7F4A7C15;
        for (; i + 24 <= length; i += 24)
        {
            a = Crc32.Arm64.ComputeCrc32C(a, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, i)));
            b = Crc32.Arm64.ComputeCrc32C(b, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, i + 8)));
            c = Crc32.Arm64.ComputeCrc32C(c, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, i + 16)));
        }
        for (; i + 8 <= length; i += 8)
            a = Crc32.Arm64.ComputeCrc32C(a, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, i)));

        for (; i < length; i++)
            a = Crc32.ComputeCrc32C(a, Unsafe.Add(ref start, i));

        return Combine(a, b, c);
    }

    private static ulong Combine(uint a, uint b, uint c) => (((ulong)a << 32) | b) ^ (c * 0x9E3779B97F4A7C15UL);
}
