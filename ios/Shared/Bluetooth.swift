import Combine
import CoreBluetooth
import CryptoKit
import Foundation
import Security
import UIKit

private let serviceID = CBUUID(string: "EA259A40-8B6E-44DA-BEEC-E10D5680D630")
private let uploadID = CBUUID(string: "EA259A41-8B6E-44DA-BEEC-E10D5680D630")
private let downloadID = CBUUID(string: "EA259A42-8B6E-44DA-BEEC-E10D5680D630")
private let nameID = CBUUID(string: "EA259A43-8B6E-44DA-BEEC-E10D5680D630")

@MainActor
final class Bluetooth: NSObject, ObservableObject {
    struct Peer: Identifiable {
        let id: UUID
        var name: String
        var connected = false
        var supportsCloudAndSecure = false
    }

    struct Pairing {
        let name: String
        let code: String
    }

    enum SendStage: Equatable {
        case requestingTransfer, pairing, waitingForDevice, encryptingFile, requestingLink
        case checkingLocalNetwork, requestingOneDriveLink, checkingOneDriveLink, oneDriveSetup
        case bluetooth, localUpload, oneDriveUpload, confirming

        var label: String {
            switch self {
            case .requestingTransfer: "Requesting transfer…"
            case .pairing: "Confirm pairing"
            case .waitingForDevice: "Waiting for the receiving device…"
            case .encryptingFile: "Encrypting file…"
            case .requestingLink: "Waiting for a transfer route…"
            case .checkingLocalNetwork: "Connecting over the local network…"
            case .requestingOneDriveLink: "Requesting a OneDrive upload link…"
            case .checkingOneDriveLink: "Checking the OneDrive upload link…"
            case .oneDriveSetup: "OneDrive setup required"
            case .bluetooth: "Sending over Bluetooth…"
            case .localUpload: "Uploading over the local network…"
            case .oneDriveUpload: "Uploading to OneDrive…"
            case .confirming: "Waiting for delivery confirmation…"
            }
        }
    }

    private static let deliveryConfirmationPoint = 0.95

    private final class Write {
        let packet: Data
        let token = UInt32.random(in: .min ... .max)
        let continuation: CheckedContinuation<Void, Error>
        let progress: ((Double) -> Void)?
        var offset = 0
        var started = false
        var ended = false

        init(_ packet: Data, continuation: CheckedContinuation<Void, Error>, progress: ((Double) -> Void)?) {
            self.packet = packet
            self.continuation = continuation
            self.progress = progress
        }
    }

    private final class Transaction {
        let id: String
        let incoming: AsyncThrowingStream<Wire, Error>.Continuation
        private var iterator: AsyncThrowingStream<Wire, Error>.Iterator
        var deadline = Date().addingTimeInterval(120)
        var peerError: String?
        var requiresOneDriveSetup = false
        var cancelledByUser = false
        var detached = false

        init(id: String) {
            self.id = id
            let stream = AsyncThrowingStream<Wire, Error>.makeStream(bufferingPolicy: .bufferingOldest(8))
            incoming = stream.continuation
            iterator = stream.stream.makeAsyncIterator()
        }

        func next() async throws -> Wire {
            var cursor = iterator
            let message = try await cursor.next()
            iterator = cursor
            guard let message else { throw TransferError.disconnected }
            return message
        }
    }

    private final class Link {
        let peripheral: CBPeripheral
        var peer: Peer
        var lastSeen = Date()
        var connectingSince: Date?
        var upload: CBCharacteristic?
        var download: CBCharacteristic?
        var incoming: IncomingPacket?
        var incomingAt = Date.distantPast
        var hello: Data?
        var session: SecureSession?
        var version: Int?
        var writes: [Write] = []
        var writeAt = Date.distantPast
        var nextNameRefresh = Date.distantPast
        var transfer: Transaction?
        var task: Task<Void, Never>?

        init(_ peripheral: CBPeripheral, name: String) {
            self.peripheral = peripheral
            peer = Peer(id: peripheral.identifier, name: name)
        }
    }

    @Published private(set) var peers = [Peer]()
    @Published private(set) var state = "Starting Bluetooth…"
    @Published private(set) var progress: Double?
    @Published private(set) var sendStage: SendStage?
    @Published private(set) var sendFailureStage: SendStage?
    @Published private(set) var sendQueued = false
    @Published private var uploadBytesPerSecond: Double?
    @Published private var uploadedFraction = 0.0
    @Published private(set) var result: String?
    @Published private(set) var received: Transfer?
    @Published private(set) var receiveError: String?
    @Published private(set) var sendCompleted = false
    @Published private(set) var pairing: Pairing?
    @Published private(set) var pairingCode: String?
    @Published private(set) var trustedDevices = TrustStore.all()
    @Published private(set) var cloudNotices: [CloudNotice] = []
    @Published private(set) var cloudStatus: String?
    private var cloudByPeer: [String: [CloudNotice]] = {
        guard let data = UserDefaults.standard.data(forKey: "PipopuCloudNotices") else { return [:] }
        return (try? JSONDecoder().decode([String: [CloudNotice]].self, from: data)) ?? [:]
    }()
    private var downloadRequest: (id: String, peer: UUID)?
    private var downloadRequestedAt = Date.distantPast
    private var downloadingCloudFile = false

