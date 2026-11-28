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
    let secure: Bool
    let replay: Bool
    var size: Int64 { file?.size ?? Int64(data.count) }
    var text: String { String(decoding: data, as: UTF8.self) }

    init(kind: Kind, name: String = "", data: Data, file: TemporaryFile? = nil, secure: Bool = false,
         replay: Bool = false) throws {
        guard data.count <= (kind == .text ? Self.textLimit : Self.limit), (file?.size ?? 0) <= Self.fileLimit
        else { throw TransferError.tooLarge }
        guard name.utf8.count <= 255, kind != .text || (file == nil && String(data: data, encoding: .utf8) != nil),
              !secure || (kind == .text && data.count <= Self.limit)
        else { throw TransferError.invalid }
        self.kind = kind
        self.name = name
        self.data = data
        self.file = file
        self.secure = secure
        self.replay = replay
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

enum CloudArchive {
    private struct Item: Codable {
        let name: String
        let kind: UInt8
        let size: Int64
    }

    static func contains(_ id: String) -> Bool {
        guard let url = try? itemDirectory(id) else { return false }
        return FileManager.default.fileExists(atPath: url.appendingPathComponent("item.json").path)
    }

    static func save(_ decoded: TemporaryFile, id: String, kind: Transfer.Kind, name: String) throws -> Transfer {
        let folder = try itemDirectory(id)
        let files = FileManager.default
        try files.createDirectory(at: folder, withIntermediateDirectories: true)
        let payload = folder.appendingPathComponent("payload")
        let metadata = folder.appendingPathComponent("item.json")
        if !files.fileExists(atPath: metadata.path) {
            if files.fileExists(atPath: payload.path) { try files.removeItem(at: payload) }
            try files.moveItem(at: decoded.url, to: payload)
            try JSONEncoder().encode(Item(name: name, kind: kind.rawValue, size: decoded.size))
                .write(to: metadata, options: .atomic)
        }
        return try open(id, replay: false)
    }

    static func open(_ id: String, replay: Bool = true) throws -> Transfer {
        let folder = try itemDirectory(id)
        let item = try JSONDecoder().decode(Item.self, from: Data(contentsOf: folder.appendingPathComponent("item.json")))
        guard let kind = Transfer.Kind(rawValue: item.kind) else { throw TransferError.invalid }
        let payload = folder.appendingPathComponent("payload")
        if kind == .text {
            return try Transfer(kind: kind, name: item.name, data: Data(contentsOf: payload), replay: replay)
        }
        return try Transfer(kind: kind, name: item.name, data: Data(),
                            file: TemporaryFile(url: payload, size: item.size), replay: replay)
    }

    static func valid(_ id: String) -> Bool {
        id.utf8.count == 32 && id.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) }
    }

    private static func itemDirectory(_ id: String) throws -> URL {
        guard valid(id) else { throw TransferError.invalid }
        let support = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask,
                                                  appropriateFor: nil, create: true)
        return support.appendingPathComponent("Pipopu Cloud", isDirectory: true).appendingPathComponent(id, isDirectory: true)
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
