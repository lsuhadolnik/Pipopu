using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Pipopu;

internal sealed class BluetoothServer : IDisposable
{
    private static readonly Guid ServiceId = new("EA259A40-8B6E-44DA-BEEC-E10D5680D630");
    private static readonly Guid UploadId = new("EA259A41-8B6E-44DA-BEEC-E10D5680D630");
    private static readonly Guid DownloadId = new("EA259A42-8B6E-44DA-BEEC-E10D5680D630");
    private static readonly Guid NameId = new("EA259A43-8B6E-44DA-BEEC-E10D5680D630");
    private const string DeviceNameKey = @"Software\Pipopu";
    private const string DeviceNameValue = "DeviceName";
    private const int MaxDeviceNameBytes = 64;

    internal sealed record Peer(string Id, string Name, int Version)
    {
        public override string ToString() => Name;
    }

    private sealed class Link(GattSubscribedClient client, SecureSession session)
    {
        internal GattSubscribedClient Client { get; } = client;
        internal SecureSession Session { get; } = session;
        internal SemaphoreSlim Writing { get; } = new(1);
        internal string Name = "";
        internal bool Receives;
        internal int? Version;
        internal bool RemoteTrust;
        internal Transaction? Active;
    }

    private sealed class Transaction(string id, CancellationToken cancellation = default) : IDisposable
    {
        internal string Id { get; } = id;
        internal CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        internal Channel<Wire> Mail { get; } = Channel.CreateBounded<Wire>(8);
        internal volatile string? PeerError;

        internal async Task<Wire> NextAsync(TimeSpan timeout)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
            deadline.CancelAfter(timeout);
            var message = await Mail.Reader.ReadAsync(deadline.Token);
            return message;
        }

