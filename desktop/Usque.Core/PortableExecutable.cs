using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Usque.Core;

internal static class PortableExecutable
{
    internal const ushort X64 = 0x8664, Arm64 = 0xAA64;

    internal static ushort ReadMachine(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[64];
        if (stream.Length < header.Length) throw new InvalidDataException("Invalid Windows executable header.");
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt16LittleEndian(header) != 0x5A4D)
            throw new InvalidDataException("Invalid Windows executable signature.");
        var offset = BinaryPrimitives.ReadInt32LittleEndian(header[60..]);
        if (offset < 64 || offset > stream.Length - 6) throw new InvalidDataException("Invalid Windows PE header offset.");
        stream.Position = offset;
        Span<byte> pe = stackalloc byte[6];
        stream.ReadExactly(pe);
        if (BinaryPrimitives.ReadUInt32LittleEndian(pe) != 0x00004550)
            throw new InvalidDataException("Invalid Windows PE signature.");
        return BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]);
    }

    internal static void Validate(string path, Architecture architecture)
    {
        var expected = architecture switch
        {
            Architecture.X64 => X64,
            Architecture.Arm64 => Arm64,
            _ => throw new PlatformNotSupportedException("Usque Desktop supports x64 and ARM64 processes only.")
        };
        if (ReadMachine(path) != expected)
            throw new InvalidDataException($"{Path.GetFileName(path)} does not match this app's {architecture} architecture. Rebuild the distribution with the matching Wintun DLL before connecting.");
    }
}
