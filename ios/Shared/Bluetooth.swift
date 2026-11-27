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
    }

    struct Pairing {
        let name: String
        let code: String
    }

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
    @Published private(set) var result: String?
    @Published private(set) var received: Transfer?
    @Published private(set) var receiveError: String?
    @Published private(set) var sendCompleted = false
    @Published private(set) var pairing: Pairing?
    @Published private(set) var pairingCode: String?
    @Published private(set) var trustedDevices = TrustStore.all()

    private var central: CBCentralManager?
    private var links = [UUID: Link]()
    private var timer: Timer?
    private var receivingName: String?
    private var identity: P256.KeyAgreement.PrivateKey?
    private var sender: UUID?
    private var confirmation: (Link, CheckedContinuation<Bool, Never>)?

    func start(receivingAs name: String? = nil) {
        guard central == nil else { return }
        do { identity = try TrustStore.identity() }
        catch { state = error.localizedDescription; return }
        receivingName = name
        trustedDevices = TrustStore.all()
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

    func send(_ transfer: Transfer, to id: UUID, onFinish: @escaping () -> Void) {
        guard sender == nil, let link = links[id], link.peer.connected, link.transfer == nil,
              let session = link.session else {
            sendCompleted = false
            result = TransferError.message("The selected PC is disconnected or busy.").localizedDescription
            onFinish()
            return
        }
        sender = id
        sendCompleted = false
        result = nil
        progress = 0
        let transaction = Transaction(id: UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased())
        link.transfer = transaction
        link.task = Task { [weak self] in
            guard let self else { return }
            defer {
                finish(link, transaction)
                sender = nil
                progress = nil
                pairingCode = nil
                onFinish()
            }
            do {
                let pair = !TrustStore.contains(session.identity)
                let offer = Wire(type: "offer", id: transaction.id, name: transfer.name, kind: Int(transfer.kind.rawValue),
                                 size: transfer.size, pair: pair)
                try await transmit(offer, on: link)
                let review = try await transaction.next()
                try expect(review, "review")
                if pair || review.pair == true {
                    pairingCode = session.code
                    try await confirmTrust(link, session: session)
                }
                try expect(await transaction.next(), "accept")
                pairingCode = nil
                try await transmit(Wire(type: "verified", id: transaction.id), on: link)
                if pair || review.pair == true {
                    try TrustStore.remember(session.identity, name: link.peer.name)
                    trustedDevices = TrustStore.all()
                }
                transaction.deadline = Date().addingTimeInterval(7200)
                if transfer.size > Int64(Transfer.limit) {
                    let file: TemporaryFile
                    if let existing = transfer.file {
                        file = existing
                    } else {
                        file = TemporaryFile(size: transfer.size)
                        try transfer.data.write(to: file.url)
                    }
                    let input = try FileHandle(forReadingFrom: file.url)
                    defer { try? input.close() }
                    let encrypted = try await FileCipher.transform(input: input, size: transfer.size,
                        key: session.key(for: transaction.id), encrypt: true)
                    let route = try await transaction.next()
                    try await upload(encrypted, route: route, link: link, transaction: transaction)
                    try await transmit(Wire(type: "uploaded", id: transaction.id), on: link)
                } else {
                    try await transmit(Wire(type: "data", id: transaction.id, data: transfer.data), on: link) {
                        [weak self] value in self?.progress = value * 0.95
                    }
                }
                try expect(await transaction.next(), "complete")
                sendCompleted = true
                result = "Sent"
            } catch {
                result = transaction.peerError ?? error.localizedDescription
                if transaction.peerError == nil && !Task.isCancelled && link.peer.connected {
                    try? await transmit(Wire(type: "cancel", id: transaction.id), on: link)
                }
            }
        }
    }

    private func upload(_ encrypted: TemporaryFile, route: Wire, link: Link, transaction: Transaction) async throws {
        try expect(route, "route")
        if route.route == "lan" {
            do {
                let url = try await BulkNetwork.localURL(route.urls ?? [])
                try await BulkNetwork.uploadLocal(url, file: encrypted) { [weak self] value in
                    guard link.transfer === transaction else { return }
                    self?.progress = value
                }
                progress = 0.95
                return
            } catch {
                try Task.checkCancellation()
                try await transmit(Wire(type: "fallback", id: transaction.id), on: link)
                let next = try await transaction.next()
                try expect(next, "route")
                guard next.route == "cloud" else { throw TransferError.invalid }
                try await BulkNetwork.uploadCloud(BulkNetwork.cloudURL(next), file: encrypted) { [weak self] in
                    self?.progress = $0
                }
            }
        } else if route.route == "cloud" {
            try await BulkNetwork.uploadCloud(BulkNetwork.cloudURL(route), file: encrypted) { [weak self] in
                self?.progress = $0
            }
        } else {
            throw TransferError.invalid
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
                let pair = offer.pair == true || !TrustStore.contains(session.identity)
                try await transmit(Wire(type: "review", id: id, pair: pair), on: link)
                if pair { try await confirmTrust(link, session: session) }
                try await transmit(Wire(type: "accept", id: id), on: link)
                try expect(await transaction.next(), "verified")
                if pair {
                    try TrustStore.remember(session.identity, name: link.peer.name)
                    trustedDevices = TrustStore.all()
                }
                transaction.deadline = Date().addingTimeInterval(7200)
                let message = try await transaction.next()
                let transfer: Transfer
                if size <= Int64(Transfer.limit) {
                    try expect(message, "data")
                    guard let data = message.data, Int64(data.count) == size else { throw TransferError.invalid }
                    transfer = try Transfer(kind: kind, name: name, data: data)
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
        guard message.type == type else { throw TransferError.invalid }
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
        for (id, link) in Array(links) {
            if let transaction = link.transfer, now > transaction.deadline {
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
        try await enqueue(try session.seal(message), on: link, progress: progress)
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
            let message = try session.open(packet)
            if message.type == "ready", let name = message.name {
                link.peer.name = name
                link.peer.connected = true
                link.connectingSince = nil
                publishPeers()
            } else if message.type == "offer", link.peer.connected {
                receive(message, on: link)
            } else if let transaction = link.transfer, message.id == transaction.id {
                if message.type == "cancel" || message.type == "fail" {
                    let failure = message.type == "cancel" ? "The PC cancelled the transfer." :
                        message.error ?? "The PC declined the transfer."
                    transaction.peerError = failure
                    transaction.incoming.finish(throwing: TransferError.message(failure))
                    link.task?.cancel()
                    if confirmation?.0 === link { answerPairing(false) }
                } else if case .dropped = transaction.incoming.yield(message) {
                    transaction.incoming.finish(throwing: TransferError.invalid)
                    link.task?.cancel()
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
            do { try await transmit(Wire(type: "ready", name: name, receive: receivingName != nil), on: link) }
            catch { central?.cancelPeripheralConnection(link.peripheral) }
        }
    }

    private func disconnect(_ link: Link, error: Error? = nil) {
        let failure = error ?? TransferError.disconnected
        link.transfer?.incoming.finish(throwing: failure)
        link.task?.cancel()
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
        link.peer.connected = false
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
            let fallback = "Windows PC · " + peripheral.identifier.uuidString.prefix(4)
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
                guard let packet = try link.incoming?.finish(frame) else { throw TransferError.invalid }
                link.incoming = nil
                try dispatch(packet, on: link)
            default: throw TransferError.invalid
            }
            link.incomingAt = Date()
        } catch {
            disconnect(link, error: error)
            central?.cancelPeripheralConnection(peripheral)
        }
    }
}