    var sendFraction: Double? {
        switch sendStage {
        case .bluetooth, .localUpload, .oneDriveUpload:
            return min(1, (progress ?? 0) / Self.deliveryConfirmationPoint)
        default:
            return nil
        }
    }

    var uploadSpeedLabel: String? {
        guard sendStage == .localUpload || sendStage == .oneDriveUpload else { return nil }
        return uploadBytesPerSecond.map {
            ByteCountFormatter.string(fromByteCount: Int64($0), countStyle: .binary) + "/s"
        } ?? "Measuring speed…"
    }

    func uploadByteLabel(total: Int64) -> String? {
        guard sendStage == .localUpload || sendStage == .oneDriveUpload else { return nil }
        let transferred = Int64(Double(total) * uploadedFraction)
        let counter = ByteCountFormatter()
        counter.countStyle = .file
        counter.allowedUnits = total >= 1_000_000_000 ? .useGB : (total >= 1_000_000 ? .useMB : .useKB)
        counter.allowsNonnumericFormatting = false
        return "\(counter.string(fromByteCount: transferred)) / \(counter.string(fromByteCount: total))"
    }

    private var central: CBCentralManager?
    private var links = [UUID: Link]()
    private var timer: Timer?
    private var receivingName: String?
    private var identity: P256.KeyAgreement.PrivateKey?
    private var sender: UUID?
    private var sendTask: Task<Void, Never>?
    private var detachedSend: Transaction?
    private var uploadSample: (time: TimeInterval, bytes: Double)?
    private var confirmation: (Link, CheckedContinuation<Bool, Never>)?

    func start(receivingAs name: String? = nil) {
        guard central == nil else { return }
        BulkNetwork.startMonitoringLocalNetwork()
        do { identity = try TrustStore.identity() }
        catch { state = error.localizedDescription; return }
        receivingName = name
        trustedDevices = TrustStore.all()
        publishCloudNotices()
        central = CBCentralManager(delegate: self, queue: .main)
        timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
    }

    func stop() {
        timer?.invalidate()
        timer = nil
        central?.stopScan()
        for link in Array(links.values) {
            disconnect(link)
            central?.cancelPeripheralConnection(link.peripheral)
        }
        central?.delegate = nil
        central = nil
        links.removeAll()
        peers = []
        pairingCode = nil
    }

    func clearReceived() { received = nil }

    func updateReceivingName(_ name: String?) {
        guard receivingName != name else { return }
        receivingName = name
        for link in links.values { link.nextNameRefresh = .distantPast }
        tick()
    }

    func forget(_ peer: PeerTrust) {
        do {
            try TrustStore.forget(peer.id)
            trustedDevices = TrustStore.all()
            cloudByPeer[peer.id] = nil
            publishCloudNotices()
            for link in links.values where link.session?.identity == peer.id && link.transfer != nil {
                central?.cancelPeripheralConnection(link.peripheral)
            }
        } catch { result = error.localizedDescription }
    }

    func answerPairing(_ accepted: Bool) {
        let pending = confirmation
        confirmation = nil
        pairing = nil
        pending?.1.resume(returning: accepted)
    }

