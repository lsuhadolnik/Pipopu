import Combine
import CoreBluetooth
import Foundation

private let serviceID = CBUUID(string: "EA259A30-8B6E-44DA-BEEC-E10D5680D630")
private let uploadID = CBUUID(string: "EA259A31-8B6E-44DA-BEEC-E10D5680D630")
private let downloadID = CBUUID(string: "EA259A32-8B6E-44DA-BEEC-E10D5680D630")
private let nameID = CBUUID(string: "EA259A33-8B6E-44DA-BEEC-E10D5680D630")

final class Bluetooth: NSObject, ObservableObject {
    struct Peer: Identifiable {
        let id: UUID
        var name: String
        var connected = false
    }

    private enum WritePurpose { case send, registration, acknowledgement }

    private struct PendingWrite {
        let frame: Data
        let purpose: WritePurpose
    }

    private final class Link {
        let peripheral: CBPeripheral
        var peer: Peer
        var lastSeen = Date()
        var connectingSince: Date?
        var upload: CBCharacteristic?
        var download: CBCharacteristic?
        var writing: WritePurpose?
        var pendingWrites: [PendingWrite] = []
        var nameCharacteristic: CBCharacteristic?
        var nextNameRead = Date.distantPast
        var hasServiceName = false
        var incoming: IncomingTransfer?
        var incomingAt = Date.distantPast

        init(_ peripheral: CBPeripheral, name: String) {
            self.peripheral = peripheral
            peer = Peer(id: peripheral.identifier, name: name)
        }
    }

    private struct Send {
        let id: UUID
        let token = UInt32.random(in: .min ... .max)
        let data: Data
        let onFinish: () -> Void
        var offset = 0
        var started = false
        var ended = false
        var lastActivity = Date()
    }

    @Published private(set) var peers = [Peer]()
    @Published private(set) var state = "Starting Bluetooth…"
    @Published private(set) var progress: Double?
    @Published private(set) var result: String?
    @Published private(set) var received: Transfer?
    @Published private(set) var receiveError: String?
    @Published private(set) var sendCompleted = false

    private var central: CBCentralManager?
    private var links = [UUID: Link]()
    private var timer: Timer?
    private var receivingName: String?
    private var send: Send?

