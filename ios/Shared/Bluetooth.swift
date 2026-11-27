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

    private final class Link {
        let peripheral: CBPeripheral
        var peer: Peer
        var lastSeen = Date()
        var connectingSince: Date?
        var upload: CBCharacteristic?
        var download: CBCharacteristic?
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

    func send(_ transfer: Transfer, to id: UUID, onFinish: @escaping () -> Void) {
        guard send == nil, let link = links[id] else { return }
        sendCompleted = false
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
            if link.peripheral.state == .disconnected && send?.id != id &&
                Date().timeIntervalSince(link.lastSeen) > 12 {
                links[id] = nil
            }
        }
        publishPeers()
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
        guard var outgoing = send, outgoing.id == link.peer.id, let upload = link.upload else { return }
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
        link.peripheral.writeValue(frame, for: upload, type: .withResponse)
    }

    private func clearLink(_ peripheral: CBPeripheral, error: Error?) {
        guard let link = links[peripheral.identifier] else { return }
        if send?.id == peripheral.identifier { finish(error ?? TransferError.disconnected) }
        link.connectingSince = nil
        link.upload = nil
        link.download = nil
        link.incoming = nil
        link.peer.connected = false
        publishPeers()
    }
}

extension Bluetooth: CBCentralManagerDelegate {
    func centralManagerDidUpdateState(_ manager: CBCentralManager) {
        switch manager.state {
        case .poweredOn:
            state = receivingName == nil ? "Looking for Pipopu PCs…" : "Looking for PCs while Pipopu is open…"
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
        if let name = chars.first(where: { $0.uuid == nameID }) { peripheral.readValue(for: name) }
        if let download = link.download, receivingName != nil {
            peripheral.setNotifyValue(true, for: download)
        }
        if send?.id == peripheral.identifier { writeNext(link) }
    }

    func peripheral(_ peripheral: CBPeripheral, didUpdateNotificationStateFor characteristic: CBCharacteristic, error: Error?) {
        guard let link = links[peripheral.identifier], characteristic.uuid == downloadID,
              let name = receivingName, let upload = link.upload else { return }
        guard error == nil, characteristic.isNotifying else {
            central?.cancelPeripheralConnection(peripheral)
            return
        }
        let length = max(0, min(64, peripheral.maximumWriteValueLength(for: .withoutResponse) - 1))
        peripheral.writeValue(Data([Frame.available]) + Data(name.utf8.prefix(length)), for: upload, type: .withResponse)
    }

    func peripheral(_ peripheral: CBPeripheral, didWriteValueFor characteristic: CBCharacteristic, error: Error?) {
        guard let link = links[peripheral.identifier], characteristic.uuid == uploadID else { return }
        if error != nil {
            if send?.id == peripheral.identifier { finish(error) }
            central?.cancelPeripheralConnection(peripheral)
        } else if send?.id == peripheral.identifier {
            writeNext(link)
        } else if link.download?.isNotifying == true && !link.peer.connected {
            link.peer.connected = true
            publishPeers()
        }
    }

    func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        guard let link = links[peripheral.identifier], error == nil,
              let frame = characteristic.value, let opcode = frame.first else { return }
        if characteristic.uuid == nameID {
            let name = String(decoding: frame, as: UTF8.self)
            if !name.isEmpty { link.peer.name = name; publishPeers() }
            return
        }
        guard characteristic.uuid == downloadID, let upload = link.upload else { return }
        do {
            switch opcode {
            case Frame.begin: link.incoming = try IncomingTransfer(frame)
            case Frame.chunk: try link.incoming?.append(frame)
            case Frame.end:
                guard let incoming = link.incoming else { return }
                received = try incoming.finish(frame)
                link.incoming = nil
                peripheral.writeValue(Data([Frame.received] + Frame.number(incoming.token)), for: upload, type: .withResponse)
            default: return
            }
            link.incomingAt = Date()
        } catch {
            link.incoming = nil
            result = error.localizedDescription
        }
    }
}
