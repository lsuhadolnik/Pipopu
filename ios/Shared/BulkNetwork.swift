import Foundation
import Network

private final class LocalNetworkPath: @unchecked Sendable {
    static let shared = LocalNetworkPath()
    private let monitor = NWPathMonitor()
    private let lock = NSLock()
    private var available: Bool?

    private init() {
        monitor.pathUpdateHandler = { [weak self] path in
            guard let self else { return }
            let local = path.usesInterfaceType(.wifi) || path.usesInterfaceType(.wiredEthernet) ||
                path.availableInterfaces.contains { $0.type == .wifi || $0.type == .wiredEthernet }
            lock.withLock { self.available = local }
        }
        monitor.start(queue: DispatchQueue(label: "Pipopu.LocalNetworkPath"))
    }

    var canTryLocalNetwork: Bool { lock.withLock { available != false } }
}

private final class TransferRoutes: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    let cloud: Bool
    let progress: (@MainActor (Double) -> Void)?

    init(cloud: Bool, progress: (@MainActor (Double) -> Void)?) {
        self.cloud = cloud
        self.progress = progress
    }

    static func valid(_ url: URL, cloud: Bool) -> Bool {
        guard url.user == nil, url.password == nil, let host = url.host?.lowercased() else { return false }
        if cloud {
            let domains = ["1drv.com", "onedrive.com", "sharepoint.com", "sharepoint-df.com", "live.com",
                           "microsoftpersonalcontent.com"]
            return url.scheme == "https" && (url.port == nil || url.port == 443) &&
                domains.contains { host == $0 || host.hasSuffix("." + $0) }
        }
        let parts = host.split(separator: ".", omittingEmptySubsequences: false)
        let ip = parts.compactMap { UInt8($0) }
        guard url.scheme == "http", url.port != nil, ip.count == 4, parts.count == 4,
              url.path.count == 65, url.path.dropFirst().allSatisfy({ $0.isHexDigit }) else { return false }
        return ip[0] == 10 || (ip[0] == 172 && (16...31).contains(ip[1])) ||
            (ip[0] == 192 && ip[1] == 168) || (ip[0] == 169 && ip[1] == 254) ||
            (ip[0] == 100 && (64...127).contains(ip[1]))
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(cloud && request.url.map { Self.valid($0, cloud: true) } == true ? request : nil)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didSendBodyData bytesSent: Int64,
                    totalBytesSent: Int64, totalBytesExpectedToSend: Int64) {
        guard let progress, totalBytesExpectedToSend > 0 else { return }
        let fraction = min(1, Double(totalBytesSent) / Double(totalBytesExpectedToSend))
        Task { @MainActor in progress(fraction) }
    }
}

enum BulkNetwork {
    static func startMonitoringLocalNetwork() { _ = LocalNetworkPath.shared }

    private static func session(cloud: Bool, timeout: TimeInterval = 30,
                                progress: (@MainActor (Double) -> Void)? = nil) -> URLSession {
        let options = URLSessionConfiguration.ephemeral
        options.timeoutIntervalForRequest = timeout
        options.timeoutIntervalForResource = 7200
        options.httpShouldSetCookies = false
        options.urlCache = nil
        return URLSession(configuration: options, delegate: TransferRoutes(cloud: cloud, progress: progress), delegateQueue: nil)
    }

    static func localURL(_ candidates: [String]) async throws -> URL {
        guard LocalNetworkPath.shared.canTryLocalNetwork else {
            throw TransferError.message("Connect this device to Wi-Fi or Ethernet to use local transfers.")
        }
        let probe = session(cloud: false, timeout: 2.5)
        defer { probe.finishTasksAndInvalidate() }
        for item in candidates.prefix(8) {
            try Task.checkCancellation()
            guard let url = URL(string: item), TransferRoutes.valid(url, cloud: false) else { continue }
            var request = URLRequest(url: url)
            request.httpMethod = "HEAD"
            if let (_, response) = try? await probe.data(for: request), status(response) == 200 { return url }
        }
        throw TransferError.message("The receiving device could not be reached on the local network.")
    }

