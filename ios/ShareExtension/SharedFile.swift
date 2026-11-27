import Foundation
import UniformTypeIdentifiers

enum SharedFile {
    static func load(from providers: [NSItemProvider]) async throws -> Transfer? {
        for provider in providers {
            if provider.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier),
               let transfer = try await loadFileURL(from: provider) {
                return transfer
            }

            let types = provider.registeredTypeIdentifiers.compactMap { UTType($0) }
            let suggestedName = provider.suggestedName
            let suggestedType = suggestedName
                .flatMap { UTType(filenameExtension: ($0 as NSString).pathExtension) }
            let containsTextOrLink = types.contains { $0.conforms(to: .text) || $0.conforms(to: .url) }

            for type in types where !type.conforms(to: .url) && !type.conforms(to: .directory) {
                let matchesFilename = suggestedType.map { $0.conforms(to: type) } ?? false
                guard matchesFilename || (!containsTextOrLink && type.conforms(to: .data)) else { continue }

                let transfer: Transfer? = try await withCheckedThrowingContinuation { continuation in
                    provider.loadFileRepresentation(forTypeIdentifier: type.identifier) { url, _ in
                        continuation.resume(with: Result {
                            guard let url else { return nil }
                            return try read(url, suggestedName: suggestedName, type: type)
                        })
                    }
                }
                if let transfer { return transfer }
            }
        }
        return nil
    }

    private static func loadFileURL(from provider: NSItemProvider) async throws -> Transfer? {
        try await withCheckedThrowingContinuation { continuation in
            provider.loadItem(forTypeIdentifier: UTType.fileURL.identifier, options: nil) { item, _ in
                continuation.resume(with: Result {
                    let url: URL?
                    switch item {
                    case let value as URL: url = value
                    case let bytes as Data: url = URL(dataRepresentation: bytes, relativeTo: nil)
                    case let value as String: url = URL(string: value)
                    default: url = nil
                    }
                    guard let url else { return nil }
                    return try read(url, suggestedName: nil, type: nil)
                })
            }
        }
    }

    private static func read(_ url: URL, suggestedName: String?, type: UTType?) throws -> Transfer? {
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
