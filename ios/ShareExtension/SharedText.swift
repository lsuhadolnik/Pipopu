import Foundation
import UIKit
import UniformTypeIdentifiers

enum SharedText {
    static func load(from items: [NSExtensionItem]) async throws -> Transfer {
        for item in items {
            for provider in item.attachments ?? [] {
                let types = provider.registeredTypeIdentifiers.compactMap { UTType($0) }
                for type in types where isSupported(type) {
                    if let text = try await read(provider, as: type), !text.isEmpty {
                        return try Transfer(kind: .text, data: Data(text.utf8))
                    }
                }
            }
        }
        for item in items {
            if let text = item.attributedContentText?.string, !text.isEmpty {
                return try Transfer(kind: .text, data: Data(text.utf8))
            }
        }
        throw TransferError.invalid
    }

    private static func isSupported(_ type: UTType) -> Bool {
        if type.conforms(to: .fileURL) { return false }
        return type == .text || type.conforms(to: .plainText) ||
            type.conforms(to: .url) || documentType(for: type) != nil
    }

    private static func read(_ provider: NSItemProvider, as type: UTType) async throws -> String? {
        if type.conforms(to: .url) {
            let url: URL? = await withCheckedContinuation { continuation in
                _ = provider.loadObject(ofClass: NSURL.self) { object, _ in
                    continuation.resume(returning: object as? URL)
                }
            }
            if let url, !url.isFileURL { return url.absoluteString }
        } else {
            let data: Data? = await withCheckedContinuation { continuation in
                _ = provider.loadDataRepresentation(for: type) { bytes, _ in
                    continuation.resume(returning: bytes)
                }
            }
            if let data, let text = try decode(data, as: type), !text.isEmpty { return text }
        }

        // Some host apps vouch for a text type but vend a file URL or attributed string.
        // Read the original object while the provider's temporary file is still accessible.
        return try await withCheckedThrowingContinuation { continuation in
            provider.loadItem(forTypeIdentifier: type.identifier, options: nil) { object, _ in
                continuation.resume(with: Result {
                    switch object {
                    case let text as String:
                        return text
                    case let text as NSAttributedString:
                        return text.string
                    case let data as Data:
                        return try decode(data, as: type)
                    case let url as URL where type.conforms(to: .url):
                        return url.isFileURL ? nil : url.absoluteString
                    case let url as URL where url.isFileURL:
                        guard let file = try? FileHandle(forReadingFrom: url) else { return nil }
                        defer { try? file.close() }
                        guard let data = try? file.read(upToCount: Transfer.limit + 1) else { return nil }
                        return try decode(data, as: type)
                    default:
                        return nil
                    }
                })
            }
        }
    }

    private static func decode(_ data: Data, as type: UTType) throws -> String? {
        guard data.count <= Transfer.limit else { throw TransferError.tooLarge }
        if let format = documentType(for: type) {
            return try? NSAttributedString(data: data, options: [.documentType: format], documentAttributes: nil).string
        }
        if data.starts(with: [0xFF, 0xFE]) || data.starts(with: [0xFE, 0xFF]) ||
            type.conforms(to: .utf16ExternalPlainText) {
            return String(data: data, encoding: .utf16)
        }
        if type.conforms(to: .utf16PlainText) {
            return String(data: data, encoding: .utf16LittleEndian)
        }
        return String(data: data, encoding: .utf8)
    }

    private static func documentType(for type: UTType) -> NSAttributedString.DocumentType? {
        if type.conforms(to: .rtf) { return .rtf }
        if type.conforms(to: .rtfd) || type.conforms(to: .flatRTFD) { return .rtfd }
        return nil
    }
}
