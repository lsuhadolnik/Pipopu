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
                            return try Transfer.loadFile(at: url, suggestedName: suggestedName, type: type)
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
                    return try Transfer.loadFile(at: url, suggestedName: nil, type: nil)
                })
            }
        }
    }
}