    func send(_ transfer: Transfer, to id: UUID, overCloud: Bool = false, onFinish: @escaping () -> Void) {
        guard sender == nil else { return }
        sendCompleted = false
        sendQueued = false
        sendFailureStage = nil
        guard let link = links[id], link.peer.connected, link.transfer == nil,
              let session = link.session else {
            result = "The selected device is disconnected or busy."
            onFinish()
            return
        }
        if (transfer.secure || overCloud) && !link.peer.supportsCloudAndSecure {
            result = "Update Pipopu on the receiving device to use this send option."
            onFinish()
            return
        }
        sender = id
        sendCompleted = false
        result = nil
        progress = 0
        sendStage = .requestingTransfer
        let transaction = Transaction(id: UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased())
        link.transfer = transaction
        let task = Task { [weak self] in
            guard let self else { return }
            defer {
                finish(link, transaction)
                sender = nil
                detachedSend = nil
                sendTask = nil
                progress = nil
                uploadBytesPerSecond = nil
                uploadSample = nil
                sendStage = nil
                pairingCode = nil
                onFinish()
            }
            do {
                let pair = !TrustStore.contains(session.identity)
                let offer = Wire(type: "offer", id: transaction.id, name: transfer.name, kind: Int(transfer.kind.rawValue),
                                 size: transfer.size, pair: pair, route: overCloud ? "cloud-deferred" : nil,
                                 secure: transfer.secure)
                try await transmit(offer, on: link)
                sendStage = .waitingForDevice
                let review = try await transaction.next()
                try expect(review, "review")
                if pair || review.pair == true {
                    sendStage = .pairing
                    pairingCode = session.code
                    try await confirmTrust(link, session: session)
                }
                sendStage = .waitingForDevice
                try expect(await transaction.next(), "accept")
                pairingCode = nil
                try await transmit(Wire(type: "verified", id: transaction.id), on: link)
                if pair || review.pair == true {
                    try TrustStore.remember(session.identity, name: link.peer.name)
                    trustedDevices = TrustStore.all()
                }
                transaction.deadline = Date().addingTimeInterval(7200)
                if overCloud || transfer.size > Int64(Transfer.limit) {
                    if transfer.size == 0 { throw TransferError.message("Choose a nonempty file or message for cloud delivery.") }
                    let file: TemporaryFile
                    if let existing = transfer.file {
                        file = existing
                    } else {
                        file = TemporaryFile(size: transfer.size)
                        try transfer.data.write(to: file.url)
                    }
                    let cloudKey = overCloud ? SymmetricKey(size: .bits256) : nil
                    var cloudUploadURL: URL?
                    var route: (url: URL, cloud: Bool)?
                    if let cloudKey {
                        sendStage = .requestingOneDriveLink
                        transaction.deadline = Date().addingTimeInterval(65)
                        let key = cloudKey.withUnsafeBytes { Data($0) }
                        try await transmit(Wire(type: "cloud-key", id: transaction.id, data: key), on: link)
                        let ticket = try await transaction.next()
                        try expect(ticket, "cloud-upload")
                        detachedSend = transaction
                        finish(link, transaction)
                        sendStage = .checkingOneDriveLink
                        cloudUploadURL = try BulkNetwork.cloudURL(ticket, forUpload: true)
                    } else {
                        sendStage = .requestingLink
                        let offeredRoute = try await transaction.next()
                        route = try await prepareUploadRoute(offeredRoute, link: link, transaction: transaction)
                    }
                    sendStage = .encryptingFile
                    let input = try FileHandle(forReadingFrom: file.url)
                    defer { try? input.close() }
                    let encrypted = try await FileCipher.transform(input: input, size: transfer.size,
                        key: cloudKey ?? session.key(for: transaction.id), encrypt: true)
                    if let cloudUploadURL {
                        beginUpload(.oneDriveUpload)
                        try await BulkNetwork.uploadCloud(cloudUploadURL, file: encrypted) {
                            [weak self] value in
                            guard let self, detachedSend === transaction, sendStage == .oneDriveUpload else { return }
                            updateUploadProgress(value, size: encrypted.size)
                        }
                        sendCompleted = true
                        sendQueued = true
                        result = "\(link.peer.name) can collect the file when it’s back online."
                        refreshCloud()
                        return
                    }
                    guard let route else { throw TransferError.message("No upload route was available.") }
                    try await upload(encrypted, route: route, link: link, transaction: transaction)
                    sendStage = .confirming
                    try await transmit(Wire(type: "uploaded", id: transaction.id), on: link)
                } else {
                    sendStage = .bluetooth
                    try await transmit(Wire(type: "data", id: transaction.id, data: transfer.data), on: link) {
                        [weak self] value in self?.progress = value * Self.deliveryConfirmationPoint
                    }
                    sendStage = .confirming
                }
                try expect(await transaction.next(), "complete")
                sendCompleted = true
                result = "Sent"
            } catch {
                sendFailureStage = transaction.requiresOneDriveSetup ? .oneDriveSetup : sendStage
                result = transaction.cancelledByUser ? "Transfer canceled." : (transaction.peerError ?? error.localizedDescription)
                if transaction.peerError == nil && (!Task.isCancelled || transaction.detached) && link.peer.connected {
                    if transaction.detached {
                        let message = Wire(type: "cloud-upload-error", id: transaction.id, error: result)
                        Task { [weak self] in try? await self?.transmit(message, on: link) }
                    } else {
                        try? await transmit(Wire(type: "cancel", id: transaction.id), on: link)
                    }
                }
            }
        }
        link.task = task
        sendTask = task
    }

    func cancelSend() {
        if let detachedSend {
            detachedSend.cancelledByUser = true
            sendTask?.cancel()
            return
        }
        guard let sender, let link = links[sender], let transaction = link.transfer else { return }
        transaction.cancelledByUser = true
        disconnect(link)
        central?.cancelPeripheralConnection(link.peripheral)
        publishPeers()
    }

    private func prepareUploadRoute(_ route: Wire, link: Link, transaction: Transaction) async throws
        -> (url: URL, cloud: Bool) {
        try expect(route, "route")
        if route.route == "lan" {
            sendStage = .checkingLocalNetwork
            do {
                return (try await BulkNetwork.localURL(route.urls ?? []), false)
            } catch {
                try Task.checkCancellation()
                return (try await requestCloudUploadLink(link: link, transaction: transaction), true)
            }
        }
        guard route.route == "cloud" else {
            throw TransferError.message("The receiving device offered an unsupported transfer route.")
        }
        sendStage = .checkingOneDriveLink
        return (try BulkNetwork.cloudURL(route, forUpload: true), true)
    }

    private func requestCloudUploadLink(link: Link, transaction: Transaction) async throws -> URL {
        sendStage = .requestingOneDriveLink
        transaction.deadline = Date().addingTimeInterval(65)
        uploadBytesPerSecond = nil
        progress = 0
        try await transmit(Wire(type: "fallback", id: transaction.id), on: link)
        let next = try await transaction.next()
        try expect(next, "route")
        guard next.route == "cloud" else {
            throw TransferError.message("The receiving device replied without a OneDrive upload route.")
        }
        transaction.deadline = Date().addingTimeInterval(7200)
        sendStage = .checkingOneDriveLink
        return try BulkNetwork.cloudURL(next, forUpload: true)
    }