    func start(receivingAs name: String? = nil) {
        guard central == nil else { return }
        receivingName = name
        central = CBCentralManager(delegate: self, queue: .main)
        timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in self?.tick() }
    }

    func stop() {
        timer?.invalidate()
        timer = nil
        central?.stopScan()
        for link in links.values { central?.cancelPeripheralConnection(link.peripheral) }
        central?.delegate = nil
        central = nil
        links.removeAll()
        peers = []
        send = nil
        progress = nil
    }

    func clearReceived() { received = nil }

    func updateReceivingName(_ name: String?) {
        guard receivingName != name else { return }
        let availabilityChanged = (receivingName == nil) != (name == nil)
        receivingName = name
        for link in links.values where link.peripheral.state == .connected {
            guard let download = link.download else { continue }
            if availabilityChanged {
                link.peripheral.setNotifyValue(name != nil, for: download)
            } else if let name, download.isNotifying {
                announce(name, on: link)
            }
        }
    }

    func send(_ transfer: Transfer, to id: UUID, onFinish: @escaping () -> Void) {
        guard send == nil else { return }
        sendCompleted = false
        guard let link = links[id] else {
            result = TransferError.disconnected.localizedDescription
            onFinish()
            return
        }
        result = nil
        progress = 0
        send = Send(id: id, data: transfer.envelope(), onFinish: onFinish)
        if link.upload != nil {
            writeNext(link)
        } else {
            connect(link)
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
        if let active = send, Date().timeIntervalSince(active.lastActivity) > 20 {
            finish(TransferError.timedOut)
            if let link = links[active.id] { central?.cancelPeripheralConnection(link.peripheral) }
        }
        for (id, link) in Array(links) {
            if let started = link.connectingSince, Date().timeIntervalSince(started) > 18 {
                link.connectingSince = nil
                central?.cancelPeripheralConnection(link.peripheral)
            }
            if Date().timeIntervalSince(link.incomingAt) > 30 { link.incoming = nil }
            if send?.id != id, link.incoming == nil, Date() >= link.nextNameRead {
                refreshName(of: link)
            }
            if link.peripheral.state == .disconnected && send?.id != id &&
                Date().timeIntervalSince(link.lastSeen) > 12 {
                links[id] = nil
            }
        }
        publishPeers()
    }

    private func refreshName(of link: Link) {
        guard link.peripheral.state == .connected, let characteristic = link.nameCharacteristic else { return }
        link.nextNameRead = Date().addingTimeInterval(4)
        link.peripheral.readValue(for: characteristic)
    }

    private func announce(_ name: String, on link: Link) {
        let peripheral = link.peripheral
        guard peripheral.state == .connected, link.upload != nil else { return }
        let capacity = min(64, peripheral.maximumWriteValueLength(for: .withoutResponse) - 1)
        guard capacity > 0 else { return }
        var bytes = Data(name.utf8.prefix(capacity))
        while String(data: bytes, encoding: .utf8) == nil { bytes.removeLast() }
        write(Data([Frame.available]) + bytes, on: link, purpose: .registration)
    }

    private func write(_ frame: Data, on link: Link, purpose: WritePurpose) {
        link.pendingWrites.append(PendingWrite(frame: frame, purpose: purpose))
        flushWrites(on: link)
    }

    private func flushWrites(on link: Link) {
        guard link.writing == nil, !link.pendingWrites.isEmpty,
              link.peripheral.state == .connected, let upload = link.upload else { return }
        let next = link.pendingWrites.removeFirst()
        link.writing = next.purpose
        link.peripheral.writeValue(next.frame, for: upload, type: .withResponse)
    }

    private func finish(_ error: Error? = nil) {
        guard let outgoing = send else { return }
        send = nil
        progress = nil
        result = error?.localizedDescription ?? "Sent"
        sendCompleted = error == nil
        outgoing.onFinish()
    }

    private func writeNext(_ link: Link) {
        guard var outgoing = send, outgoing.id == link.peer.id, link.upload != nil else { return }
        if outgoing.ended {
            finish()
            return
        }

        let frame: Data
        if !outgoing.started {
            frame = Data([Frame.begin] + Frame.number(outgoing.token) + Frame.number(UInt32(outgoing.data.count)))
            outgoing.started = true
        } else if outgoing.offset < outgoing.data.count {
            let capacity = min(
                link.peripheral.maximumWriteValueLength(for: .withResponse),
                link.peripheral.maximumWriteValueLength(for: .withoutResponse)
            ) - 1
            guard capacity > 0 else { finish(TransferError.disconnected); return }
            let end = min(outgoing.offset + capacity, outgoing.data.count)
            frame = Data([Frame.chunk]) + outgoing.data[outgoing.offset..<end]
            outgoing.offset = end
            progress = Double(end) / Double(outgoing.data.count)
        } else {
            frame = Data([Frame.end] + Frame.number(outgoing.token))
            outgoing.ended = true
        }
        outgoing.lastActivity = Date()
        send = outgoing
        write(frame, on: link, purpose: .send)
    }

    private func clearLink(_ peripheral: CBPeripheral, error: Error?) {
        guard let link = links[peripheral.identifier] else { return }
        if send?.id == peripheral.identifier { finish(error ?? TransferError.disconnected) }
        link.connectingSince = nil
        link.upload = nil
        link.download = nil
        link.writing = nil
        link.pendingWrites.removeAll()
        link.nameCharacteristic = nil
        link.incoming = nil
        link.peer.connected = false
        publishPeers()
    }
}

extension Bluetooth: CBCentralManagerDelegate {
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
        finish(TransferError.disconnected)
        links.removeAll()
        publishPeers()
    }

    func centralManager(
        _ central: CBCentralManager, didDiscover peripheral: CBPeripheral,
        advertisementData: [String: Any], rssi RSSI: NSNumber
    ) {
        if links[peripheral.identifier] == nil {
            guard links.count < 16 else { return }
            let name = advertisementData[CBAdvertisementDataLocalNameKey] as? String ?? peripheral.name
            let fallback = "Windows PC · " + peripheral.identifier.uuidString.prefix(4)
            links[peripheral.identifier] = Link(peripheral, name: name ?? fallback)
            publishPeers()
        }
        guard let link = links[peripheral.identifier] else { return }
        link.lastSeen = Date()
        let advertisedName = advertisementData[CBAdvertisementDataLocalNameKey] as? String
        updateFallbackName(advertisedName ?? peripheral.name, on: link)
        connect(link)
    }

    func centralManager(_ manager: CBCentralManager, didConnect peripheral: CBPeripheral) {
        peripheral.discoverServices([serviceID])
    }

    func centralManager(_ manager: CBCentralManager, didFailToConnect peripheral: CBPeripheral, error: Error?) {
        clearLink(peripheral, error: error)
    }

    func centralManager(_ manager: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
        clearLink(peripheral, error: error)
    }
}

