import Foundation

struct ActivityItem: Codable, Identifiable, Sendable {
    let id: String
    let device: String
    let deviceName: String
    let date: Date
    let kind: UInt8
    let name: String
    let size: Int64
    let secure: Bool
    let outgoing: Bool
    let queued: Bool

    init(_ event: Bluetooth.ActivityEvent) {
        id = event.id
        device = event.device
        deviceName = event.deviceName
        date = Date()
        kind = event.transfer.kind.rawValue
        name = event.transfer.name
        size = event.transfer.size
        secure = event.transfer.secure
        outgoing = event.outgoing
        queued = event.queued
    }

    var isText: Bool { kind == Transfer.Kind.text.rawValue }
}

enum ActivityArchive {
    private static func directory() throws -> URL {
        let files = FileManager.default
        let support = try files.url(for: .applicationSupportDirectory, in: .userDomainMask,
                                    appropriateFor: nil, create: true)
        return support.appendingPathComponent("Activity", isDirectory: true)
    }

    private static func folder(_ id: String) throws -> URL {
        guard id.utf8.count == 32, id.allSatisfy(\.isHexDigit) else { throw TransferError.invalid }
        return try directory().appendingPathComponent(id, isDirectory: true)
    }

    private static func readItem(_ folder: URL) throws -> ActivityItem {
        try JSONDecoder().decode(ActivityItem.self, from: Data(contentsOf: folder.appendingPathComponent("item.json")))
    }

    static func items() -> [ActivityItem] {
        guard let root = try? directory(),
              let entries = try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil,
                    options: [.skipsHiddenFiles]) else { return [] }
        return entries.compactMap { entry in
            guard let item = try? readItem(entry), item.id == entry.lastPathComponent else { return nil }
            return item
        }
    }

    static func save(_ item: ActivityItem, transfer: Transfer) throws -> ActivityItem {
        let files = FileManager.default
        let destination = try folder(item.id)
        if let existing = try? readItem(destination) { return existing }
        var root = try directory()
        let protection: [FileAttributeKey: Any] = [.protectionKey: FileProtectionType.complete]
        try files.createDirectory(at: root, withIntermediateDirectories: true, attributes: protection)
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try root.setResourceValues(values)

        let staging = root.appendingPathComponent("." + UUID().uuidString, isDirectory: true)
        try files.createDirectory(at: staging, withIntermediateDirectories: false, attributes: protection)
        defer { try? files.removeItem(at: staging) }
        let payload = staging.appendingPathComponent("payload")
        if let file = transfer.file {
            try files.copyItem(at: file.url, to: payload)
            try files.setAttributes(protection, ofItemAtPath: payload.path)
        } else {
            try transfer.data.write(to: payload, options: .completeFileProtection)
        }
        try JSONEncoder().encode(item).write(to: staging.appendingPathComponent("item.json"), options: .completeFileProtection)
        do { try files.moveItem(at: staging, to: destination) }
        catch {
            if let existing = try? readItem(destination) { return existing }
            throw error
        }
        return item
    }

    static func file(_ item: ActivityItem) throws -> TemporaryFile {
        let payload = try folder(item.id).appendingPathComponent("payload")
        return TemporaryFile(url: payload, size: item.size)
    }

    static func remove(_ item: ActivityItem) throws {
        try FileManager.default.removeItem(at: folder(item.id))
    }
}