    private func upload(_ encrypted: TemporaryFile, route: (url: URL, cloud: Bool),
                        link: Link, transaction: Transaction) async throws {
        if route.cloud {
            try await uploadToOneDrive(encrypted, url: route.url, link: link, transaction: transaction)
        } else {
            do {
                beginUpload(.localUpload)
                try await BulkNetwork.uploadLocal(route.url, file: encrypted) { [weak self] value in
                    guard let self, link.transfer === transaction, sendStage == .localUpload else { return }
                    updateUploadProgress(value, size: encrypted.size)
                }
                progress = Self.deliveryConfirmationPoint
            } catch {
                try Task.checkCancellation()
                let url = try await requestCloudUploadLink(link: link, transaction: transaction)
                try await uploadToOneDrive(encrypted, url: url, link: link, transaction: transaction)
            }
        }
    }

    private func uploadToOneDrive(_ encrypted: TemporaryFile, url: URL, link: Link, transaction: Transaction) async throws {
        beginUpload(.oneDriveUpload)
        try await BulkNetwork.uploadCloud(url, file: encrypted) { [weak self] value in
            guard let self, link.transfer === transaction, sendStage == .oneDriveUpload else { return }
            updateUploadProgress(value, size: encrypted.size)
        }
    }

    private func beginUpload(_ stage: SendStage) {
        sendStage = stage
        uploadBytesPerSecond = nil
        uploadedFraction = 0
        uploadSample = (ProcessInfo.processInfo.systemUptime, 0)
    }

    private func updateUploadProgress(_ value: Double, size: Int64) {
        uploadedFraction = max(uploadedFraction, min(1, max(0, value)))
        progress = max(progress ?? 0, uploadedFraction * Self.deliveryConfirmationPoint)
        let now = ProcessInfo.processInfo.systemUptime
        let bytes = min(1, max(0, value)) * Double(size)
        if let sample = uploadSample, bytes > sample.bytes,
           now - sample.time >= 0.4 || (value >= 1 && now > sample.time) {
            let speed = (bytes - sample.bytes) / (now - sample.time)
            uploadBytesPerSecond = uploadBytesPerSecond.map { 0.65 * speed + 0.35 * $0 } ?? speed
            uploadSample = (now, bytes)
        }
    }

    private func receive(_ offer: Wire, on link: Link) {
        guard let session = link.session, let id = offer.id, id.count == 32, id.allSatisfy({ $0.isHexDigit }),
              let raw = offer.kind, let number = UInt8(exactly: raw), let kind = Transfer.Kind(rawValue: number),
              let name = offer.name, name.utf8.count <= 255,
              let size = offer.size, size >= 0, size <= Transfer.fileLimit,
              kind != .text || size <= Int64(Transfer.textLimit) else { return }

        guard receivingName != nil, link.transfer == nil, !links.values.contains(where: { $0.transfer != nil }) else {
            Task { try? await transmit(Wire(type: "fail", id: id, error: "Pipopu is busy or unavailable to receive."), on: link) }
            return
        }

        let transaction = Transaction(id: id)
        receiveError = nil
        link.transfer = transaction
        link.task = Task { [weak self] in
            guard let self else { return }
            defer { finish(link, transaction) }
            do {
                if offer.secure == true && (!link.peer.supportsCloudAndSecure || kind != .text ||
                    size > Int64(Transfer.limit) || offer.route != nil) {
                    throw TransferError.message("The secure text offer used an unsupported format.")
                }
                if offer.route == "cloud-deferred" && !link.peer.supportsCloudAndSecure {
                    throw TransferError.message("This cloud transfer requires a newer version of Pipopu.")
                }
                let pair = offer.pair == true || !TrustStore.contains(session.identity)
                try await transmit(Wire(type: "review", id: id, pair: pair), on: link)
                if pair { try await confirmTrust(link, session: session) }
                try await transmit(Wire(type: "accept", id: id), on: link)
                try expect(await transaction.next(), "verified")
                if pair {
                    try TrustStore.remember(session.identity, name: link.peer.name)
                    trustedDevices = TrustStore.all()
                }
                if offer.route == "cloud-deferred" {
                    return
                }
                transaction.deadline = Date().addingTimeInterval(7200)
                let message = try await transaction.next()
                let transfer: Transfer
                if size <= Int64(Transfer.limit) {
                    try expect(message, "data")
                    guard let data = message.data, Int64(data.count) == size else {
                        throw TransferError.message("The received message size did not match the transfer offer.")
                    }
                    transfer = try Transfer(kind: kind, name: name, data: data, secure: offer.secure == true)
                } else {
                    let encrypted = try await download(message, link: link, transaction: transaction,
                                                       size: FileCipher.encryptedSize(size))
                    let input = try FileHandle(forReadingFrom: encrypted.url)
                    defer { try? input.close() }
                    let clear = try await FileCipher.transform(input: input, size: size, key: session.key(for: id), encrypt: false)
                    if kind == .text {
                        transfer = try Transfer(kind: kind, name: name, data: Data(contentsOf: clear.url))
                    } else {
                        transfer = try Transfer(kind: kind, name: name, data: Data(), file: clear)
                    }
                }
                try Task.checkCancellation()
                received = transfer
                try await transmit(Wire(type: "complete", id: id), on: link)
            } catch {
                receiveError = transaction.peerError ?? error.localizedDescription
                if transaction.peerError == nil && !Task.isCancelled && link.peer.connected {
                    try? await transmit(Wire(type: "fail", id: id, error: error.localizedDescription), on: link)
                }
            }
        }
    }

