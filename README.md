# Pipopu

Pipopu sends text, images, and files between an iPhone or iPad and the Pipopu desktop app. Bluetooth Low Energy handles discovery, pairing, and payloads up to 500 KB. Larger payloads use local Wi-Fi or an optional OneDrive fallback.

## Project website

The static website lives in [`site/`](site/). Preview it from the repository root with `python -m http.server 8000 --bind 127.0.0.1 --directory site`, then open [the local preview](http://127.0.0.1:8000/).

In [GitHub Pages settings](https://github.com/lsuhadolnik/Pipopu/settings/pages), select **GitHub Actions** as the publishing source and set the custom domain to `pipopu.solutions.si`. The [Publish website workflow](.github/workflows/pages.yml) deploys `site/` on `main` together with the Windows x64 executable and an `update.json` generated from that executable. Changes to the website, Windows app, or changelog trigger a deployment. You can also run it manually from the Actions tab using `main`.

The DNS CNAME for `pipopu.solutions.si` should point to `lsuhadolnik.github.io`. Once GitHub validates the domain and provisions a certificate, enable **Enforce HTTPS** in Pages settings. [GitHub custom domain documentation](https://docs.github.com/en/pages/configuring-a-custom-domain-for-your-github-pages-site/managing-a-custom-domain-for-your-github-pages-site)

For App Store Connect, use [Privacy Policy](https://pipopu.solutions.si/privacy/) as the Privacy Policy URL and [Support](https://pipopu.solutions.si/support/) as the Support URL.

## Run on Windows

Use a device running Windows 11 with a Bluetooth LE adapter that supports peripheral mode and install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet run --project windows/Pipopu/Pipopu.csproj
```

To build a portable 64-bit Windows executable, use the publish profile. Run
`.build/publish/win-x64/Pipopu.exe` on the target PC without installing .NET.

```powershell
dotnet publish windows/Pipopu/Pipopu.csproj -c Release -p:PublishProfile=Windows-x64 -o .build/publish/win-x64
```

Closing the window keeps the app in the notification area. Click the tray icon to reopen it or right-click and choose **Exit** to stop advertising. On receipt, Pipopu opens a popup to copy text or an image, or save an image or file.

The Windows setup wizard offers optional OneDrive setup, launch at Windows sign-in, and a move to `%LOCALAPPDATA%\Pipopu\App\Pipopu.exe` for the single-file download. Moving Pipopu restarts it from the new location and removes the original copy after the handoff. Launch at sign-in opens Pipopu in the tray. You can revisit these choices under **Settings → Setup wizard**.

Click the name next to the Bluetooth icon to change how this device appears to nearby Apple devices. On iPhone or iPad, edit **Device name** in Settings in Pipopu to update how the device appears in the tray app. Open device lists refresh while connected.

To send, paste text or an image, or drop a single image or file. Select the iPhone or iPad below and click **Send**. Use **Cancel** while data is sending to stop the transfer and keep your draft. Open Pipopu on the Apple device first and keep **Available to receive** enabled. The Apple app displays the received text or image and lets you save files through the Files picker.

## Run and debug on iPhone or iPad

Use Xcode and an iPhone or iPad running iOS or iPadOS 17 or later. Sign in to your Apple development account in Xcode and pair the device in **Window → Devices and Simulators** or **Device Hub**, depending on the Xcode version. Enable [Developer Mode](https://developer.apple.com/documentation/xcode/enabling-developer-mode-on-a-device). Put the Mac and device on the same Wi-Fi network and unplug USB once the device is available over the network. Wi-Fi is used for development deployment and debugging. Pipopu transfers use BLE.

```bash
./scripts/debug-ios.sh --list
./scripts/debug-ios.sh --device "My iPhone" --team YOURTEAMID --bundle-id com.example.pipopu
./scripts/debug-ios.sh --device "My iPhone" --team YOURTEAMID --bundle-id com.example.pipopu --lldb
```

Replace the team and bundle ID with values for your Apple account. The extension bundle ID derives from the app ID. The script builds and installs both targets, then streams the app console by default. With `--lldb`, it attaches before the app begins running. Use `continue` at the LLDB prompt when ready. To debug the Share Sheet extension, attach to `PipopuShare` from Xcode while bringing up the extension on the device.

Open Pipopu, follow the setup guide, and grant Bluetooth access. In **Send**, type or paste text or choose a file, then tap a recipient under **Nearby devices**. Find recent items and the cloud queue in **Activity**. Use **Settings** to change the device name, manage trusted devices, or control receiving.

In Photos, Notes, or Files, share one item and choose **Send via Pipopu** in the Share Sheet’s actions. Tap a PC to send. The ring around that PC shows transfer progress, and the send screen closes once Windows confirms receipt. You can also choose **Pipopu** in the app row. Use **Edit Actions** in the Share Sheet to favorite the action and adjust its order. iOS controls which actions appear in the compact row.

Shared web URLs are sent as text. Files and images retain their original bytes. Receivers can save files up to 5 GB, and text can be up to 3 MB. Some image formats may only be available to save on Windows. Allow Pipopu to access the local network on iOS to use Wi-Fi from the app and the Share Sheet.

If Xcode reports that it could not mount the developer disk image, check for [Xcode hardware support updates](https://developer.apple.com/documentation/xcode/downloading-and-installing-additional-xcode-components) for the device's OS version.

## Pairing and large transfers

Install this version on both devices. When either side sends to a new device, compare the six-digit code and approve on both screens. Pipopu saves the peer's public key and authenticates paired devices on each connection. A decline ends the attempt and leaves the device eligible to ask again. Manage saved peers under **Trusted devices** in **Settings** on iOS and Windows.

For large files, Pipopu on Windows starts a temporary HTTP endpoint on an active private IPv4 address. The Apple device probes it to see if the two devices can communicate. This works with Windows on Ethernet as well as Wi-Fi if the devices can reach each other. If Windows Firewall prompts, allow Pipopu on the private network. Pipopu encrypts file data before making it available over HTTP or OneDrive.

To use OneDrive when the local endpoint is unreachable, open **Settings** from the Windows tray menu and select **Sign in to OneDrive**. Copy the displayed code and complete Microsoft sign-in. Pipopu uses its built-in public client ID and delegated Microsoft Graph **Files.ReadWrite** permission. Windows encrypts the saved refresh token for your Windows user so Pipopu can renew the session after a restart. **Sign out** removes it.

The sign-in code is copied automatically. Use **Copy code** to copy it again.

On Windows, **Settings → Open transfer logs** opens the local diagnostics folder. Logs rotate after 512 KiB and include transfer stages, HTTP status codes, and link hostnames. Signed links, sign-in codes, file names, and payloads stay out of the logs.

Windows uses a temporary encrypted OneDrive item and sends its preauthenticated download URL over the secured BLE connection. When sending from Apple, Windows creates a preauthenticated upload session and shares its URL over BLE. Apple uploads in order and reports completion, then Windows retrieves and verifies the file before acknowledging it. Windows removes its temporary OneDrive items after the transfer when it can reach OneDrive.

## Saving received files

In **Settings** on Windows, enable **Save received files automatically** and choose a local folder or sign in and choose a OneDrive folder. Selecting a OneDrive folder saves a regular file in that account in addition to showing the received item in Pipopu.

On iOS, turn on **Save received files automatically** under **Receiving**. Files and images appear in **Files → On My iPhone** (or **On My iPad**) **→ Pipopu → Received**. Text remains available to copy in Pipopu.

## Secure text and cloud transfers

Use **Send securely** in a device's send options to send text directly from the clipboard without previewing it. The receiver sees a **Copy secure text** button. Secure text uses the authenticated Bluetooth connection and stays out of the deferred cloud queue.

**Send over cloud** queues an encrypted OneDrive transfer even when local networking is available. The receiver can close Pipopu after pairing or after an upload session has been issued. Check **Cloud transfers** in the Windows tray menu or in the iOS app later. iOS reconnects to the paired device over Bluetooth to refresh status and request a fresh OneDrive download link.

For large incoming files, the Windows flyout shows the filename, transfer stage, and measured speed while data is moving. Choose a file on the iPhone to see its upload speed on the dedicated transfer screen. OneDrive reports remote upload progress after it accepts each fragment, so that estimate updates less often than the speed of a local transfer.

## Wire protocol

Version 2 uses service UUID `EA259A40-8B6E-44DA-BEEC-E10D5680D630`. Windows exposes Apple writes with response (prefix `EA259A41`), Windows indications (`EA259A42`), and a readable discovery label (`EA259A43`). Version 1 clients use a separate service and cannot receive unpaired data from version 2.

A packet travels as a start frame (opcode `01`, little-endian 32-bit token and length), data frames (opcode `02` followed by bytes), and an end frame (opcode `03` and token). The initial packets exchange P-256 public keys and random nonces. Both sides derive directional AES-GCM keys and a pairing code using HKDF-SHA256 over the agreement secret and the handshake transcript. Encrypted packets carry a monotonically increasing sequence number and protect transfer offers, approvals, route negotiation, and completion. Bulk payloads use a transfer-specific AES-GCM key with a separate nonce for each 256 KiB block.

Within the v2 framing and encryption family, the first encrypted ready message advertises an inclusive minimum and maximum protocol version. The peers use the highest overlapping version and pin it for the connection. Packets with a conflicting version are rejected. Older v2 builds that omit the fields are treated as version 2. Bump the negotiated version for changes to the encrypted transfer messages. Assign a new service UUID if the framing or cryptographic handshake changes.

Version 3 adds hidden secure-text offers and deferred cloud transfer messages. Version 2 peers can continue immediate BLE and bulk transfers. Both peers need version 3 for the new send modes.

## Hardware smoke test

1. Send small text and a file in both directions. Compare pairing codes, decline once, retry, then approve. Repeat the transfer without a prompt.
2. Transfer a file above 500 KB in both directions while both devices can reach each other locally. Save both received files and compare their hashes.
3. Sign into OneDrive, put the devices on networks that cannot reach each other, and repeat the large-file test. Keep Bluetooth in range to exchange transfer controls. Confirm the temporary OneDrive items are removed.
4. Forget a device and confirm the next transfer asks for approval again. Cancel a send and start another.
