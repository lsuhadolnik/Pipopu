import CryptoKit
import Foundation
import UniformTypeIdentifiers

struct Transfer: Identifiable {
    enum Kind: UInt8 { case text = 1, image, file }

    static let limit = 3_000_000
    static let maximumEnvelope = limit + 3 + 255 + 32

    let id = UUID()
    let kind: Kind
    let name: String
    let data: Data

    var text: String { String(decoding: data, as: UTF8.self) }

    init(kind: Kind, name: String = "", data: Data) throws {
        guard data.count <= Self.limit else { throw TransferError.tooLarge }
        guard name.utf8.count <= 255, kind != .text || String(data: data, encoding: .utf8) != nil
        else { throw TransferError.invalid }
        self.kind = kind
        self.name = name
        self.data = data
    }

    func envelope() -> Data {
        let filename = Data(name.utf8)
        var bytes = Data([kind.rawValue, UInt8(filename.count), 0])
        bytes.append(filename)
        bytes.append(data)
        bytes.append(contentsOf: SHA256.hash(data: bytes))
        return bytes
    }

    init(envelope: Data) throws {
        guard (35...Self.maximumEnvelope).contains(envelope.count),
              let kind = Kind(rawValue: envelope[0]) else { throw TransferError.invalid }
        let nameEnd = 3 + Int(envelope[1]) + Int(envelope[2]) * 256
        let body = envelope.dropLast(32)
        guard nameEnd <= body.count, nameEnd <= 258,
              envelope.suffix(32).elementsEqual(SHA256.hash(data: body)),
              let name = String(data: body[3..<nameEnd], encoding: .utf8)
        else { throw TransferError.invalid }
        try self.init(kind: kind, name: name, data: Data(body[nameEnd...]))
    }
}

extension Transfer {
    static func loadFile(at url: URL, suggestedName: String? = nil, type: UTType? = nil) throws -> Transfer? {
        guard url.isFileURL else { return nil }
        let scoped = url.startAccessingSecurityScopedResource()
        defer { if scoped { url.stopAccessingSecurityScopedResource() } }

        guard let values = try? url.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey]),
              values.isRegularFile == true else { return nil }
        if let size = values.fileSize, size > Transfer.limit { throw TransferError.tooLarge }

        guard let file = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? file.close() }
        let bytes = try file.read(upToCount: Transfer.limit + 1) ?? Data()
        let name = filename(suggestedName ?? url.lastPathComponent, fallbackURL: url, type: type)
        return try Transfer(kind: .file, name: name, data: bytes)
    }

    private static func filename(_ value: String, fallbackURL: URL, type: UTType?) -> String {
        let component = (value.replacingOccurrences(of: "\\", with: "/") as NSString).lastPathComponent
        let cleanName = component.isEmpty || component == "." || component == ".." ? "Shared file" : component
        let name = cleanName as NSString
        var suffix = name.pathExtension
        if suffix.isEmpty {
            suffix = fallbackURL.pathExtension.isEmpty
                ? (type?.preferredFilenameExtension ?? "")
                : fallbackURL.pathExtension
        }
        let extensionPart = suffix.isEmpty || suffix.utf8.count > 32 ? "" : "." + suffix
        var stem = name.deletingPathExtension
        if stem.isEmpty { stem = "Shared file" }
        while stem.utf8.count + extensionPart.utf8.count > 255 { stem.removeLast() }
        return (stem.isEmpty ? "Shared file" : stem) + extensionPart
    }
}

enum TransferError: LocalizedError {
    case tooLarge, invalid, disconnected, timedOut

    var errorDescription: String? {
        switch self {
        case .tooLarge: "The payload exceeds the 3 MB limit."
        case .invalid: "Pipopu could not read the transfer."
        case .disconnected: "The Bluetooth connection closed or the PC refused the transfer."
        case .timedOut: "The PC stopped responding. Try again with both devices nearby."
        }
    }
}

enum Frame {
    static let begin: UInt8 = 1
    static let chunk: UInt8 = 2
    static let end: UInt8 = 3
    static let received: UInt8 = 4
    static let available: UInt8 = 5
    static let abort: UInt8 = 6

    static func number(_ value: UInt32) -> [UInt8] {
        (0..<4).map { UInt8(truncatingIfNeeded: value >> ($0 * 8)) }
    }

    static func readNumber(_ data: Data, at offset: Int) -> UInt32 {
        (0..<4).reduce(0) { $0 | UInt32(data[offset + $1]) << ($1 * 8) }
    }
}

struct IncomingTransfer {
    let token: UInt32
    let length: Int
    private var bytes = Data()

    init(_ begin: Data) throws {
        guard begin.count == 9 else { throw TransferError.invalid }
        token = Frame.readNumber(begin, at: 1)
        length = Int(Frame.readNumber(begin, at: 5))
        guard (35...Transfer.maximumEnvelope).contains(length) else { throw TransferError.invalid }
    }

    mutating func append(_ frame: Data) throws {
        guard frame.count > 1, bytes.count + frame.count - 1 <= length else { throw TransferError.invalid }
        bytes.append(frame.dropFirst())
    }

    func finish(_ frame: Data) throws -> Transfer {
        guard frame.count == 5, Frame.readNumber(frame, at: 1) == token, bytes.count == length
        else { throw TransferError.invalid }
        return try Transfer(envelope: bytes)
    }
}
