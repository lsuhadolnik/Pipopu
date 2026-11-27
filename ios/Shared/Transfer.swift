import Foundation
import UniformTypeIdentifiers

struct Transfer: Identifiable, Sendable {
    enum Kind: UInt8, Sendable { case text = 1, image, file }
    static let limit = 500_000
    static let textLimit = 3_000_000
    static let fileLimit: Int64 = 5_000_000_000
    let id = UUID()
    let kind: Kind
    let name: String
    let data: Data
    let file: TemporaryFile?
    var size: Int64 { file?.size ?? Int64(data.count) }
    var text: String { String(decoding: data, as: UTF8.self) }

    init(kind: Kind, name: String = "", data: Data, file: TemporaryFile? = nil) throws {
        guard data.count <= (kind == .text ? Self.textLimit : Self.limit), (file?.size ?? 0) <= Self.fileLimit
        else { throw TransferError.tooLarge }
        guard name.utf8.count <= 255, kind != .text || (file == nil && String(data: data, encoding: .utf8) != nil)
        else { throw TransferError.invalid }
        self.kind = kind
        self.name = name
        self.data = data
        self.file = file
    }

    static func importFile(_ url: URL, kind: Kind, name: String) throws -> Transfer {
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        guard let length = attributes[.size] as? NSNumber else { throw TransferError.invalid }
        let size = length.int64Value
        guard size <= fileLimit else { throw TransferError.tooLarge }
        if size <= Int64(limit) {
            let source = try FileHandle(forReadingFrom: url)
            defer { try? source.close() }
            let data = try source.read(upToCount: limit + 1) ?? Data()
            return try Transfer(kind: kind, name: name, data: data)
        }
        let file = TemporaryFile(size: size)
        try FileManager.default.copyItem(at: url, to: file.url)
        return try Transfer(kind: kind, name: name, data: Data(), file: file)
    }
}

extension Transfer {
    static func loadFile(at url: URL, suggestedName: String? = nil, type: UTType? = nil) throws -> Transfer? {
        guard url.isFileURL else { return nil }
        let scoped = url.startAccessingSecurityScopedResource()
        defer { if scoped { url.stopAccessingSecurityScopedResource() } }

        guard let values = try? url.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey]),
              values.isRegularFile == true else { return nil }
        let name = filename(suggestedName ?? url.lastPathComponent, fallbackURL: url, type: type)
        return try Transfer.importFile(url, kind: .file, name: name)
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
    case tooLarge, invalid, disconnected, timedOut, message(String)

    var errorDescription: String? {
        switch self {
        case .tooLarge: "Files must be 5 GB or smaller. Text is limited to 3 MB."
        case .invalid: "Pipopu could not verify the transfer."
        case .disconnected: "The Bluetooth connection closed."
        case .timedOut: "The other device stopped responding."
        case .message(let text): text
        }
    }
}

enum Frame {
    static let begin: UInt8 = 1
    static let chunk: UInt8 = 2
    static let end: UInt8 = 3
    static let maximumPacket = 700_000

    static func number(_ value: UInt32) -> [UInt8] {
        (0..<4).map { UInt8(truncatingIfNeeded: value >> ($0 * 8)) }
    }

    static func readNumber(_ data: Data, at offset: Int) -> UInt32 {
        (0..<4).reduce(0) { $0 | UInt32(data[offset + $1]) << ($1 * 8) }
    }
}

struct IncomingPacket {
    let token: UInt32
    let length: Int
    private var bytes = Data()

    init(_ begin: Data) throws {
        guard begin.count == 9 else { throw TransferError.invalid }
        token = Frame.readNumber(begin, at: 1)
        length = Int(Frame.readNumber(begin, at: 5))
        guard (1...Frame.maximumPacket).contains(length) else { throw TransferError.invalid }
    }

    mutating func append(_ frame: Data) throws {
        guard frame.count > 1, bytes.count + frame.count - 1 <= length else { throw TransferError.invalid }
        bytes.append(frame.dropFirst())
    }

    func finish(_ frame: Data) throws -> Data {
        guard frame.count == 5, Frame.readNumber(frame, at: 1) == token, bytes.count == length
        else { throw TransferError.invalid }
        return bytes
    }
}