    private func download(_ route: Wire, link: Link, transaction: Transaction, size: Int64) async throws -> TemporaryFile {
        try expect(route, "route")
        if route.route == "lan" {
            do {
                let url = try await BulkNetwork.localURL(route.urls ?? [])
                return try await BulkNetwork.download(url, cloud: false, size: size)
            } catch {
                try Task.checkCancellation()
                try await transmit(Wire(type: "fallback", id: transaction.id), on: link)
                let next = try await transaction.next()
                try expect(next, "route")
                guard next.route == "cloud" else { throw TransferError.invalid }
                return try await BulkNetwork.download(BulkNetwork.cloudURL(next), cloud: true, size: size)
            }
        }
        guard route.route == "cloud" else { throw TransferError.invalid }
        return try await BulkNetwork.download(BulkNetwork.cloudURL(route), cloud: true, size: size)
    }

    private func expect(_ message: Wire, _ type: String) throws {
        guard message.type == type else { throw TransferError.message("The other device sent an unexpected message while waiting for \(type).") }
    }

    func refreshCloud() {
        let available = links.values.filter {
            $0.peer.connected && $0.peer.supportsCloudAndSecure && $0.session.map { TrustStore.contains($0.identity) } == true
        }
        guard !available.isEmpty else {
            cloudStatus = "Connect to a paired device to refresh cloud transfers."
            return
        }
        cloudStatus = "Checking cloud transfers…"
        for link in available {
            Task {
                do { try await transmit(Wire(type: "cloud-list"), on: link) }
                catch { cloudStatus = error.localizedDescription }
            }
        }
    }

    func hasCloudFile(_ notice: CloudNotice) -> Bool {
        !notice.outgoing && CloudArchive.contains(notice.id)
    }

    func getCloudFile(_ notice: CloudNotice) {
        guard !notice.outgoing else { return }
        if hasCloudFile(notice) {
            do { received = try CloudArchive.open(notice.id) }
            catch { cloudStatus = error.localizedDescription }
            return
        }
        guard downloadRequest == nil, let link = links.values.first(where: { link in
            guard link.peer.connected, link.peer.supportsCloudAndSecure, let identity = link.session?.identity,
                  TrustStore.contains(identity) else { return false }
            return cloudByPeer[identity]?.contains(where: { $0.id == notice.id }) == true
        }) else {
            cloudStatus = "Connect to the paired device to request a fresh download link."
            return
        }
        downloadRequest = (notice.id, link.peer.id)
        downloadRequestedAt = Date()
        cloudStatus = "Requesting the cloud file…"
        Task {
            do { try await transmit(Wire(type: "cloud-fetch", id: notice.id), on: link) }
            catch {
                downloadRequest = nil
                cloudStatus = error.localizedDescription
            }
        }
    }

    private func publishCloudNotices() {
        cloudNotices = cloudByPeer.values.flatMap { $0 }
        if let data = try? JSONEncoder().encode(cloudByPeer) {
            UserDefaults.standard.set(data, forKey: "PipopuCloudNotices")
        }
    }

    private func cloudMessage(_ message: Wire, on link: Link, identity: String) {
        guard TrustStore.contains(identity) else { return }
        switch message.type {
        case "cloud-inbox":
            guard let data = message.data,
                  let notices = try? JSONDecoder().decode([CloudNotice].self, from: data) else {
                cloudStatus = "The other device returned an invalid cloud transfer list."
                return
            }
            cloudByPeer[identity] = notices
            publishCloudNotices()
            if downloadRequest == nil { cloudStatus = nil }
            for notice in notices where !notice.outgoing && notice.status == "Ready" && hasCloudFile(notice) {
                Task { try? await transmit(Wire(type: "cloud-delivered", id: notice.id), on: link) }
            }
        case "cloud-error" where message.id == downloadRequest?.id:
            downloadRequest = nil
            cloudStatus = message.error ?? "The cloud file is unavailable."
        case "cloud-ticket" where !downloadingCloudFile && message.id == downloadRequest?.id &&
            link.peer.id == downloadRequest?.peer:
            downloadingCloudFile = true
            Task {
                do {
                    guard let id = message.id, CloudArchive.valid(id), let key = message.data, key.count == 32,
                          let name = message.name, name.utf8.count <= 255,
                          let raw = message.kind, let number = UInt8(exactly: raw), let kind = Transfer.Kind(rawValue: number),
                          let size = message.size, size > 0, size <= Transfer.fileLimit,
                          kind != .text || size <= Int64(Transfer.textLimit) else {
                        throw TransferError.message("The other device returned an invalid cloud download ticket.")
                    }
                    cloudStatus = "Downloading from OneDrive…"
                    let encrypted = try await BulkNetwork.download(BulkNetwork.cloudURL(message), cloud: true,
                                                                  size: FileCipher.encryptedSize(size))
                    let input = try FileHandle(forReadingFrom: encrypted.url)
                    defer { try? input.close() }
                    let clear = try await FileCipher.transform(input: input, size: size, key: SymmetricKey(data: key),
                                                              encrypt: false)
                    received = try CloudArchive.save(clear, id: id, kind: kind, name: name)
                    publishCloudNotices()
                    cloudStatus = "Downloaded."
                    if link.peer.connected {
                        try? await transmit(Wire(type: "cloud-delivered", id: id), on: link)
                    }
                } catch { cloudStatus = error.localizedDescription }
                downloadRequest = nil
                downloadingCloudFile = false
            }
        default: break
        }
    }

