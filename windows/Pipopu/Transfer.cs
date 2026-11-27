using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Pipopu;

internal enum TransferKind : byte { Text = 1, Image, File }

internal sealed record Transfer(TransferKind Kind, string Name, byte[] Data, TransferFile? File = null)
{
    internal const int Limit = 500_000;
    internal const int TextLimit = 3_000_000;
    internal const long FileLimit = 5_000_000_000;
    internal string Text => new UTF8Encoding(false, true).GetString(Data);
    internal long Size => File?.Size ?? Data.LongLength;

    internal Stream OpenRead() => File is null ? new MemoryStream(Data, writable: false) :
        System.IO.File.OpenRead(File.Path);

    internal static void Validate(Wire offer)
    {
        if (offer.Id is null || !Guid.TryParseExact(offer.Id, "N", out _) ||
            offer.Name is null || Encoding.UTF8.GetByteCount(offer.Name) > 255 ||
            offer.Kind is < 1 or > 3 || offer.Size < 0 || offer.Size > FileLimit ||
            (offer.Kind == (int)TransferKind.Text && offer.Size > TextLimit))
            throw new InvalidDataException("Invalid file offer.");
    }
}

internal sealed class TransferFile(string path, long size, bool owned = true) : IDisposable
{
    internal string Path { get; } = path;
    internal long Size { get; } = size;
    internal static string TemporaryPath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pipopu-{Guid.NewGuid():N}");
    public void Dispose()
    {
        if (owned)
        {
            try { File.Delete(Path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

internal static class FileCipher
{
    internal const int Block = 256 * 1024;
    internal static long EncryptedSize(long size) => checked(size + ((size + Block - 1) / Block) * 16);

    internal static async Task<TransferFile> TransformAsync(
        Stream source, long size, byte[] key, bool encrypt, CancellationToken cancellation)
    {
        var temp = new TransferFile(TransferFile.TemporaryPath(), encrypt ? EncryptedSize(size) : size);
        try
        {
            await using var output = File.Create(temp.Path);
            using var cipher = new AesGcm(key, 16);
            var plain = new byte[Block];
            var encrypted = new byte[Block];
            var tag = new byte[16];
            var nonce = new byte[12];
            for (long offset = 0; offset < size;)
            {
                int count = (int)Math.Min(Block, size - offset);
                BinaryPrimitives.WriteInt64LittleEndian(nonce.AsSpan(4), offset / Block);
                if (encrypt)
                {
                    await source.ReadExactlyAsync(plain.AsMemory(0, count), cancellation);
                    cipher.Encrypt(nonce, plain.AsSpan(0, count), encrypted.AsSpan(0, count), tag);
                    await output.WriteAsync(encrypted.AsMemory(0, count), cancellation);
                    await output.WriteAsync(tag, cancellation);
                }
                else
                {
                    await source.ReadExactlyAsync(encrypted.AsMemory(0, count), cancellation);
                    await source.ReadExactlyAsync(tag, cancellation);
                    cipher.Decrypt(nonce, encrypted.AsSpan(0, count), tag, plain.AsSpan(0, count));
                    await output.WriteAsync(plain.AsMemory(0, count), cancellation);
                }
                offset += count;
            }
            if (await source.ReadAsync(plain.AsMemory(0, 1), cancellation) != 0)
                throw new InvalidDataException("The file size changed during transfer.");
            return temp;
        }
        catch
        {
            temp.Dispose();
            throw;
        }
    }
}

internal static class Frame
{
    internal const byte Begin = 1;
    internal const byte Chunk = 2;
    internal const byte End = 3;
    internal const int MaximumPacket = 700_000;

    internal static byte[] Control(byte opcode, uint token, int? length = null)
    {
        var bytes = new byte[length.HasValue ? 9 : 5];
        bytes[0] = opcode;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), token);
        if (length.HasValue)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5), length.Value);
        return bytes;
    }
}

internal sealed class IncomingPacket
{
    private readonly uint token;
    private readonly int length;
    private readonly MemoryStream bytes = new();
    internal DateTime LastActivity { get; private set; } = DateTime.UtcNow;

    internal IncomingPacket(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != 9)
            throw new InvalidDataException("Invalid start frame.");
        token = BinaryPrimitives.ReadUInt32LittleEndian(frame[1..]);
        length = BinaryPrimitives.ReadInt32LittleEndian(frame[5..]);
        if (length is < 1 or > Frame.MaximumPacket)
            throw new InvalidDataException("Invalid secure packet size.");
    }

    internal void Append(ReadOnlySpan<byte> frame)
    {
        if (frame.Length <= 1 || bytes.Length + frame.Length - 1 > length)
            throw new InvalidDataException("Invalid packet frame.");
        bytes.Write(frame[1..]);
        LastActivity = DateTime.UtcNow;
    }

    internal byte[] Finish(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != 5 || BinaryPrimitives.ReadUInt32LittleEndian(frame[1..]) != token || bytes.Length != length)
            throw new InvalidDataException("Incomplete secure packet.");
        return bytes.ToArray();
    }
}
