# Pipopu

Pipopu sends text and images between an iPhone or iPad and a Windows PC over Bluetooth Low Energy. The Windows tray app can also send a file of up to 3 MB to the Apple device.

## Run on Windows

Use a Windows 11 PC with a Bluetooth LE adapter that supports peripheral mode and install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet run --project windows/Pipopu/Pipopu.csproj
```

Closing the window keeps the app in the notification area. Click the tray icon to reopen it or right-click and choose **Exit** to stop advertising. On receipt, Pipopu opens a popup with **Copy text** or **Copy image**. Images can also be saved.

To send, paste text or an image, or drop a single image or file. Select the iPhone or iPad below and click **Send**. Open Pipopu on the Apple device first and keep **Available to receive** enabled. The Apple app displays the received text or image and lets you save files through the Files picker.

## Run and debug on iPhone or iPad

Use Xcode and an iPhone or iPad running iOS or iPadOS 17 or later. Sign in to your Apple development account in Xcode and pair the device in **Window → Devices and Simulators** or **Device Hub**, depending on the Xcode version. Enable [Developer Mode](https://developer.apple.com/documentation/xcode/enabling-developer-mode-on-a-device). Put the Mac and device on the same Wi-Fi network and unplug USB once the device is available over the network. Wi-Fi is used for development deployment and debugging. Pipopu transfers use BLE.

```bash
./scripts/debug-ios.sh --list
./scripts/debug-ios.sh --device "My iPhone" --team YOURTEAMID --bundle-id com.example.pipopu
./scripts/debug-ios.sh --device "My iPhone" --team YOURTEAMID --bundle-id com.example.pipopu --lldb
```

Replace the team and bundle ID with values for your Apple account. The extension bundle ID derives from the app ID. The script builds and installs both targets, then streams the app console by default. With `--lldb`, it attaches before the app begins running. Use `continue` at the LLDB prompt when ready. To debug the Share Sheet extension, attach to `PipopuShare` from Xcode while bringing up the extension on the device.

To send from Apple, choose **Share → Pipopu** in an app such as Photos or Notes. Select a nearby PC from the list. If Pipopu is hidden, open **More** in the Share Sheet and enable it. The extension handles one item at a time and treats a shared web URL as text. It keeps JPEG and PNG originals that fit and converts other or larger images to a smaller JPEG for Windows clipboard compatibility.

## BLE behavior

Windows advertises the Pipopu service UUID. The Apple app and Share Sheet extension scan for that UUID. To receive from Windows, the foreground Apple app subscribes to its download characteristic and registers the label shown on the PC. The Windows adapter and driver must support connectable GATT advertising. Large transfers can take several minutes, depending on the adapter and connection.

This bootstrap has no peer authentication or app-level encryption. The service UUID identifies protocol-compatible devices. Nearby devices that implement the protocol can send to the Windows app while it is advertising.

### Wire format

Service and characteristic UUIDs share the suffix `-8B6E-44DA-BEEC-E10D5680D630`.

| Prefix | Purpose |
| --- | --- |
| `EA259A30` | Advertised Pipopu GATT service |
| `EA259A31` | Apple-to-Windows writes with response |
| `EA259A32` | Windows-to-Apple indications |
| `EA259A33` | Readable UTF-8 PC name |

A transfer consists of a start frame (opcode `01`, 32-bit token, 32-bit envelope length), one or more data frames (opcode `02` and envelope bytes), and an end frame (opcode `03` and the token). The envelope contains the type byte (1 text, 2 image, 3 file), a 16-bit UTF-8 filename length, up to 255 filename bytes, up to 3,000,000 payload bytes, and the 32-byte SHA-256 digest of all preceding envelope bytes. Integers are little-endian. Windows acknowledges successful receipt through the end-frame write response. Apple writes opcode `04` with the token after validating a download, and opcode `05` with its UTF-8 display label after subscribing.

### Hardware smoke test

1. Start the Windows tray app and confirm it reports that Bluetooth is available.
2. Share text and then a photo from iOS. Verify that the correct PC appears and that each Windows popup copies the expected content.
3. Open Pipopu on iOS. Send text, an image, and a file close to 3 MB from Windows. Copy the text and image, save the file to Files, and compare it with the original.
4. Background the Apple app during a transfer. Reopen it and verify the Windows device list recovers and a fresh send succeeds.