    private func confirmTrust(_ link: Link, session: SecureSession) async throws {
        guard confirmation == nil else { throw TransferError.message("A pairing request is already open.") }
        let accepted = await withCheckedContinuation { continuation in
            confirmation = (link, continuation)
            pairing = Pairing(name: link.peer.name, code: session.code)
        }
        guard accepted else { throw TransferError.message("The pairing request was declined.") }
        try Task.checkCancellation()
    }

    private func finish(_ link: Link, _ transaction: Transaction) {
        transaction.incoming.finish()
        if link.transfer === transaction {
            link.transfer = nil
            link.task = nil
        }
    }

    private func connect(_ link: Link) {
        guard link.peripheral.state == .disconnected else { return }
        link.connectingSince = Date()
        link.peripheral.delegate = self
        central?.connect(link.peripheral)
    }

    private func publishPeers() {
        peers = links.values.map(\.peer).sorted {
            let order = $0.name.localizedStandardCompare($1.name)
            return order == .orderedSame ? $0.id.uuidString < $1.id.uuidString : order == .orderedAscending
        }
    }

    private func tick() {
        let now = Date()
        if downloadRequest != nil && !downloadingCloudFile && now.timeIntervalSince(downloadRequestedAt) > 40 {
            downloadRequest = nil
            cloudStatus = "The other device did not return a download link. Refresh and try again."
        }
        for (id, link) in Array(links) {
            if let transaction = link.transfer, now > transaction.deadline {
                if transaction.peerError == nil {
                    transaction.peerError = sendStage == .requestingOneDriveLink && sender == id
                        ? "The receiving device did not return a OneDrive link in time. Check that it’s online and signed in to OneDrive."
                        : TransferError.timedOut.localizedDescription
                }
                transaction.incoming.finish(throwing: TransferError.timedOut)
                link.task?.cancel()
                if confirmation?.0 === link { answerPairing(false) }
                central?.cancelPeripheralConnection(link.peripheral)
            }
            if let started = link.connectingSince, now.timeIntervalSince(started) > 18 {
                central?.cancelPeripheralConnection(link.peripheral)
            }
            if now.timeIntervalSince(link.incomingAt) > 30 { link.incoming = nil }
            if link.peer.connected && link.transfer == nil && now >= link.nextNameRefresh {
                announce(on: link)
            }
            if !link.writes.isEmpty && now.timeIntervalSince(link.writeAt) > 18 {
                central?.cancelPeripheralConnection(link.peripheral)
            }
            if link.peripheral.state == .disconnected && now.timeIntervalSince(link.lastSeen) > 12 {
                links[id] = nil
            }
        }
        publishPeers()
    }

    private func transmit(_ message: Wire, on link: Link, progress: ((Double) -> Void)? = nil) async throws {
        try Task.checkCancellation()
        guard let session = link.session else { throw TransferError.disconnected }
        var outgoing = message
        if message.type != "ready" {
            guard let version = link.version else { throw TransferError.disconnected }
            outgoing.version = version
        }
        try await enqueue(try session.seal(outgoing), on: link, progress: progress)
    }

    private func enqueue(_ packet: Data, on link: Link, progress: ((Double) -> Void)? = nil) async throws {
        guard link.upload != nil else { throw TransferError.disconnected }
        try await withCheckedThrowingContinuation { continuation in
            link.writes.append(Write(packet, continuation: continuation, progress: progress))
            if link.writes.count == 1 { writeNext(link) }
        }
    }

    private func writeNext(_ link: Link) {
        guard let upload = link.upload else { return }
        if link.writes.first?.ended == true {
            link.writes.removeFirst().continuation.resume()
        }
        guard let write = link.writes.first else { return }
        let frame: Data
        if !write.started {
            frame = Data([Frame.begin] + Frame.number(write.token) + Frame.number(UInt32(write.packet.count)))
            write.started = true
        } else if write.offset < write.packet.count {
            let capacity = min(link.peripheral.maximumWriteValueLength(for: .withResponse),
                               link.peripheral.maximumWriteValueLength(for: .withoutResponse)) - 1
            guard capacity > 0 else { central?.cancelPeripheralConnection(link.peripheral); return }
            let end = min(write.offset + capacity, write.packet.count)
            frame = Data([Frame.chunk]) + write.packet[write.offset..<end]
            write.offset = end
            write.progress?(Double(end) / Double(write.packet.count))
        } else {
            frame = Data([Frame.end] + Frame.number(write.token))
            write.ended = true
        }
        link.writeAt = Date()
        link.peripheral.writeValue(frame, for: upload, type: .withResponse)
    }