    static func cloudURL(_ route: Wire, forUpload: Bool = false) throws -> URL {
        let purpose = forUpload ? "upload" : "download"
        guard let location = route.urls?.first else {
            throw TransferError.message("The other device did not provide a OneDrive \(purpose) link.")
        }
        guard let url = URL(string: location), url.host != nil else {
            throw TransferError.message("The other device provided an incomplete OneDrive \(purpose) link.")
        }
        guard TransferRoutes.valid(url, cloud: true) else {
            throw TransferError.message("Pipopu could not validate the OneDrive \(purpose) address (\(url.host ?? "unknown host")). Update Pipopu on both devices and try again.")
        }
        return url
    }

    static func download(_ url: URL, cloud: Bool, size: Int64) async throws -> TemporaryFile {
        let client = session(cloud: cloud)
        defer { client.finishTasksAndInvalidate() }
        let (source, response) = try await client.download(from: url)
        defer { try? FileManager.default.removeItem(at: source) }
        guard status(response) == 200 else {
            throw TransferError.message("The file download returned HTTP \(status(response)).")
        }
        guard let bytes = try FileManager.default.attributesOfItem(atPath: source.path)[.size] as? NSNumber,
              bytes.int64Value == size else {
            throw TransferError.message("The downloaded file size did not match the transfer offer.")
        }
        let file = TemporaryFile(size: size)
        try FileManager.default.moveItem(at: source, to: file.url)
        return file
    }

    static func uploadLocal(_ url: URL, file: TemporaryFile, progress: @escaping @MainActor (Double) -> Void) async throws {
        let client = session(cloud: false, progress: progress)
        defer { client.finishTasksAndInvalidate() }
        var request = URLRequest(url: url)
        request.httpMethod = "PUT"
        request.setValue(String(file.size), forHTTPHeaderField: "Content-Length")
        let (_, response) = try await client.upload(for: request, fromFile: file.url)
        guard status(response) == 204 else {
            throw TransferError.message("The receiving device rejected the local upload (HTTP \(status(response))).")
        }
    }

    static func uploadCloud(_ url: URL, file: TemporaryFile, progress: @escaping @MainActor (Double) -> Void) async throws {
        let client = session(cloud: true)
        defer { client.finishTasksAndInvalidate() }
        let source = try FileHandle(forReadingFrom: file.url)
        defer { try? source.close() }
        var offset: Int64 = 0
        while offset < file.size {
            try Task.checkCancellation()
            let count = Int(min(10 * 320 * 1024, file.size - offset))
            let bytes = try FileCipher.readExact(source, count: count)
            let end = offset + Int64(count)
            var request = URLRequest(url: url)
            request.httpMethod = "PUT"
            request.setValue("bytes \(offset)-\(end - 1)/\(file.size)", forHTTPHeaderField: "Content-Range")
            request.setValue(String(count), forHTTPHeaderField: "Content-Length")
            let start = offset
            let observer = TransferRoutes(cloud: true) { fraction in
                progress((Double(start) + Double(count) * fraction) / Double(file.size))
            }
            let response: URLResponse
            do {
                let (_, reply) = try await client.upload(for: request, from: bytes, delegate: observer)
                response = reply
            } catch let error as URLError where error.code != .cancelled {
                let reason: String
                switch error.code {
                case .timedOut: reason = "OneDrive stopped responding."
                case .notConnectedToInternet, .networkConnectionLost: reason = "The internet connection dropped during the OneDrive upload."
                case .cannotFindHost, .cannotConnectToHost, .dnsLookupFailed: reason = "The iPhone or iPad could not reach OneDrive."
                case .secureConnectionFailed, .serverCertificateUntrusted:
                    reason = "The connection to OneDrive could not be authenticated."
                default: reason = "The connection to OneDrive failed (network error \(error.errorCode))."
                }
                throw TransferError.message(reason)
            }
            let code = status(response)
            guard end < file.size ? code == 202 : code == 200 || code == 201 else {
                let reason: String
                switch code {
                case 401, 403: reason = "OneDrive refused access to this upload link."
                case 404, 410: reason = "The OneDrive upload session is missing or has expired."
                case 429: reason = "OneDrive is limiting upload requests. Try again shortly."
                case 507: reason = "The receiving OneDrive account is out of storage."
                default: reason = "OneDrive rejected part of the file."
                }
                throw TransferError.message("\(reason) (HTTP \(code))")
            }
            offset = end
            await progress(Double(offset) / Double(file.size))
        }
    }

    private static func status(_ response: URLResponse) -> Int { (response as? HTTPURLResponse)?.statusCode ?? 0 }
}
