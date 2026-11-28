import CryptoKit
import Foundation
import Security

struct CloudNotice: Codable, Identifiable, Sendable {
    let id: String
    let peer: String
    let name: String
    let outgoing: Bool
    let status: String
    let error: String?
}

struct Wire: Codable {
    var type: String
    var id: String? = nil
    var name: String? = nil
    var kind: Int? = nil
    var size: Int64? = nil
    var pair: Bool? = nil
    var data: Data? = nil
    var urls: [String]? = nil
    var route: String? = nil
    var error: String? = nil
    var receive: Bool? = nil
    var protocolMin: Int? = nil
    var protocolMax: Int? = nil
    var version: Int? = nil
    var secure: Bool? = nil
}

enum WireProtocol {
    static let legacy = 2
    static let minimum = 2
    static let current = 3
    static let cloudAndSecure = 3

    static func negotiate(_ minimum: Int?, _ maximum: Int?) -> Int? {
        if minimum == nil && maximum == nil {
            return (Self.minimum...current).contains(legacy) ? legacy : nil
        }
        guard let minimum, let maximum, minimum > 0, maximum >= minimum else { return nil }
        let selected = min(maximum, current)
        return selected >= max(Self.minimum, minimum) ? selected : nil
    }
}

struct PeerTrust: Identifiable {
    let id: String
    let name: String
}