    private func dispatch(_ packet: Data, on link: Link) throws {
        if let session = link.session {
            let message: Wire
            do { message = try session.open(packet) }
            catch is DecodingError {
                throw TransferError.message("The other device sent a transfer message Pipopu could not read. Update Pipopu on both devices.")
            } catch {
                throw TransferError.message("Pipopu could not authenticate a Bluetooth message from the other device. Reconnect and try again.")
            }
            if message.type == "ready", let name = message.name {
                guard message.error == nil,
                      let selected = WireProtocol.negotiate(message.protocolMin, message.protocolMax),
                      (message.version ?? WireProtocol.legacy) == selected,
                      link.version == nil || link.version == selected else {
                    let error = message.error ?? "Update Pipopu on both devices to use a compatible transfer protocol."
                    state = error
                    throw TransferError.message(error)
                }
                let firstConnection = !link.peer.connected
                link.version = selected
                link.peer.name = name
                link.peer.connected = true
                link.peer.supportsCloudAndSecure = selected >= WireProtocol.cloudAndSecure
                link.connectingSince = nil
                publishPeers()
                if firstConnection && receivingName != nil && link.peer.supportsCloudAndSecure &&
                    TrustStore.contains(session.identity) {
                    Task { try? await transmit(Wire(type: "cloud-list"), on: link) }
                }
            } else if link.version == nil || (message.version ?? WireProtocol.legacy) != link.version {
                throw TransferError.message("The other device sent a message using a different transfer protocol version. Update Pipopu on both devices.")
            } else if message.type == "cloud-inbox" || message.type == "cloud-ticket" || message.type == "cloud-error" {
                cloudMessage(message, on: link, identity: session.identity)
            } else if message.type == "offer", link.peer.connected {
                receive(message, on: link)
            } else if let transaction = link.transfer, message.id == transaction.id {
                if message.type == "cancel" || message.type == "fail" {
                    let failure = message.type == "cancel" ? "The other device cancelled the transfer." :
                        message.error ?? "The other device declined the transfer."
                    transaction.peerError = failure
                    transaction.requiresOneDriveSetup = message.route == "cloud-sign-in-required"
                    transaction.incoming.finish(throwing: TransferError.message(failure))
                    link.task?.cancel()
                    if confirmation?.0 === link { answerPairing(false) }
                } else {
                    if message.type == "cloud-upload" { transaction.detached = true }
                    if case .dropped = transaction.incoming.yield(message) {
                        let message = "The other device sent more Bluetooth messages than expected for this transfer."
                        transaction.peerError = message
                        transaction.incoming.finish(throwing: TransferError.message(message))
                        link.task?.cancel()
                    }
                }
            }
        } else {
            guard let identity, let hello = link.hello else { throw TransferError.invalid }
            link.session = try SecureSession(key: identity, hello: hello, welcome: packet)
            announce(on: link)
        }
    }

    private func announce(on link: Link) {
        link.nextNameRefresh = Date().addingTimeInterval(4)
        let device = receivingName ?? UIDevice.current.model
        let name = String(decoding: device.utf8.prefix(60), as: UTF8.self)
        Task {
            do {
                try await transmit(Wire(type: "ready", name: name,
                                        pair: link.session.map { !TrustStore.contains($0.identity) },
                                        receive: receivingName != nil,
                                        protocolMin: WireProtocol.minimum, protocolMax: WireProtocol.current), on: link)
            }
            catch { central?.cancelPeripheralConnection(link.peripheral) }
        }
    }

    private func disconnect(_ link: Link, error: Error? = nil) {
        let failure = error ?? TransferError.disconnected
        if !downloadingCloudFile && downloadRequest?.peer == link.peer.id {
            downloadRequest = nil
            cloudStatus = "Connect to the paired device to request a fresh download link."
        }
        if link.transfer?.detached == true {
            link.transfer = nil
            link.task = nil
        } else {
            link.transfer?.incoming.finish(throwing: failure)
            link.task?.cancel()
        }
        if confirmation?.0 === link { answerPairing(false) }
        let writes = link.writes
        link.writes.removeAll()
        for write in writes { write.continuation.resume(throwing: failure) }
        link.connectingSince = nil
        link.upload = nil
        link.download = nil
        link.incoming = nil
        link.hello = nil
        link.session = nil
        link.version = nil
        link.peer.connected = false
        link.peer.supportsCloudAndSecure = false
    }
}

