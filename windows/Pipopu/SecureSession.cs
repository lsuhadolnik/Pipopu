using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pipopu;

internal sealed record Wire(
    string Type, string? Id = null, string? Name = null, int Kind = 0, long Size = 0,
    bool Pair = false, byte[]? Data = null, string[]? Urls = null, string? Route = null, string? Error = null,
    bool Receive = false);

internal sealed class TrustStore
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pipopu");
    private readonly string trustFile;
    private readonly object gate = new();
    private readonly ECDiffieHellman identity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly Dictionary<string, string> trusted;

    internal TrustStore(string? storageDirectory = null)
    {
        string directory = storageDirectory ?? Folder;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "identity");
        trustFile = Path.Combine(directory, "trusted.json");
        if (File.Exists(path))
        {
            byte[] key = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try { identity.ImportPkcs8PrivateKey(key, out _); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        else
        {
            byte[] key = identity.ExportPkcs8PrivateKey();
            try { Save(path, ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser)); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        trusted = File.Exists(trustFile)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(trustFile)) ?? new()
            : new();
    }

    internal byte[] PublicKey
    {
        get
        {
            lock (gate)
            {
                var q = identity.ExportParameters(false).Q;
                return [4, .. q.X!, .. q.Y!];
            }
        }
    }

    internal byte[] Agree(byte[] key)
    {
        if (key.Length != 65 || key[0] != 4)
            throw new InvalidDataException("Invalid peer identity.");
        using var peer = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = key[1..33], Y = key[33..65] }
        });
        lock (gate)
            return identity.DeriveRawSecretAgreement(peer.PublicKey);
    }

    internal bool Contains(string id) { lock (gate) return trusted.ContainsKey(id); }
    internal KeyValuePair<string, string>[] All() { lock (gate) return trusted.ToArray(); }

    internal void Remember(string id, string name)
    {
        lock (gate)
        {
            var updated = new Dictionary<string, string>(trusted) { [id] = name };
            Save(trustFile, JsonSerializer.SerializeToUtf8Bytes(updated));
            trusted[id] = name;
        }
    }

    internal void Forget(string id)
    {
        lock (gate)
        {
            var updated = new Dictionary<string, string>(trusted);
            updated.Remove(id);
            Save(trustFile, JsonSerializer.SerializeToUtf8Bytes(updated));
            trusted.Remove(id);
        }
    }

    private static void Save(string path, byte[] bytes)
    {
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, true);
    }
}

internal sealed class SecureSession
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly byte[] sendKey;
    private readonly byte[] receiveKey;
    private readonly byte[] fileKey;
    private ulong sent;
    private ulong received;

    internal SecureSession(TrustStore trust, byte[] hello, byte[] welcome)
    {
        byte[] peer = hello[1..66];
        Identity = Convert.ToHexString(SHA256.HashData(peer));
        byte[] secret = trust.Agree(peer);
        byte[] salt = SHA256.HashData([.. hello, .. welcome]);
        byte[] keys = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 100, salt, "Pipopu v2 session"u8.ToArray());
        receiveKey = keys[..32];
        sendKey = keys[32..64];
        fileKey = keys[64..96];
        Code = (BinaryPrimitives.ReadUInt32LittleEndian(keys.AsSpan(96)) % 1_000_000).ToString("D6");
        CryptographicOperations.ZeroMemory(secret);
        CryptographicOperations.ZeroMemory(keys);
    }

    internal string Identity { get; }
    internal string Code { get; }
    internal byte[] FileKey(string id) => HMACSHA256.HashData(fileKey, Encoding.UTF8.GetBytes(id));

    internal byte[] Seal(Wire packet)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(packet, Json);
        var result = new byte[9 + plain.Length + 16];
        result[0] = 3;
        ulong sequence = ++sent;
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(1), sequence);
        Span<byte> nonce = stackalloc byte[12];
        nonce.Clear();
        result.AsSpan(1, 8).CopyTo(nonce[4..]);
        using var cipher = new AesGcm(sendKey, 16);
        cipher.Encrypt(nonce, plain, result.AsSpan(9, plain.Length), result.AsSpan(^16), result.AsSpan(0, 9));
        return result;
    }

    internal Wire Open(byte[] packet)
    {
        if (packet.Length < 26 || packet[0] != 3)
            throw new InvalidDataException("Invalid secure packet.");
        ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(1));
        if (sequence <= received)
            throw new InvalidDataException("Duplicate or out-of-order secure packet.");
        var plain = new byte[packet.Length - 25];
        Span<byte> nonce = stackalloc byte[12];
        nonce.Clear();
        packet.AsSpan(1, 8).CopyTo(nonce[4..]);
        using var cipher = new AesGcm(receiveKey, 16);
        cipher.Decrypt(nonce, packet.AsSpan(9, plain.Length), packet.AsSpan(^16), plain, packet.AsSpan(0, 9));
        var message = JsonSerializer.Deserialize<Wire>(plain, Json) ?? throw new InvalidDataException("Invalid secure message.");
        received = sequence;
        return message;
    }
}