        public void Dispose() => Lifetime.Dispose();
    }

    private static readonly TimeSpan PairingTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromHours(2);
    private readonly object gate = new();
    private readonly Dictionary<string, IncomingPacket> incoming = new();
    private readonly Dictionary<string, Link> links = new();
    internal TrustStore Trust { get; } = new();
    internal OneDrive Cloud { get; } = new();
    internal CloudTransfers Deferred { get; }

    internal BluetoothServer()
    {
        Deferred = new CloudTransfers(Cloud);
        Deferred.Arrival += transfer => TransferReceived?.Invoke(transfer);
        Deferred.Progress += progress => IncomingTransferProgress?.Invoke(progress);
    }
    internal Func<string, string, CancellationToken, Task<bool>>? ConfirmTrust { get; set; }
    internal event Action<string?>? PairingCodeChanged;

    private GattServiceProvider? provider;
    private GattLocalCharacteristic? upload;
    private GattLocalCharacteristic? download;
    private GattLocalCharacteristic? nameCharacteristic;
    private string deviceName = LoadDeviceName();
    private bool disposed;
    private bool pausedForUpdate;

    internal bool HasActiveTransfer
    {
        get { lock (gate) return links.Values.Any(link => link.Active is not null); }
    }

    internal bool TryPauseForUpdate()
    {
        lock (gate)
        {
            if (links.Values.Any(link => link.Active is not null))
                return false;
            pausedForUpdate = true;
            return true;
        }
    }

    internal void ResumeAfterUpdateFailure()
    {
        lock (gate) pausedForUpdate = false;
    }

    internal event Action<Transfer>? TransferReceived;
    internal event Action<TransferProgress>? IncomingTransferProgress;
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
        lock (gate)
            return links.Where(entry => entry.Value.Receives)
                .Select(entry => new Peer(entry.Key, entry.Value.Name, entry.Value.Version ?? WireProtocol.Legacy))
                .OrderBy(peer => peer.Name).ToArray();
    }

    internal void Forget(string identity)
    {
        Trust.Forget(identity);
        lock (gate)
            foreach (var link in links.Values.Where(link => link.Session.Identity == identity))
                link.Active?.Lifetime.Cancel();
    }

    internal async Task SendAsync(Peer peer, Transfer transfer, IProgress<int> progress, CancellationToken cancellation,
        bool overCloud = false, IProgress<TransferProgress>? networkProgress = null)
    {
        using var transaction = new Transaction(Guid.NewGuid().ToString("N"), cancellation);
        Link link;
        lock (gate)
        {
            if (!links.TryGetValue(peer.Id, out link!) || !link.Receives || link.Active is not null)
                throw new IOException("The selected device is disconnected or busy.");
            if ((transfer.Secure || overCloud) && link.Version < WireProtocol.CloudAndSecure)
                throw new IOException("Update Pipopu on the other device to use secure text or queued cloud sending.");
            if (overCloud)
                Cloud.RequireSignIn();
            link.Active = transaction;
        }

        transaction.Lifetime.CancelAfter(TransferTimeout);
        var token = transaction.Lifetime.Token;
        string id = transaction.Id;
        bool pair = !Trust.Contains(link.Session.Identity);
        bool bulk = overCloud || transfer.Size > Transfer.Limit;
        if (bulk)
            TransferLog.Write($"Send started id={id} bytes={transfer.Size} cloudRequested={overCloud}");
        try
        {
            var offer = new Wire("offer", id, transfer.Name, (int)transfer.Kind, transfer.Size, pair,
                Route: overCloud ? "cloud-deferred" : null, Secure: transfer.Secure);
            Transfer.Validate(offer);
            if (!overCloud || pair || !link.RemoteTrust)
            {
                await SendPacketAsync(link, offer, token);
                var review = await transaction.NextAsync(PairingTimeout);
                Require(review, "review");
                if (pair || review.Pair)
                {
                    PairingCodeChanged?.Invoke(link.Session.Code);
                    await EnsureTrustConfirmedAsync(link, token);
                }
                Require(await transaction.NextAsync(PairingTimeout), "accept");
                PairingCodeChanged?.Invoke(null);
                await SendPacketAsync(link, new Wire("verified", id), token);
                if (pair || review.Pair)
                    Trust.Remember(link.Session.Identity, link.Name);
            }

            if (overCloud)
            {
                Release(link, transaction);
                await Deferred.EnqueueAsync(id, link.Session.Identity, link.Name, transfer, cancellation);
                TransferLog.Write($"Send queued id={id}");
                return;
            }

            if (transfer.Size <= Transfer.Limit)
            {
                await using var input = transfer.OpenRead();
                var bytes = new byte[(int)transfer.Size];
                await input.ReadExactlyAsync(bytes, token);
                if (input.ReadByte() != -1)
                    throw new IOException("The file changed while Pipopu was reading it.");
                await SendPacketAsync(link, new Wire("data", id, Data: bytes), token, progress);
                Require(await transaction.NextAsync(TimeSpan.FromSeconds(30)), "complete");
            }
            else
            {
                var network = new TransferProgressReporter(id, transfer.Name, FileCipher.EncryptedSize(transfer.Size),
                    value => networkProgress?.Report(value));
                network.Update("Encrypting file…");
                await using var input = transfer.OpenRead();
                using var encrypted = await FileCipher.TransformAsync(input, transfer.Size, link.Session.FileKey(id), true, token);
                network.Update("Checking local network…");
                if (!await TryLanAsync(link, transaction, encrypted, upload: false, progress,
                    bytes => network.Update("Sending over local network", bytes)))
                {
                    TransferLog.Write($"Send falling back to OneDrive id={id}");
                    Cloud.RequireSignIn();
                    Uri? session = null;
                    try
                    {
                        network.Update("Acquiring OneDrive upload link…");
                        session = await Cloud.CreateUploadAsync(id, token);
                        const string uploading = "Uploading to OneDrive";
                        network.Update(uploading, 0);
                        await Cloud.UploadAsync(session, encrypted, token, progress, bytes => network.Update(uploading, bytes));
                        network.Update("Waiting for recipient to download…");
                        var url = await Cloud.DownloadUrlAsync(id, token);
                        await SendPacketAsync(link, new Wire("route", id, Urls: [url.AbsoluteUri], Route: "cloud"), token);
                        Require(await transaction.NextAsync(TransferTimeout), "complete");
                    }
                    finally { if (session is not null) await Cloud.CleanupAsync(id, session); }
                }
                network.Finish();
            }
            if (bulk)
                TransferLog.Write($"Send completed id={id}");
        }
        catch (Exception error)
        {
            if (bulk)
                TransferLog.Write($"Send failed id={id} reason={error.GetType().Name} peerReported={transaction.PeerError is not null}");
            if (transaction.PeerError is { } message)
                throw new IOException(message, error);
            await ReplyBestEffortAsync(link, new Wire(cancellation.IsCancellationRequested ? "cancel" : "fail",
                id, Route: error is OneDriveSignInRequiredException ? OneDriveSignInRequiredException.Route : null,
                Error: error.Message));
            throw;
        }
        finally
        {
            PairingCodeChanged?.Invoke(null);
            Release(link, transaction);
        }
    }

    private async Task ReceiveAsync(Link link, Transaction transaction, Wire offer)
    {
        using (transaction)
        {
            transaction.Lifetime.CancelAfter(TransferTimeout);
            var token = transaction.Lifetime.Token;
            string id = transaction.Id;
            TransferFile? received = null;
            TransferProgressReporter? incoming = null;
            bool queued = false;
            bool bulk = offer.Size > Transfer.Limit || offer.Route == "cloud-deferred";
            try
            {
                Transfer.Validate(offer);
                if ((offer.Secure || offer.Route == "cloud-deferred") && link.Version < WireProtocol.CloudAndSecure)
                    throw new IOException("This transfer requires a newer version of Pipopu.");
                if (bulk)
                    TransferLog.Write($"Receive started id={id} bytes={offer.Size} cloudRequested={offer.Route == "cloud-deferred"}");
                if (offer.Route == "cloud-deferred")
                    Cloud.RequireSignIn();
                bool pair = !Trust.Contains(link.Session.Identity) || offer.Pair;
                await SendPacketAsync(link, new Wire("review", id, Pair: pair), token);
                if (pair)
                    await EnsureTrustConfirmedAsync(link, token);
                await SendPacketAsync(link, new Wire("accept", id), token);
                Require(await transaction.NextAsync(PairingTimeout), "verified");
                token.ThrowIfCancellationRequested();
                if (pair)
                    Trust.Remember(link.Session.Identity, link.Name);

                if (bulk)
                    incoming = new TransferProgressReporter(id, string.IsNullOrWhiteSpace(offer.Name) ? "Incoming message" : offer.Name,
                        FileCipher.EncryptedSize(offer.Size), value => IncomingTransferProgress?.Invoke(value));

                if (offer.Route == "cloud-deferred")
                {
                    incoming!.Update("Acquiring OneDrive upload link…");
                    var message = await transaction.NextAsync(PairingTimeout);
                    Require(message, "cloud-key");
                    if (message.Data is not { Length: 32 } key)
                        throw new InvalidDataException("The cloud encryption key is missing.");
                    Uri upload = await Deferred.PrepareIncomingAsync(link.Session.Identity, link.Name, offer, key, token);
                    queued = true;
                    Deferred.TrackIncoming(id, incoming);
                    await SendPacketAsync(link, new Wire("cloud-upload", id, Urls: [upload.AbsoluteUri], Route: "cloud"), token);
                    TransferLog.Write($"Receive cloud ticket sent id={id}");
                    return;
                }

                var kind = (TransferKind)offer.Kind;
                Transfer transfer;
                if (offer.Size <= Transfer.Limit)
                {
                    var message = await transaction.NextAsync(TimeSpan.FromMinutes(5));
                    Require(message, "data");
                    if (message.Data is not { } bytes || bytes.LongLength != offer.Size)
                        throw new InvalidDataException("The payload size did not match the offer.");
                    transfer = new Transfer(kind, offer.Name!, bytes, Secure: offer.Secure);
                    if (kind == TransferKind.Text)
                        _ = transfer.Text;
                }
                else
                {
                    long encryptedSize = FileCipher.EncryptedSize(offer.Size);
                    using var lanFile = new TransferFile(TransferFile.TemporaryPath(), encryptedSize);
                    incoming!.Update("Waiting for local network upload…");
                    if (await TryLanAsync(link, transaction, lanFile, upload: true,
                        bytesMoved: bytes => incoming.Update("Receiving over local network", bytes)))
                    {
                        incoming.Update("Verifying file…");
                        await using var input = File.OpenRead(lanFile.Path);
                        received = await FileCipher.TransformAsync(input, offer.Size, link.Session.FileKey(id), false, token);
                    }
                    else
                    {
                        TransferLog.Write($"Receive falling back to OneDrive id={id}");
                        Cloud.RequireSignIn();
                        string cloudId = Guid.NewGuid().ToString("N");
                        Uri? session = null;
                        try
                        {
                            incoming.Update("Acquiring OneDrive upload link…");
                            session = await Cloud.CreateUploadAsync(cloudId, token);
                            using (var monitor = CancellationTokenSource.CreateLinkedTokenSource(token))
                            {
                                incoming.Update(CloudTransfers.AwaitingUpload, 0);
                                var monitorTask = MonitorUploadAsync(session, encryptedSize, incoming, monitor.Token);
                                try
                                {
                                    await SendPacketAsync(link, new Wire("route", id, Urls: [session.AbsoluteUri], Route: "cloud"), token);
                                    Require(await transaction.NextAsync(TransferTimeout), "uploaded");
                                }
                                finally
                                {
                                    await monitor.CancelAsync();
                                    await monitorTask;
                                }
                            }
                            incoming.Update(CloudTransfers.Downloading, 0);
                            using var cloudFile = await Cloud.DownloadAsync(cloudId, encryptedSize, token,
                                bytes => incoming.Update(CloudTransfers.Downloading, bytes));
                            incoming.Update("Verifying file…");
                            await using var input = File.OpenRead(cloudFile.Path);
                            received = await FileCipher.TransformAsync(input, offer.Size, link.Session.FileKey(id), false, token);
                        }
                        finally { if (session is not null) await Cloud.CleanupAsync(cloudId, session); }
                    }
                    if (kind == TransferKind.Text)
                    {
                        transfer = new Transfer(kind, offer.Name!, File.ReadAllBytes(received.Path));
                        _ = transfer.Text;
                        received.Dispose();
                        received = null;
                    }
                    else
                    {
                        transfer = new Transfer(kind, offer.Name!, [], received);
                    }
                }

                token.ThrowIfCancellationRequested();
                incoming?.Finish();
                TransferReceived?.Invoke(transfer);
                received = null;
                await SendPacketAsync(link, new Wire("complete", id), token);
                if (bulk)
                    TransferLog.Write($"Receive completed id={id}");
            }
            catch (Exception error)
            {
                if (bulk)
                    TransferLog.Write($"Receive failed id={id} reason={error.GetType().Name} peerReported={transaction.PeerError is not null}");
                if (!queued)
                    incoming?.Finish(transaction.PeerError ?? (error is OperationCanceledException or TimeoutException
                        ? "The sending device stopped responding." : error.Message));
                if (transaction.PeerError is null)
                    await ReplyBestEffortAsync(link, new Wire("fail", id, Error: error.Message,
                        Route: error is OneDriveSignInRequiredException ? OneDriveSignInRequiredException.Route : null));
            }
            finally
            {
                received?.Dispose();
                Release(link, transaction);
            }
        }
    }

    private static async Task MonitorUploadAsync(Uri session, long size, TransferProgressReporter progress,
        CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation))
            {
                try
                {
                    var status = await OneDrive.UploadStatusAsync(session, cancellation);
                    if (status.Bytes is long bytes)
                        progress.Update(CloudTransfers.AwaitingUpload, Math.Min(bytes, size));
                }
                catch (Exception error) when (error is IOException or HttpRequestException or System.Text.Json.JsonException)
                {
                    Debug.WriteLine(error.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task<bool> TryLanAsync(Link link, Transaction transaction, TransferFile file, bool upload,
        IProgress<int>? progress = null, Action<long>? bytesMoved = null)
    {
        var token = transaction.Lifetime.Token;
        LanTransfer server;
        try { server = await LanTransfer.OpenAsync(file, upload, token, progress, bytesMoved); }
        catch (Exception error) when (!token.IsCancellationRequested && error is IOException or System.Net.Sockets.SocketException)
        {
            return false;
        }
        await using (server)
        {
            await SendPacketAsync(link, new Wire("route", transaction.Id, Urls: server.Urls, Route: "lan"), token);
            var reply = await transaction.NextAsync(TransferTimeout);
            if (reply.Type == "fallback")
                return false;
            Require(reply, upload ? "uploaded" : "complete");
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10), token);
            return true;
        }
    }

    private async Task EnsureTrustConfirmedAsync(Link link, CancellationToken cancellation)
    {
        using var prompt = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        prompt.CancelAfter(PairingTimeout);
        if (ConfirmTrust is null || !await ConfirmTrust(link.Name, link.Session.Code, prompt.Token))
            throw new IOException("The pairing request was declined.");
        cancellation.ThrowIfCancellationRequested();
    }

    private static void Require(Wire message, string type)
    {
        if (message.Type != type)
            throw new InvalidDataException($"Expected {type} from the other device.");
    }

    private void Release(Link link, Transaction transaction)
    {
        lock (gate)
            if (link.Active == transaction)
                link.Active = null;
    }

    private async Task ReplyBestEffortAsync(Link link, Wire message)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await SendPacketAsync(link, message, deadline.Token);
        }
        catch (Exception error) { Debug.WriteLine(error); }
    }

    private async Task SendPacketAsync(Link link, Wire packet, CancellationToken cancellation, IProgress<int>? progress = null)
    {
        await link.Writing.WaitAsync(cancellation);
        try
        {
            if (packet.Type != "ready")
                packet = packet with { Version = link.Version ?? throw new IOException("Pairing protocol is unavailable.") };
            await IndicateAsync(link.Client, link.Session.Seal(packet), cancellation, progress);
        }
        finally { link.Writing.Release(); }
    }

    private async Task IndicateAsync(GattSubscribedClient client, byte[] packet, CancellationToken cancellation,
        IProgress<int>? progress = null)
    {
        var characteristic = download ?? throw new IOException("Bluetooth is unavailable.");
        uint token = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
        await FrameAsync(Frame.Control(Frame.Begin, token, packet.Length));
        int previous = -1;
        for (int offset = 0; offset < packet.Length;)
        {
            int count = Math.Min(client.MaxNotificationSize - 1, packet.Length - offset);
            if (count <= 0)
                throw new IOException("The Bluetooth connection closed.");
            var frame = new byte[count + 1];
            frame[0] = Frame.Chunk;
            packet.AsSpan(offset, count).CopyTo(frame.AsSpan(1));
            await FrameAsync(frame);
            offset += count;
            int percent = 95 * offset / packet.Length;
            if (percent != previous)
            {
                previous = percent;
                progress?.Report(percent);
            }
        }
        await FrameAsync(Frame.Control(Frame.End, token));

        async Task FrameAsync(byte[] frame)
        {
            cancellation.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var result = await characteristic.NotifyValueAsync(MakeBuffer(frame), client).AsTask(deadline.Token);
            if (result.Status != GattCommunicationStatus.Success)
                throw new IOException("The Bluetooth connection closed.");
        }
    }

    private async void OnWrite(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        GattWriteRequest? request = null;
        Task? dispatch = null;
        string id = "";
        using (args.GetDeferral())
        {
            try
            {
                byte[]? packet = null;
                request = await args.GetRequestAsync();
                if (request is null || request.Option != GattWriteOption.WriteWithResponse)
                    return;
                id = args.Session.DeviceId.Id;
                if (request.Offset != 0 || request.Value.Length is < 1 or > 512)
                    throw new InvalidDataException("Invalid Bluetooth frame.");
                using var reader = DataReader.FromBuffer(request.Value);
                byte[] frame = new byte[request.Value.Length];
                reader.ReadBytes(frame);
                lock (gate)
                {
                    foreach (var stale in incoming.Where(entry => DateTime.UtcNow - entry.Value.LastActivity > TimeSpan.FromSeconds(30))
                        .Select(entry => entry.Key).ToArray())
                        incoming.Remove(stale);
                    switch (frame[0])
                    {
                        case Frame.Begin when incoming.Count < 16 || incoming.ContainsKey(id):
                            incoming[id] = new IncomingPacket(frame);
                            break;
                        case Frame.Chunk when incoming.TryGetValue(id, out var partial):
                            partial.Append(frame);
                            break;
                        case Frame.End when incoming.Remove(id, out var complete):
                            packet = complete.Finish(frame);
                            break;
                        default:
                            throw new InvalidDataException("Unexpected Bluetooth frame.");
                    }
                }
                if (packet is not null)
                    dispatch = DispatchAsync(id, packet);
                request.Respond();
            }
            catch (Exception error)
            {
                Debug.WriteLine(error);
                try { request?.RespondWithProtocolError(0x80); }
                catch (System.Runtime.InteropServices.COMException) { }
                return;
            }
        }

        if (dispatch is null)
            return;
        try { await dispatch; }
        catch (Exception error) { Debug.WriteLine(error); }
    }

    private async Task DispatchAsync(string id, byte[] packet)
    {
        if (packet[0] == 1 && packet.Length == 98)
        {
            var client = download?.SubscribedClients.FirstOrDefault(client => client.Session.DeviceId.Id == id);
            if (client is null)
                return;
            byte[] welcome = [2, .. Trust.PublicKey, .. RandomNumberGenerator.GetBytes(32)];
            var established = new Link(client, new SecureSession(Trust, packet, welcome));
            lock (gate)
            {
                if (links.Count >= 16 || links.ContainsKey(id))
                    return;
                links[id] = established;
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await IndicateAsync(client, welcome, deadline.Token); }
            catch
            {
                lock (gate) links.Remove(id);
                throw;
            }
            return;
        }

        Link link;
        Wire message;
        Transaction? inbound = null;
        bool busy = false;
        bool incompatible = false;
        bool cloudCommand = false;
        lock (gate)
        {
            if (!links.TryGetValue(id, out link!))
                return;
            message = link.Session.Open(packet);
            if (message.Type == "ready" && link.Active is null &&
                message.Name is { } name && IsValidName(name))
            {
                int? version = WireProtocol.Negotiate(message.ProtocolMin, message.ProtocolMax);
                incompatible = version is null || (link.Version is int previous && previous != version);
                if (!incompatible)
                {
                    link.Version = version;
                    link.Name = name;
                    link.Receives = message.Receive;
                    link.RemoteTrust = !message.Pair;
                }
            }
            else if (link.Version is null || (message.Version ?? WireProtocol.Legacy) != link.Version)
            {
                incompatible = true;
                if (link.Active is { } transfer)
                {
                    transfer.PeerError = "The other device changed the transfer protocol.";
                    transfer.Lifetime.Cancel();
                }
            }
            else if (link.Version >= WireProtocol.CloudAndSecure &&
                (message.Type is "cloud-list" or "cloud-fetch" or "cloud-delivered" or "cloud-upload-error"))
            {
                cloudCommand = Trust.Contains(link.Session.Identity);
            }
            else if (message.Type == "offer" && message.Id is { } transferId && link.Name.Length > 0)
            {
                if (!pausedForUpdate && link.Active is null && links.Values.Count(peer => peer.Active is not null) < 4)
                    link.Active = inbound = new Transaction(transferId);
                else
                    busy = true;
            }
            else if (link.Active is { } active && active.Id == message.Id)
            {
                if (message.Type is "fail" or "cancel")
                {
                    active.PeerError = message.Type == "cancel" ? "The other device cancelled the transfer." :
                        message.Error ?? "The other device declined the transfer.";
                    active.Lifetime.Cancel();
                }
                else if (!active.Mail.Writer.TryWrite(message))
                    active.Lifetime.Cancel();
                return;
            }
            else
                return;
        }

        if (incompatible || message.Type == "ready")
        {
            await ReplyBestEffortAsync(link, new Wire("ready", Name: DeviceName,
                ProtocolMin: WireProtocol.Minimum, ProtocolMax: WireProtocol.Current,
                Version: incompatible ? null : link.Version,
                Error: incompatible ? "Update Pipopu on both devices to use a compatible transfer protocol." : null));
            if (!incompatible)
                PeersChanged?.Invoke();
        }
        else if (inbound is not null)
            await ReceiveAsync(link, inbound, message);
        else if (cloudCommand)
            await HandleCloudAsync(link, message);
        else if (busy)
            await ReplyBestEffortAsync(link, new Wire("fail", message.Id, Error: "The other device is busy. Try again in a moment."));
    }

    private async Task HandleCloudAsync(Link link, Wire message)
    {
        try
        {
            switch (message.Type)
            {
                case "cloud-list":
                    await ReplyBestEffortAsync(link, new Wire("cloud-inbox", Data: Deferred.Inbox(link.Session.Identity)));
                    break;
                case "cloud-fetch" when message.Id is { } id:
                    using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    {
                        var ticket = await Deferred.DownloadTicketAsync(link.Session.Identity, id, deadline.Token);
                        await SendPacketAsync(link, ticket, deadline.Token);
                    }
                    break;
                case "cloud-delivered" when message.Id is { } id:
                    Deferred.Acknowledge(link.Session.Identity, id);
                    break;
                case "cloud-upload-error" when message.Id is { } id:
                    Deferred.UploadFailed(link.Session.Identity, id, message.Error);
                    break;
            }
        }
        catch (Exception error)
        {
            await ReplyBestEffortAsync(link, new Wire("cloud-error", message.Id, Error: error.Message));
        }
    }

    private void OnSubscribersChanged(GattLocalCharacteristic sender, object args)
    {
        var present = sender.SubscribedClients.Select(client => client.Session.DeviceId.Id).ToHashSet();
        lock (gate)
        {
            foreach (string id in links.Keys.Where(id => !present.Contains(id)).ToArray())
            {
                var link = links[id];
                links.Remove(id);
                incoming.Remove(id);
                link.Active?.Lifetime.Cancel();
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

    public void Dispose()
    {
        disposed = true;
        lock (gate)
            foreach (var link in links.Values)
                link.Active?.Lifetime.Cancel();
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
