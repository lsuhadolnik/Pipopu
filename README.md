# Pipopu

Pipopu sends text, images, and files between an iPhone or iPad and a Windows PC. Bluetooth Low Energy handles discovery, pairing, and payloads up to 500 KB. Larger payloads use local Wi-Fi or an optional OneDrive fallback.

## Run on Windows

Use a Windows 11 PC with a Bluetooth LE adapter that supports peripheral mode and install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet run --project windows/Pipopu/Pipopu.csproj
```

To build a portable 64-bit Windows executable, use the publish profile. Run
`.build/publish/win-x64/Pipopu.exe` on the target PC without installing .NET.

```powershell
dotnet publish windows/Pipopu/Pipopu.csproj -c Release -p:PublishProfile=Windows-x64 -o .build/publish/win-x64
```

Closing the window keeps the app in the notification area. Click the tray icon to reopen it or right-click and choose **Exit** to stop advertising. On receipt, Pipopu opens a popup to copy text or an image, or save an image or file.

Click the name next to the Bluetooth icon to change how this PC appears to nearby Apple devices. On iPhone or iPad, edit **Name on Windows** in Pipopu to update how the device appears in the tray app. Open device lists refresh while connected.

To send, paste text or an image, or drop a single image or file. Select the iPhone or iPad below and click **Send**. Use **Cancel** while data is sending to stop the transfer and keep your draft. Open Pipopu on the Apple device first and keep **Available to receive** enabled. The Apple app displays the received text or image and lets you save files through the Files picker.

## Run and debug on iPhone or iPad

Use Xcode and an iPhone or iPad running iOS or iPadOS 17 or later. Sign in to your Apple development account in Xcode and pair the device in **Window → Devices and Simulators** or **Device Hub**, depending on the Xcode version. Enable [Developer Mode](https://developer.apple.com/documentation/xcode/enabling-developer-mode-on-a-device). Put the Mac and device on the same Wi-Fi network and unplug USB once the device is available over the network. Wi-Fi is used for development deployment and debugging. Pipopu transfers use BLE.

```bash
./scripts/debug-ios.sh --list
./scripts/debug-ios.sh --device "My iPhone" --team YOURTEAMID --bundle-id com.example.pipopu
./scripts/debug-ios.sh --device "My iPhone" --team YOURTEAMID --bundle-id com.example.pipopu --lldb
```

Replace the team and bundle ID with values for your Apple account. The extension bundle ID derives from the app ID. The script builds and installs both targets, then streams the app console by default. With `--lldb`, it attaches before the app begins running. Use `continue` at the LLDB prompt when ready. To debug the Share Sheet extension, attach to `PipopuShare` from Xcode while bringing up the extension on the device.

Open Pipopu and grant Bluetooth access. To send from the app, type or paste text or choose a file, then tap a PC under **Nearby Windows PCs**. The app can stay available to receive while you send.

In Photos, Notes, or Files, share one item and choose **Send via Pipopu** in the Share Sheet’s actions. Tap a PC to send. The ring around that PC shows transfer progress, and the send screen closes once Windows confirms receipt. You can also choose **Pipopu** in the app row. Use **Edit Actions** in the Share Sheet to favorite the action and adjust its order. iOS controls which actions appear in the compact row.

Shared web URLs are sent as text. Files and images retain their original bytes. Receivers can save files up to 5 GB, and text can be up to 3 MB. Some image formats may only be available to save on Windows. Allow Pipopu to access the local network on iOS to use Wi-Fi from the app and the Share Sheet.

If Xcode reports that it could not mount the developer disk image, check for [Xcode hardware support updates](https://developer.apple.com/documentation/xcode/downloading-and-installing-additional-xcode-components) for the device's OS version.

## Pairing and large transfers

Install this version on both devices. When either side sends to a new device, compare the six-digit code and approve on both screens. Pipopu saves the peer's public key and authenticates paired devices on each connection. A decline ends the attempt and leaves the device eligible to ask again. Manage saved peers in **Trusted devices** in the iOS app and the Windows tray menu.

For large files, Pipopu on Windows starts a temporary HTTP endpoint on an active private IPv4 address. The Apple device probes it to see if the two devices can communicate. This works with Windows on Ethernet as well as Wi-Fi if the devices can reach each other. If Windows Firewall prompts, allow Pipopu on the private network. Pipopu encrypts file data before making it available over HTTP or OneDrive.

If the local endpoint is unreachable, configure **OneDrive fallback** in the Windows tray menu:

1. In the Microsoft Entra admin center, register a public-client application that supports the accounts you want to use. To support both personal Microsoft and work or school accounts, select the corresponding multi-tenant and personal-account option.
2. Enable **Allow public client flows** and add the delegated Microsoft Graph **Files.ReadWrite** permission.
3. Enter the registration's **Application (client) ID** in Pipopu and use the code to sign in at Microsoft. Pipopu keeps the session in memory, so sign in again after restarting Windows Pipopu.

Windows uses a temporary encrypted OneDrive item and sends its preauthenticated download URL over the secured BLE connection. When sending from Apple, Windows creates a preauthenticated upload session and shares its URL over BLE. Apple uploads in order and reports completion, then Windows retrieves and verifies the file before acknowledging it. Windows removes its temporary OneDrive items after the transfer when it can reach OneDrive.

## Wire protocol

Version 2 uses service UUID `EA259A40-8B6E-44DA-BEEC-E10D5680D630`. Windows exposes Apple writes with response (prefix `EA259A41`), Windows indications (`EA259A42`), and a readable discovery label (`EA259A43`). Version 1 clients use a separate service and cannot receive unpaired data from version 2.

A packet travels as a start frame (opcode `01`, little-endian 32-bit token and length), data frames (opcode `02` followed by bytes), and an end frame (opcode `03` and token). The initial packets exchange P-256 public keys and random nonces. Both sides derive directional AES-GCM keys and a pairing code using HKDF-SHA256 over the agreement secret and the handshake transcript. Encrypted packets carry a monotonically increasing sequence number and protect transfer offers, approvals, route negotiation, and completion. Bulk payloads use a transfer-specific AES-GCM key with a separate nonce for each 256 KiB block.

## Hardware smoke test

1. Send small text and a file in both directions. Compare pairing codes, decline once, retry, then approve. Repeat the transfer without a prompt.
2. Transfer a file above 500 KB in both directions while both devices can reach each other locally. Save both received files and compare their hashes.
3. Sign into OneDrive, put the devices on networks that cannot reach each other, and repeat the large-file test. Keep Bluetooth in range to exchange transfer controls. Confirm the temporary OneDrive items are removed.
4. Forget a device and confirm the next transfer asks for approval again. Cancel a send and start another.
