using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Pipopu;

internal sealed class BluetoothServer : IDisposable
{
    private static readonly Guid ServiceId = new("EA259A30-8B6E-44DA-BEEC-E10D5680D630");
    private static readonly Guid UploadId = new("EA259A31-8B6E-44DA-BEEC-E10D5680D630");
    private static readonly Guid DownloadId = new("EA259A32-8B6E-44DA-BEEC-E10D5680D630");
    private static readonly Guid NameId = new("EA259A33-8B6E-44DA-BEEC-E10D5680D630");
    private const string DeviceNameKey = @"Software\Pipopu";
    private const string DeviceNameValue = "DeviceName";
    private const int MaxDeviceNameBytes = 64;

    internal sealed record Peer(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record PendingSend(uint Token, TaskCompletionSource Completion);

    private readonly object gate = new();
    private readonly Dictionary<string, IncomingTransfer> incoming = new();
    private readonly Dictionary<string, string> names = new();
    private readonly Dictionary<string, PendingSend> outgoing = new();
    private GattServiceProvider? provider;
    private GattLocalCharacteristic? upload;
    private GattLocalCharacteristic? download;
    private GattLocalCharacteristic? nameCharacteristic;
    private string deviceName = LoadDeviceName();
    private bool disposed;

    internal event Action<Transfer>? TransferReceived;
    internal event Action? PeersChanged;
    internal event Action<string>? StatusChanged;

    internal string DeviceName => Volatile.Read(ref deviceName);

    internal void Rename(string value)
    {
        string name = value.Trim();
        if (name.Length == 0)
            name = Environment.MachineName;
        if (!IsValidName(name))
            throw new ArgumentException("Choose a shorter device name without control characters.");
        if (name == DeviceName)
            return;

        using var settings = Registry.CurrentUser.CreateSubKey(DeviceNameKey);
        settings.SetValue(DeviceNameValue, name);
        Volatile.Write(ref deviceName, name);
    }

    private static string LoadDeviceName()
    {
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(DeviceNameKey);
            if (settings?.GetValue(DeviceNameValue) is string name && IsValidName(name))
                return name;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Debug.WriteLine(error);
        }
        return Environment.MachineName;
    }

