using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Pipopu;

internal enum TransferKind : byte { Text = 1, Image, File }

internal sealed record Transfer(TransferKind Kind, string Name, byte[] Data)
{
    internal const int Limit = 3_000_000;
    internal const int MaximumEnvelope = Limit + 3 + 255 + 32;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal string Text => Utf8.GetString(Data);

    internal byte[] Encode()
    {
        if (Data.Length > Limit)
            throw new InvalidDataException("The payload exceeds the 3 MB limit.");

        byte[] name = Utf8.GetBytes(Name);
        if (name.Length > 255)
            throw new InvalidDataException("The file name exceeds 255 UTF-8 bytes.");

        var envelope = new byte[3 + name.Length + Data.Length + 32];
        envelope[0] = (byte)Kind;
        BinaryPrimitives.WriteUInt16LittleEndian(envelope.AsSpan(1), (ushort)name.Length);
        name.CopyTo(envelope, 3);
        Data.CopyTo(envelope, 3 + name.Length);
        SHA256.HashData(envelope.AsSpan(0, envelope.Length - 32), envelope.AsSpan(envelope.Length - 32));
        return envelope;
    }

    internal static Transfer Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 35 or > MaximumEnvelope ||
            bytes[0] < (byte)TransferKind.Text || bytes[0] > (byte)TransferKind.File)
            throw new InvalidDataException("Invalid transfer envelope.");

        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[1..]);
        int dataStart = 3 + nameLength;
        int dataEnd = bytes.Length - 32;
        if (nameLength > 255 || dataStart > dataEnd || dataEnd - dataStart > Limit ||
            !bytes[dataEnd..].SequenceEqual(SHA256.HashData(bytes[..dataEnd])))
            throw new InvalidDataException("The received transfer did not pass validation.");

        var transfer = new Transfer((TransferKind)bytes[0], Utf8.GetString(bytes[3..dataStart]), bytes[dataStart..dataEnd].ToArray());
        if (transfer.Kind == TransferKind.Text)
            _ = Utf8.GetCharCount(transfer.Data);
        return transfer;
    }
}

internal static class Frame
{
    internal const byte Begin = 1;
    internal const byte Chunk = 2;
    internal const byte End = 3;
    internal const byte Received = 4;
    internal const byte Available = 5;
    internal const byte Abort = 6;

    internal static byte[] Control(byte opcode, uint token, int? length = null)
    {
        byte[] bytes = new byte[length.HasValue ? 9 : 5];
        bytes[0] = opcode;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), token);
        if (length.HasValue)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5), length.Value);
        return bytes;
    }
}

internal sealed class IncomingTransfer
{
    private readonly uint token;
    private readonly int length;
    private readonly MemoryStream bytes = new();
    internal DateTime LastActivity { get; private set; } = DateTime.UtcNow;

    internal IncomingTransfer(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != 9)
            throw new InvalidDataException("Invalid start frame.");
        token = BinaryPrimitives.ReadUInt32LittleEndian(frame[1..]);
        length = BinaryPrimitives.ReadInt32LittleEndian(frame[5..]);
        if (length is < 35 or > Transfer.MaximumEnvelope)
            throw new InvalidDataException("Invalid transfer size.");
    }

    internal void Append(ReadOnlySpan<byte> frame)
    {
        if (frame.Length <= 1 || bytes.Length + frame.Length - 1 > length)
            throw new InvalidDataException("Invalid data frame.");
        bytes.Write(frame[1..]);
        LastActivity = DateTime.UtcNow;
    }

    internal Transfer Finish(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != 5 || BinaryPrimitives.ReadUInt32LittleEndian(frame[1..]) != token ||
            bytes.Length != length)
            throw new InvalidDataException("Incomplete transfer.");
        return Transfer.Decode(bytes.GetBuffer().AsSpan(0, length));
    }
}