extension Bluetooth: CBPeripheralDelegate {
    private func updateFallbackName(_ name: String?, on link: Link) {
        guard !link.hasServiceName, let name, !name.isEmpty, name != link.peer.name else { return }
        link.peer.name = name
        publishPeers()
    }

    func peripheralDidUpdateName(_ peripheral: CBPeripheral) {
        guard let link = links[peripheral.identifier] else { return }
        updateFallbackName(peripheral.name, on: link)
    }

    func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        guard error == nil, let service = peripheral.services?.first(where: { $0.uuid == serviceID }) else {
            central?.cancelPeripheralConnection(peripheral)
            return
        }
        peripheral.discoverCharacteristics([uploadID, downloadID, nameID], for: service)
    }

    func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        guard let link = links[peripheral.identifier], error == nil,
              let chars = service.characteristics, let upload = chars.first(where: { $0.uuid == uploadID }),
              upload.properties.contains(.write) else {
            central?.cancelPeripheralConnection(peripheral)
            return
        }
        link.upload = upload
        link.download = chars.first(where: { $0.uuid == downloadID })
        link.connectingSince = nil
        link.nameCharacteristic = chars.first(where: { $0.uuid == nameID && $0.properties.contains(.read) })
        refreshName(of: link)
        if let download = link.download, receivingName != nil {
            peripheral.setNotifyValue(true, for: download)
        }
        if send?.id == peripheral.identifier { writeNext(link) }
    }

    func peripheral(_ peripheral: CBPeripheral, didUpdateNotificationStateFor characteristic: CBCharacteristic, error: Error?) {
        guard let link = links[peripheral.identifier], characteristic.uuid == downloadID else { return }
        guard error == nil else {
            central?.cancelPeripheralConnection(peripheral)
            return
        }
        if characteristic.isNotifying, let name = receivingName {
            announce(name, on: link)
        } else if !characteristic.isNotifying && link.peer.connected {
            link.peer.connected = false
            publishPeers()
        }
    }

    func peripheral(_ peripheral: CBPeripheral, didWriteValueFor characteristic: CBCharacteristic, error: Error?) {
        guard let link = links[peripheral.identifier], characteristic.uuid == uploadID,
              let purpose = link.writing else { return }
        link.writing = nil
        if let error {
            if send?.id == peripheral.identifier { finish(error) }
            link.pendingWrites.removeAll()
            central?.cancelPeripheralConnection(peripheral)
            return
        }
        switch purpose {
        case .send:
            writeNext(link)
        case .registration where receivingName != nil && !link.peer.connected:
            link.peer.connected = true
            publishPeers()
        case .registration, .acknowledgement:
            break
        }
        flushWrites(on: link)
    }

    func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        guard let link = links[peripheral.identifier], error == nil,
              let frame = characteristic.value, let opcode = frame.first else { return }
        if characteristic.uuid == nameID {
            guard let name = String(data: frame, encoding: .utf8), !name.isEmpty else { return }
            link.hasServiceName = true
            if name != link.peer.name {
                link.peer.name = name
                publishPeers()
            }
            return
        }
        guard characteristic.uuid == downloadID, link.upload != nil else { return }
        do {
            switch opcode {
            case Frame.begin:
                receiveError = nil
                link.incoming = try IncomingTransfer(frame)
            case Frame.chunk: try link.incoming?.append(frame)
            case Frame.abort:
                guard frame.count == 5, link.incoming?.token == Frame.readNumber(frame, at: 1) else { return }
                link.incoming = nil
            case Frame.end:
                guard let incoming = link.incoming else { return }
                received = try incoming.finish(frame)
                link.incoming = nil
                write(Data([Frame.received] + Frame.number(incoming.token)), on: link, purpose: .acknowledgement)
            default: return
            }
            link.incomingAt = Date()
        } catch {
            link.incoming = nil
            receiveError = error.localizedDescription
        }
    }
}
