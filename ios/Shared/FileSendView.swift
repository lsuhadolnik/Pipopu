import SwiftUI
import UIKit

struct FileSend {
    let name: String
    let size: Int64
    let recipient: String
}

struct FileSendView: View {
    let file: FileSend
    @ObservedObject var bluetooth: Bluetooth
    let tint: Color
    let close: () -> Void
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var cancelling = false
    @ScaledMetric(relativeTo: .title) private var ringSize = 192.0

    init(file: FileSend, bluetooth: Bluetooth, tint: Color, close: @escaping () -> Void) {
        self.file = file
        _bluetooth = ObservedObject(wrappedValue: bluetooth)
        self.tint = tint
        self.close = close
    }

    private var active: Bool { bluetooth.sendStage != nil }
    private var failed: Bool { !active && !bluetooth.sendCompleted && !cancelling }
    private var fraction: Double? { cancelling ? nil : bluetooth.sendFraction }
    private var highlight: Color { failed ? .orange : (bluetooth.sendCompleted ? .green : tint) }
    private var title: String {
        if cancelling && active { return "Canceling transfer…" }
        if let stage = bluetooth.sendStage { return stage.label }
        if cancelling { return "Transfer canceled" }
        if bluetooth.sendCompleted { return bluetooth.sendQueued ? "Uploaded to OneDrive" : "Transfer complete" }
        return "Transfer failed"
    }

    var body: some View {
        GeometryReader { geometry in
            ScrollView {
                VStack(spacing: 23) {
                    fileDetails
                    ring
                    transferDetails
                    transferAction
                }
                .multilineTextAlignment(.center)
                .frame(maxWidth: 410)
                .padding(.horizontal, 25)
                .padding(.vertical, 26)
                .frame(maxWidth: .infinity, minHeight: geometry.size.height)
            }
            .scrollBounceBehavior(.basedOnSize)
        }
        .background {
            TransferBackdrop(tint: highlight, secondary: .indigo, animated: active && !reduceMotion)
        }
        .tint(tint)
    }

    private var fileDetails: some View {
        VStack(spacing: 7) {
            Text(file.name.isEmpty ? "Shared file" : file.name)
                .font(.title2.weight(.semibold))
                .lineLimit(3)
            Text(ByteCountFormatter.string(fromByteCount: file.size, countStyle: .file))
                .font(.subheadline.monospacedDigit())
                .foregroundStyle(.secondary)
            VStack(spacing: 4) {
                Text("Sending to").font(.caption).foregroundStyle(.secondary)
                Label(file.recipient, systemImage: "desktopcomputer")
                    .font(.subheadline.weight(.medium))
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 9)
            .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 14))
            .padding(.top, 4)
            .accessibilityElement(children: .combine)
        }
    }

    private var transferDetails: some View {
        VStack(spacing: 9) {
            Text(title)
                .font(.title3.weight(.semibold))
                .accessibilityAddTraits(.isHeader)
            if !cancelling, let speed = bluetooth.uploadSpeedLabel,
               let bytes = bluetooth.uploadByteLabel(total: file.size) {
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 12) {
                        Label(speed, systemImage: "speedometer")
                        Text(bytes)
                    }
                    VStack(spacing: 4) {
                        Label(speed, systemImage: "speedometer")
                        Text(bytes)
                    }
                }
                .font(.subheadline.monospacedDigit())
                .foregroundStyle(.secondary)
            }
            if bluetooth.sendStage == .pairing, let code = bluetooth.pairingCode {
                Text(code).font(.title3.monospacedDigit()).textSelection(.enabled)
                    .accessibilityLabel("Pairing code \(code)")
            }
            if failed {
                VStack(alignment: .leading, spacing: 6) {
                    if let stage = bluetooth.sendFailureStage {
                        Text(stage.label.trimmingCharacters(in: CharacterSet(charactersIn: "…")))
                            .font(.subheadline.weight(.semibold))
                    }
                    Text(bluetooth.result ?? "The other device did not confirm the transfer.")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                        .textSelection(.enabled)
                }
                .multilineTextAlignment(.leading)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(14)
                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16))
            } else if bluetooth.sendQueued && bluetooth.sendCompleted, let result = bluetooth.result {
                Text(result).font(.subheadline).foregroundStyle(.secondary)
            }
        }
    }

    private var transferAction: some View {
        Group {
            if active {
                Button("Cancel transfer", role: .cancel) {
                    cancelling = true
                    bluetooth.cancelSend()
                }
                .buttonStyle(.bordered)
                .disabled(cancelling)
            } else {
                Button(bluetooth.sendCompleted ? "Done" : "Back to Send", action: close)
                    .buttonStyle(.borderedProminent)
            }
        }
        .controlSize(.large)
    }

    private var ring: some View {
        ZStack {
            Circle()
                .fill(.thinMaterial)
                .padding(7)
            Circle()
                .strokeBorder(highlight.opacity(0.23), lineWidth: 7)
            if let fraction {
                Circle()
                    .trim(from: 0, to: fraction)
                    .stroke(AngularGradient(colors: [tint, .cyan, tint], center: .center),
                            style: StrokeStyle(lineWidth: 7, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                    .padding(3.5)
                    .animation(reduceMotion ? nil : .easeOut(duration: 0.25), value: fraction)
                VStack(spacing: 5) {
                    Image(systemName: "arrow.up").font(.title3.weight(.semibold))
                    Text(fraction, format: .percent.precision(.fractionLength(0)))
                        .font(.largeTitle.monospacedDigit().weight(.semibold))
                }
            } else if active {
                ProgressView().controlSize(.large).tint(highlight)
            } else {
                Image(systemName: bluetooth.sendCompleted ? "checkmark" : (cancelling ? "xmark" : "exclamationmark"))
                    .font(.system(size: 44, weight: .semibold))
            }
        }
        .foregroundStyle(highlight)
        .frame(width: min(ringSize, 240), height: min(ringSize, 240))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Transfer progress")
        .accessibilityValue(fraction?.formatted(.percent.precision(.fractionLength(0))) ?? title)
    }
}

struct TransferBackdrop: View {
    let tint: Color
    let secondary: Color
    let animated: Bool

    var body: some View {
        GeometryReader { geometry in
            TimelineView(.animation(minimumInterval: 1.0 / 20, paused: !animated)) { timeline in
                let phase = animated ? timeline.date.timeIntervalSinceReferenceDate / 7 : 0
                ZStack {
                    Color(uiColor: .systemBackground)
                    Ellipse()
                        .fill(tint.opacity(0.26))
                        .frame(width: geometry.size.width * 0.96, height: geometry.size.height * 0.45)
                        .blur(radius: 78)
                        .position(x: geometry.size.width * CGFloat(0.24 + 0.08 * cos(phase)),
                                  y: geometry.size.height * CGFloat(0.35 + 0.07 * sin(phase)))
                    Ellipse()
                        .fill(secondary.opacity(0.18))
                        .frame(width: geometry.size.width * 0.74, height: geometry.size.height * 0.40)
                        .blur(radius: 70)
                        .position(x: geometry.size.width * CGFloat(0.78 - 0.10 * cos(phase)),
                                  y: geometry.size.height * CGFloat(0.66 + 0.06 * sin(phase)))
                }
                .clipped()
            }
        }
        .ignoresSafeArea()
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}