enum TrustStore {
    // All three targets list the same keychain group first, making it their default for writes.
    private static func query(_ account: String? = nil) -> [String: Any] {
        var request: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "Pipopu secure transport v2"
        ]
        request[kSecAttrAccount as String] = account
        return request
    }

    private static func read(_ account: String) throws -> Data? {
        var request = query(account)
        request[kSecReturnData as String] = true
        var result: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data else { throw keychainError(status) }
        return data
    }

    private static func add(_ data: Data, account: String) throws -> Bool {
        var request = query(account)
        request[kSecValueData as String] = data
        request[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let status = SecItemAdd(request as CFDictionary, nil)
        if status == errSecDuplicateItem { return false }
        guard status == errSecSuccess else { throw keychainError(status) }
        return true
    }

    static func identity() throws -> P256.KeyAgreement.PrivateKey {
        if let data = try read("identity") { return try P256.KeyAgreement.PrivateKey(rawRepresentation: data) }
        let key = P256.KeyAgreement.PrivateKey()
        if try add(key.rawRepresentation, account: "identity") { return key }
        guard let saved = try read("identity") else { throw TransferError.invalid }
        return try P256.KeyAgreement.PrivateKey(rawRepresentation: saved)
    }

    static func contains(_ id: String) -> Bool { (try? read("peer:" + id)) != nil }

    static func all() -> [PeerTrust] {
        var request = query()
        request[kSecMatchLimit as String] = kSecMatchLimitAll
        request[kSecReturnAttributes as String] = true
        request[kSecReturnData as String] = true
        var result: CFTypeRef?
        guard SecItemCopyMatching(request as CFDictionary, &result) == errSecSuccess,
              let records = result as? [[String: Any]] else { return [] }
        return records.compactMap { record -> PeerTrust? in
            guard let account = record[kSecAttrAccount as String] as? String, account.hasPrefix("peer:"),
                  let data = record[kSecValueData as String] as? Data,
                  let name = String(data: data, encoding: .utf8) else { return nil }
            return PeerTrust(id: String(account.dropFirst(5)), name: name)
        }.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    static func remember(_ id: String, name: String) throws {
        let account = "peer:" + id
        let data = Data(name.utf8)
        if try !add(data, account: account) {
            let status = SecItemUpdate(query(account) as CFDictionary, [kSecValueData as String: data] as CFDictionary)
            guard status == errSecSuccess else { throw keychainError(status) }
        }
    }

    static func forget(_ id: String) throws {
        let status = SecItemDelete(query("peer:" + id) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw keychainError(status) }
    }

    private static func keychainError(_ status: OSStatus) -> TransferError {
        .message("Pipopu could not access its shared Keychain (\(status)).")
    }
}

final class SecureSession {
    private let sendKey: SymmetricKey
    private let receiveKey: SymmetricKey
    private let fileKey: SymmetricKey
    private var sent: UInt64 = 0
    private var received: UInt64 = 0
    let identity: String
    let code: String

    init(key: P256.KeyAgreement.PrivateKey, hello: Data, welcome: Data) throws {
        guard welcome.count == 98, welcome.first == 2 else { throw TransferError.invalid }
        let point = Data(welcome[1..<66])
        let peer = try P256.KeyAgreement.PublicKey(x963Representation: point)
        identity = SHA256.hash(data: point).map { String(format: "%02X", $0) }.joined()
        let secret = try key.sharedSecretFromKeyAgreement(with: peer)
        let derived = secret.hkdfDerivedSymmetricKey(using: SHA256.self,
            salt: Data(SHA256.hash(data: hello + welcome)), sharedInfo: Data("Pipopu v2 session".utf8),
            outputByteCount: 100)
        let bytes = derived.withUnsafeBytes { Data($0) }
        sendKey = SymmetricKey(data: bytes[0..<32])
        receiveKey = SymmetricKey(data: bytes[32..<64])
        fileKey = SymmetricKey(data: bytes[64..<96])
        code = String(format: "%06u", Frame.readNumber(bytes, at: 96) % 1_000_000)
    }

    func key(for id: String) -> SymmetricKey {
        SymmetricKey(data: HMAC<SHA256>.authenticationCode(for: Data(id.utf8), using: fileKey))
    }

    func seal(_ message: Wire) throws -> Data {
        sent += 1
        let sequence = bytes(sent)
        let header = Data([3]) + sequence
        let nonce = try AES.GCM.Nonce(data: Data(repeating: 0, count: 4) + sequence)
        let encrypted = try AES.GCM.seal(JSONEncoder().encode(message), using: sendKey,
                                        nonce: nonce, authenticating: header)
        return header + encrypted.ciphertext + encrypted.tag
    }

    func open(_ packet: Data) throws -> Wire {
        guard packet.count >= 26, packet.first == 3 else { throw TransferError.invalid }
        let sequence = (0..<8).reduce(UInt64(0)) { $0 | (UInt64(packet[$1 + 1]) << ($1 * 8)) }
        guard sequence > received else { throw TransferError.invalid }
        let nonce = try AES.GCM.Nonce(data: Data(repeating: 0, count: 4) + bytes(sequence))
        let box = try AES.GCM.SealedBox(nonce: nonce, ciphertext: packet.dropFirst(9).dropLast(16), tag: packet.suffix(16))
        let clear = try AES.GCM.open(box, using: receiveKey, authenticating: packet.prefix(9))
        let message = try JSONDecoder().decode(Wire.self, from: clear)
        received = sequence
        return message
    }
}

private func bytes(_ value: UInt64) -> Data {
    Data((0..<8).map { UInt8(truncatingIfNeeded: value >> ($0 * 8)) })
}

final class TemporaryFile: @unchecked Sendable {
    let url: URL
    let size: Int64
    private let owned: Bool

    init(size: Int64) {
        url = FileManager.default.temporaryDirectory.appendingPathComponent("pipopu-" + UUID().uuidString)
        self.size = size
        owned = true
    }

    init(url: URL, size: Int64) {
        self.url = url
        self.size = size
        owned = false
    }

    deinit { if owned { try? FileManager.default.removeItem(at: url) } }
}

enum FileCipher {
    static let block = 256 * 1024
    static func encryptedSize(_ size: Int64) -> Int64 { size + (size + Int64(block) - 1) / Int64(block) * 16 }

    static func transform(input: FileHandle, size: Int64, key: SymmetricKey, encrypt: Bool) async throws -> TemporaryFile {
        let result = TemporaryFile(size: encrypt ? encryptedSize(size) : size)
        _ = FileManager.default.createFile(atPath: result.url.path, contents: nil)
        let output = try FileHandle(forWritingTo: result.url)
        defer { try? output.close() }
        var offset: Int64 = 0
        while offset < size {
            try Task.checkCancellation()
            let count = Int(min(Int64(block), size - offset))
            let nonce = try AES.GCM.Nonce(data: Data(repeating: 0, count: 4) + bytes(UInt64(offset / Int64(block))))
            if encrypt {
                let plain = try readExact(input, count: count)
                let sealed = try AES.GCM.seal(plain, using: key, nonce: nonce)
                try output.write(contentsOf: sealed.ciphertext)
                try output.write(contentsOf: sealed.tag)
            } else {
                let encrypted = try readExact(input, count: count)
                let tag = try readExact(input, count: 16)
                let sealed = try AES.GCM.SealedBox(nonce: nonce, ciphertext: encrypted, tag: tag)
                do { try output.write(contentsOf: AES.GCM.open(sealed, using: key)) }
                catch CryptoKitError.authenticationFailure {
                    throw TransferError.message("The downloaded file failed its encryption integrity check.")
                }
            }
            offset += Int64(count)
        }
        guard (try input.read(upToCount: 1) ?? Data()).isEmpty else { throw TransferError.invalid }
        return result
    }

    static func readExact(_ file: FileHandle, count: Int) throws -> Data {
        var result = Data()
        while result.count < count {
            guard let chunk = try file.read(upToCount: count - result.count), !chunk.isEmpty else {
                throw TransferError.message("The file ended before all expected bytes arrived.")
            }
            result.append(chunk)
        }
        return result
    }
}