extension Bluetooth: @preconcurrency CBCentralManagerDelegate {
    func centralManagerDidUpdateState(_ manager: CBCentralManager) {
        switch manager.state {
        case .poweredOn:
            state = "Looking for Pipopu PCs…"
            manager.scanForPeripherals(withServices: [serviceID], options: [CBCentralManagerScanOptionAllowDuplicatesKey: true])
            return
        case .unauthorized: state = "Allow Bluetooth access for Pipopu in Settings."
        case .poweredOff: state = "Turn on Bluetooth to use Pipopu."
        case .unsupported: state = "Bluetooth LE is unavailable on this device."
        default: state = "Waiting for Bluetooth…"
        }
        for link in links.values { disconnect(link) }
        links.removeAll()
        publishPeers()
    }

    func centralManager(_ central: CBCentralManager, didDiscover peripheral: CBPeripheral,
                        advertisementData: [String: Any], rssi RSSI: NSNumber) {
        if links[peripheral.identifier] == nil {
            guard links.count < 16 else { return }
            let name = advertisementData[CBAdvertisementDataLocalNameKey] as? String ?? peripheral.name
            let fallback = "Nearby device · " + peripheral.identifier.uuidString.prefix(4)
            links[peripheral.identifier] = Link(peripheral, name: name ?? fallback)
            publishPeers()
        }
        guard let link = links[peripheral.identifier] else { return }
        link.lastSeen = Date()
        if link.transfer == nil { connect(link) }
    }

    func centralManager(_ manager: CBCentralManager, didConnect peripheral: CBPeripheral) {
        peripheral.discoverServices([serviceID])
    }

    func centralManager(_ manager: CBCentralManager, didFailToConnect peripheral: CBPeripheral, error: Error?) {
        centralManager(manager, didDisconnectPeripheral: peripheral, error: error)
    }

    func centralManager(_ manager: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
        guard let link = links[peripheral.identifier] else { return }
        disconnect(link, error: error)
        publishPeers()
    }
}

extension Bluetooth: @preconcurrency CBPeripheralDelegate {
    func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        guard error == nil, let service = peripheral.services?.first(where: { $0.uuid == serviceID }) else {
            central?.cancelPeripheralConnection(peripheral)
            return
        }
        peripheral.discoverCharacteristics([uploadID, downloadID, nameID], for: service)
    }

    func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        guard let link = links[peripheral.identifier], error == nil, let chars = service.characteristics,
              let upload = chars.first(where: { $0.uuid == uploadID && $0.properties.contains(.write) }),
              let download = chars.first(where: { $0.uuid == downloadID && $0.properties.contains(.indicate) }) else {
            central?.cancelPeripheralConnection(peripheral)
            return
        }
        link.upload = upload
        link.download = download
        if let name = chars.first(where: { $0.uuid == nameID }) { peripheral.readValue(for: name) }
        peripheral.setNotifyValue(true, for: download)
    }

    func peripheral(_ peripheral: CBPeripheral, didUpdateNotificationStateFor characteristic: CBCharacteristic, error: Error?) {
        guard let link = links[peripheral.identifier], characteristic.uuid == downloadID,
              characteristic.isNotifying, error == nil, let identity else {
            central?.cancelPeripheralConnection(peripheral)
            return
        }
        var nonce = Data(count: 32)
        let status = nonce.withUnsafeMutableBytes { buffer in SecRandomCopyBytes(kSecRandomDefault, 32, buffer.baseAddress!) }
        guard status == errSecSuccess else { central?.cancelPeripheralConnection(peripheral); return }
        let hello = Data([1]) + identity.publicKey.x963Representation + nonce
        link.hello = hello
        Task {
            do { try await enqueue(hello, on: link) }
            catch { central?.cancelPeripheralConnection(peripheral) }
        }
    }

    func peripheral(_ peripheral: CBPeripheral, didWriteValueFor characteristic: CBCharacteristic, error: Error?) {
        guard let link = links[peripheral.identifier], characteristic.uuid == uploadID else { return }
        if error != nil {
            disconnect(link, error: error)
            central?.cancelPeripheralConnection(peripheral)
        } else {
            writeNext(link)
        }
    }

    func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        guard let link = links[peripheral.identifier], error == nil, let frame = characteristic.value,
              let opcode = frame.first else { return }
        if characteristic.uuid == nameID && !link.peer.connected {
            let name = String(decoding: frame, as: UTF8.self)
            if !name.isEmpty { link.peer.name = name; publishPeers() }
            return
        }
        guard characteristic.uuid == downloadID else { return }
        do {
            switch opcode {
            case Frame.begin: link.incoming = try IncomingPacket(frame)
            case Frame.chunk: try link.incoming?.append(frame)
            case Frame.end:
                guard let packet = try link.incoming?.finish(frame) else {
                    throw TransferError.message("A Bluetooth message ended before Pipopu received its contents.")
                }
                link.incoming = nil
                try dispatch(packet, on: link)
            default: throw TransferError.invalid
            }
            link.incomingAt = Date()
        } catch {
            let failure: Error
            if let transferError = error as? TransferError, case .invalid = transferError {
                failure = TransferError.message("A Bluetooth message was incomplete or damaged. Reconnect the devices and try again.")
            } else {
                failure = error
            }
            if let transaction = link.transfer, !transaction.detached {
                transaction.peerError = failure.localizedDescription
            }
            disconnect(link, error: failure)
            central?.cancelPeripheralConnection(peripheral)
        }
    }
}