    private static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl) && Encoding.UTF8.GetByteCount(name) <= MaxDeviceNameBytes;

    internal async Task StartAsync()
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null || !adapter.IsLowEnergySupported || !adapter.IsPeripheralRoleSupported)
            throw new InvalidOperationException("This PC needs a Bluetooth LE adapter with peripheral-role support.");

        var result = await GattServiceProvider.CreateAsync(ServiceId);
        if (result.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Could not create the BLE service ({result.Error}).");

        var service = result.ServiceProvider;
        var write = await AddCharacteristic(service, UploadId, new()
        {
            CharacteristicProperties = GattCharacteristicProperties.Write,
            WriteProtectionLevel = GattProtectionLevel.Plain
        });
        var indicate = await AddCharacteristic(service, DownloadId, new()
        {
            CharacteristicProperties = GattCharacteristicProperties.Indicate
        });
        var name = await AddCharacteristic(service, NameId, new()
        {
            CharacteristicProperties = GattCharacteristicProperties.Read,
            ReadProtectionLevel = GattProtectionLevel.Plain
        });

        if (disposed)
            return;
        provider = service;
        upload = write;
        download = indicate;
        nameCharacteristic = name;
        name.ReadRequested += OnNameRead;
        upload.WriteRequested += OnWrite;
        download.SubscribedClientsChanged += OnSubscribersChanged;
        provider.AdvertisementStatusChanged += OnAdvertisementStatusChanged;
        provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsConnectable = true,
            IsDiscoverable = true
        });
        ReportStatus(provider.AdvertisementStatus, BluetoothError.Success);
    }

    private async void OnNameRead(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        using var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            request?.RespondWithValue(MakeBuffer(Encoding.UTF8.GetBytes(DeviceName)));
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
        }
    }

    private static async Task<GattLocalCharacteristic> AddCharacteristic(
        GattServiceProvider service, Guid id, GattLocalCharacteristicParameters parameters)
    {
        var result = await service.Service.CreateCharacteristicAsync(id, parameters);
        if (result.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Could not create a BLE characteristic ({result.Error}).");
        return result.Characteristic;
    }

    internal Peer[] GetPeers()
    {
        if (download is null)
            return [];
        var subscribers = download.SubscribedClients;
        lock (gate)
        {
            return subscribers.Select(client => client.Session.DeviceId.Id).Distinct()
                .Where(names.ContainsKey)
                .Select(id => new Peer(id, names[id]))
                .OrderBy(peer => peer.Name)
                .ToArray();
        }
    }

    internal async Task SendAsync(Peer peer, Transfer transfer, IProgress<int> progress, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var characteristic = download ?? throw new InvalidOperationException("Bluetooth is still starting.");
        var client = characteristic.SubscribedClients.FirstOrDefault(item => item.Session.DeviceId.Id == peer.Id)
            ?? throw new IOException("The Apple device disconnected. Open Pipopu and try again.");

        byte[] data = transfer.Encode();
        uint token = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
        var pending = new PendingSend(token, new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (gate)
        {
            if (!names.ContainsKey(peer.Id) || !outgoing.TryAdd(peer.Id, pending))
                throw new IOException("The Apple device is unavailable or busy.");
        }

        try
        {
            try
            {
                await Indicate(Frame.Control(Frame.Begin, token, data.Length), cancellation);
                int offset = 0;
                while (offset < data.Length)
                {
                    int capacity = client.MaxNotificationSize - 1;
                    if (capacity <= 0)
                        throw new IOException("The Bluetooth connection closed.");
                    int count = Math.Min(capacity, data.Length - offset);
                    byte[] frame = new byte[count + 1];
                    frame[0] = Frame.Chunk;
                    data.AsSpan(offset, count).CopyTo(frame.AsSpan(1));
                    await Indicate(frame, cancellation);
                    offset += count;
                    progress.Report(offset * 100 / data.Length);
                }
                cancellation.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // A peer may have already received some chunks. Ask it to discard them.
                try { await Indicate(Frame.Control(Frame.Abort, token), CancellationToken.None, timeoutSeconds: 1); }
                catch (Exception error) { Debug.WriteLine(error); }
                throw;
            }

            await Indicate(Frame.Control(Frame.End, token), CancellationToken.None);
            await pending.Completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            lock (gate)
                outgoing.Remove(peer.Id);
        }

        async Task Indicate(byte[] frame, CancellationToken cancellationToken, int timeoutSeconds = 15)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frame.Length > client.MaxNotificationSize || pending.Completion.Task.IsCanceled)
                throw new IOException("The Bluetooth connection closed.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var result = await characteristic.NotifyValueAsync(MakeBuffer(frame), client).AsTask(timeout.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Status != GattCommunicationStatus.Success)
                throw new IOException($"Bluetooth delivery failed ({result.Status}).");
        }
    }

    private async void OnWrite(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        using var deferral = args.GetDeferral();
        GattWriteRequest? request = null;
        Transfer? transfer = null;
        bool refreshPeers = false;
        try
        {
            request = await args.GetRequestAsync();
            if (request is null || request.Option != GattWriteOption.WriteWithResponse)
                return;

            string id = args.Session.DeviceId.Id;
            byte[] frame = ReadBuffer(request.Value);
            if (request.Offset != 0 || frame.Length == 0)
                throw new InvalidDataException("Invalid write.");

            lock (gate)
            {
                foreach (var key in incoming.Where(pair => DateTime.UtcNow - pair.Value.LastActivity > TimeSpan.FromSeconds(30))
                             .Select(pair => pair.Key).ToArray())
                    incoming.Remove(key);

                switch (frame[0])
                {
                    case Frame.Begin when incoming.Count < 8 || incoming.ContainsKey(id):
                        incoming[id] = new IncomingTransfer(frame);
                        break;
                    case Frame.Chunk when incoming.TryGetValue(id, out var partial):
                        partial.Append(frame);
                        break;
                    case Frame.End when incoming.Remove(id, out var complete):
                        transfer = complete.Finish(frame);
                        break;
                    case Frame.Available when frame.Length is > 1 and <= 65:
                        if (download?.SubscribedClients.Any(client => client.Session.DeviceId.Id == id) != true)
                            throw new InvalidDataException("Subscribe before registering a receiver.");
                        names[id] = Encoding.UTF8.GetString(frame, 1, frame.Length - 1);
                        refreshPeers = true;
                        break;
                    case Frame.Received when frame.Length == 5 && outgoing.TryGetValue(id, out var pending)
                        && pending.Token == BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(1)):
                        pending.Completion.TrySetResult();
                        break;
                    default:
                        throw new InvalidDataException("Unexpected transfer frame.");
                }
            }

            request.Respond();
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            try { request?.RespondWithProtocolError(0x80); }
            catch (System.Runtime.InteropServices.COMException) { }
            return;
        }

        if (refreshPeers)
            PeersChanged?.Invoke();
        if (transfer is not null)
            TransferReceived?.Invoke(transfer);
    }

    private void OnSubscribersChanged(GattLocalCharacteristic sender, object args)
    {
        var ids = sender.SubscribedClients.Select(client => client.Session.DeviceId.Id).ToHashSet();
        lock (gate)
        {
            foreach (string id in names.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                names.Remove(id);
                incoming.Remove(id);
                if (outgoing.TryGetValue(id, out var pending))
                    pending.Completion.TrySetCanceled();
            }
        }
        PeersChanged?.Invoke();
    }

    private void OnAdvertisementStatusChanged(GattServiceProvider sender, GattServiceProviderAdvertisementStatusChangedEventArgs args)
        => ReportStatus(args.Status, args.Error);

    private void ReportStatus(GattServiceProviderAdvertisementStatus status, BluetoothError error)
    {
        string message = status switch
        {
            GattServiceProviderAdvertisementStatus.Started => "Bluetooth ready",
            GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData =>
                "Bluetooth advertising is limited by the adapter.",
            GattServiceProviderAdvertisementStatus.Aborted => $"Bluetooth advertising stopped ({error}).",
            GattServiceProviderAdvertisementStatus.Stopped => "Bluetooth advertising stopped.",
            _ => "Starting Bluetooth…"
        };
        StatusChanged?.Invoke(message);
    }

    private static IBuffer MakeBuffer(byte[] bytes)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }

    private static byte[] ReadBuffer(IBuffer buffer)
    {
        if (buffer.Length > 512)
            throw new InvalidDataException("The BLE frame is too large.");
        using var reader = DataReader.FromBuffer(buffer);
        byte[] bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    public void Dispose()
    {
        disposed = true;
        if (upload is not null)
            upload.WriteRequested -= OnWrite;
        if (download is not null)
            download.SubscribedClientsChanged -= OnSubscribersChanged;
        if (nameCharacteristic is not null)
            nameCharacteristic.ReadRequested -= OnNameRead;
        if (provider is not null)
        {
            provider.AdvertisementStatusChanged -= OnAdvertisementStatusChanged;
            provider.StopAdvertising();
        }
    }
}
