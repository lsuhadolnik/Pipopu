using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Pipopu;

internal sealed record CloudJob(
    string Id, string RemoteId, string Identity, string Peer, string Drive, string Name, int Kind, long Size, byte[] Key,
    bool Outgoing, string Status, string? Error = null, string? UploadUrl = null, DateTimeOffset? UploadMissingSince = null);

internal sealed record CloudSummary(string Id, string Peer, string Name, bool Outgoing, string Status, string? Error)
{
    public string Caption => Error is null ? $"{Name} · {Peer} · {Status}" : $"{Name} · {Peer} · {Status}: {Error}";
}

internal sealed class CloudTransfers(OneDrive oneDrive, string? directory = null)
{
    internal const string Queued = "Queued";
    internal const string Uploading = "Uploading";
    internal const string Waiting = "Waiting for upload";
    internal const string Ready = "Ready";
    internal const string Received = "Received";
    internal const string Failed = "Failed";
    internal const string AwaitingUpload = "Awaiting remote OneDrive upload…";
    internal const string Downloading = "Downloading from OneDrive…";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, byte> running = new();
    private readonly ConcurrentDictionary<string, TransferProgressReporter> incoming = new();
    private readonly string folder = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pipopu", "cloud");
    private Dictionary<string, CloudJob>? jobs;

    internal event Action<Transfer>? Arrival;
    internal event Action<TransferProgress>? Progress;
    internal event Action? Changed;

    internal void TrackIncoming(string id, TransferProgressReporter progress)
    {
        var tracker = incoming.GetOrAdd(id, progress);
        tracker.Update(AwaitingUpload, 0);
    }

    private TransferProgressReporter Incoming(CloudJob job) => incoming.GetOrAdd(job.Id, _ =>
        new TransferProgressReporter(job.Id, string.IsNullOrWhiteSpace(job.Name) ? "Incoming message" : job.Name,
            FileCipher.EncryptedSize(job.Size), value => Progress?.Invoke(value)));

    private void FinishIncoming(CloudJob job, string? error = null)
    {
        if (incoming.TryRemove(job.Id, out var tracker))
            tracker.Finish(error);
    }

    internal CloudSummary[] List(string? identity = null)
    {
        lock (gate)
            return Jobs.Values.Where(job => identity is null || job.Identity == identity).Reverse().Take(40)
                .Select(job => new CloudSummary(job.Id, job.Peer, job.Name, job.Outgoing, job.Status, job.Error)).ToArray();
    }

    internal byte[] Inbox(string identity) => JsonSerializer.SerializeToUtf8Bytes(List(identity), Json);

    internal async Task EnqueueAsync(
        string id, string identity, string peer, Transfer transfer, CancellationToken cancellation)
    {
        if (transfer.Size <= 0)
            throw new IOException("Choose a nonempty file or message for cloud delivery.");
        if (!oneDrive.IsSignedIn)
            throw new IOException("Sign in to OneDrive in Pipopu Settings before sending over cloud.");

        string drive = await oneDrive.DriveIdAsync(cancellation);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        await using var input = transfer.OpenRead();
        using var encrypted = await FileCipher.TransformAsync(input, transfer.Size, key, true, cancellation);
        cancellation.ThrowIfCancellationRequested();
        Directory.CreateDirectory(folder);
        string path = DataPath(id);
        File.Move(encrypted.Path, path);
        try
        {
            Put(new CloudJob(id, Guid.NewGuid().ToString("N"), identity, peer, drive, transfer.Name, (int)transfer.Kind, transfer.Size,
                key, true, Queued), add: true);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    internal async Task<Uri> PrepareIncomingAsync(
        string identity, string peer, Wire offer, byte[] key, CancellationToken cancellation)
    {
        if (key.Length != 32 || offer.Size <= 0)
            throw new InvalidDataException("Invalid cloud file.");
        string id = offer.Id!;
        string drive = await oneDrive.DriveIdAsync(cancellation);
        lock (gate)
            if (Jobs.ContainsKey(id))
                throw new IOException("A cloud transfer with that ID already exists.");

        string remoteId = Guid.NewGuid().ToString("N");
        Uri upload = await oneDrive.CreateUploadAsync(remoteId, cancellation);
        try
        {
            Put(new CloudJob(id, remoteId, identity, peer, drive, offer.Name!, offer.Kind, offer.Size, key, false,
                Waiting, UploadUrl: upload.AbsoluteUri), add: true);
            return upload;
        }
        catch
        {
            await oneDrive.CancelSessionAsync(upload);
            throw;
        }
    }

    internal async Task<Wire> DownloadTicketAsync(string identity, string id, CancellationToken cancellation)
    {
        var job = Find(id);
        if (job is null || !job.Outgoing || job.Status != Ready || job.Identity != identity)
            throw new IOException("This cloud file is not ready to download.");
        if (await oneDrive.DriveIdAsync(cancellation) != job.Drive)
            throw new IOException("Sign in to the OneDrive account used for this transfer.");
        Uri url = await oneDrive.DownloadUrlAsync(job.RemoteId, cancellation);
        return new Wire("cloud-ticket", id, job.Name, job.Kind, job.Size, Data: job.Key, Urls: [url.AbsoluteUri]);
    }

    internal void Acknowledge(string identity, string id)
    {
        var job = Find(id);
        if (job is { Outgoing: true, Status: Ready } && job.Identity == identity)
            Put(job with { Status = Received });
    }

    internal void UploadFailed(string identity, string id, string? error)
    {
        lock (gate)
        {
            if (Jobs.GetValueOrDefault(id) is { Outgoing: false, Status: Waiting } job && job.Identity == identity)
            {
                string message = SafeError(error);
                Put(job with { Error = message });
                if (incoming.TryGetValue(id, out var progress))
                    progress.Update(message);
            }
        }
    }

    internal void Retry(string id)
    {
        var job = Find(id);
        if (job is { Outgoing: true, Status: Failed } && File.Exists(DataPath(id)))
            Put(job with { Status = Queued, Error = null });
    }

    internal async Task<Transfer> OpenReceivedAsync(string id, CancellationToken cancellation)
    {
        var job = Find(id);
        if (job is not { Outgoing: false, Status: Received })
            throw new IOException("This file is still waiting for upload or verification.");
        return await OpenAsync(job, cancellation);
    }

    internal async Task PumpAsync(CancellationToken cancellation)
    {
        CloudJob[] pending;
        lock (gate)
            pending = Jobs.Values.Where(job => !running.ContainsKey(job.Id) &&
                (job.Status is Queued or Uploading or Waiting || (job.Status == Received && job.UploadUrl is not null))).ToArray();
        if (pending.Length == 0)
            return;
        if (!oneDrive.IsSignedIn)
        {
            foreach (var job in pending.Where(job => job.Status == Waiting))
                Incoming(job).Update("Sign in to OneDrive in Pipopu Settings to continue.");
            return;
        }

        string drive = await oneDrive.DriveIdAsync(cancellation);
        foreach (var job in pending.Where(job => job.Status == Waiting && job.Drive != drive))
            Incoming(job).Update("Sign in to the OneDrive account used for this transfer.");
        await Task.WhenAll(pending.Where(job => job.Drive == drive).Select(job => ProcessAsync(job, cancellation)));
    }

    private async Task ProcessAsync(CloudJob snapshot, CancellationToken cancellation)
    {
        if (!running.TryAdd(snapshot.Id, 0))
            return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromHours(2));
        var token = deadline.Token;
        try
        {
            var job = Find(snapshot.Id);
            if (job is null || job.Status != snapshot.Status)
                return;
            if (job.Status is Queued or Uploading)
                await UploadAsync(job, token);
            else if (job.Status == Waiting)
                await ReceiveAsync(job, token);
            else if (job.UploadUrl is not null)
            {
                await oneDrive.RemoveStagedFileAsync(job.RemoteId, token);
                Put(job with { UploadUrl = null });
            }
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        {
            if (Find(snapshot.Id) is { Status: Queued or Uploading or Waiting } current)
            {
                if (current.Status == Waiting && error is HttpRequestException)
                {
                    Incoming(current).Update("OneDrive is temporarily unreachable. Retrying…");
                    return;
                }
                string message = SafeError(error.Message);
                Put(current with { Status = Failed, Error = message });
                FinishIncoming(current, message);
            }
        }
        finally { running.TryRemove(snapshot.Id, out _); }
    }

    private async Task UploadAsync(CloudJob job, CancellationToken cancellation)
    {
        Put(job with { Status = Uploading });
        long size = FileCipher.EncryptedSize(job.Size);
        if (!await oneDrive.StagedFileExistsAsync(job.RemoteId, size, cancellation))
        {
            Uri upload = await oneDrive.CreateUploadAsync(job.RemoteId, cancellation);
            try
            {
                using var encrypted = new TransferFile(DataPath(job.Id), size, owned: false);
                await oneDrive.UploadAsync(upload, encrypted, cancellation, null);
            }
            catch
            {
                await oneDrive.CancelSessionAsync(upload);
                throw;
            }
        }
        // Keep the marker until the recipient acknowledges receipt and the cloud object can be removed.
        Put(job with { Status = Ready, UploadUrl = "" });
        File.Delete(DataPath(job.Id));
    }

    private async Task ReceiveAsync(CloudJob job, CancellationToken cancellation)
    {
        long size = FileCipher.EncryptedSize(job.Size);
        var progress = Incoming(job);
        string stage = job.Error ?? AwaitingUpload;
        progress.Update(stage);
        if (!await oneDrive.StagedFileExistsAsync(job.RemoteId, size, cancellation))
        {
            if (job.UploadUrl is null)
                return;
            (bool Exists, long? Bytes) status;
            using var check = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            check.CancelAfter(TimeSpan.FromSeconds(8));
            try { status = await OneDrive.UploadStatusAsync(new Uri(job.UploadUrl), check.Token); }
            catch (Exception error) when (error is IOException or HttpRequestException or JsonException) { return; }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return; }
            if (status.Exists)
            {
                if (job.UploadMissingSince is not null)
                    Put(job with { UploadMissingSince = null });
                if (status.Bytes is long bytes)
                    progress.Update(stage, Math.Min(bytes, size));
            }
            else
            {
                if (await oneDrive.StagedFileExistsAsync(job.RemoteId, size, cancellation))
                {
                    await CompleteIncomingAsync(job, size, cancellation);
                    return;
                }
                if (job.UploadMissingSince is null)
                    Put(job with { UploadMissingSince = DateTimeOffset.UtcNow });
                else if (DateTimeOffset.UtcNow - job.UploadMissingSince >= TimeSpan.FromMinutes(1))
                {
                    string message = job.Error ?? "The OneDrive upload session expired.";
                    Put(job with { Status = Failed, Error = message });
                    FinishIncoming(job, message);
                }
            }
            return;
        }
        await CompleteIncomingAsync(job, size, cancellation);
    }

    private async Task CompleteIncomingAsync(CloudJob job, long size, CancellationToken cancellation)
    {
        var progress = Incoming(job);
        progress.Update(Downloading, 0);
        using var encrypted = await oneDrive.DownloadAsync(job.RemoteId, size, cancellation,
            bytes => progress.Update(Downloading, bytes));
        progress.Update("Verifying file…");
        Directory.CreateDirectory(folder);
        File.Move(encrypted.Path, DataPath(job.Id), overwrite: true);
        var transfer = await OpenAsync(job, cancellation);
        if (Find(job.Id)?.Status != Waiting)
        {
            transfer.File?.Dispose();
            return;
        }
        Put(job with { Status = Received, Error = null });
        FinishIncoming(job);
        if (Arrival is { } arrival)
            arrival(transfer);
        else
            transfer.File?.Dispose();
    }

    private async Task<Transfer> OpenAsync(CloudJob job, CancellationToken cancellation)
    {
        await using var source = File.OpenRead(DataPath(job.Id));
        var clear = await FileCipher.TransformAsync(source, job.Size, job.Key, false, cancellation);
        var kind = (TransferKind)job.Kind;
        if (kind != TransferKind.Text)
            return new Transfer(kind, job.Name, [], clear);
        using (clear)
        {
            var transfer = new Transfer(kind, job.Name, await File.ReadAllBytesAsync(clear.Path, cancellation));
            _ = transfer.Text;
            return transfer;
        }
    }

    private CloudJob? Find(string id) { lock (gate) return Jobs.GetValueOrDefault(id); }

    private Dictionary<string, CloudJob> Jobs
    {
        get
        {
            if (jobs is not null)
                return jobs;
            string journal = Path.Combine(folder, "jobs");
            jobs = File.Exists(journal)
                ? (JsonSerializer.Deserialize<CloudJob[]>(ProtectedData.Unprotect(File.ReadAllBytes(journal), null,
                    DataProtectionScope.CurrentUser), Json) ?? []).ToDictionary(job => job.Id)
                : new();
            return jobs;
        }
    }

    private void Put(CloudJob job, bool add = false)
    {
        lock (gate)
        {
            if (add && Jobs.ContainsKey(job.Id))
                throw new IOException("This cloud transfer has already been queued.");
            var updated = new Dictionary<string, CloudJob>(Jobs) { [job.Id] = job };
            Directory.CreateDirectory(folder);
            string journal = Path.Combine(folder, "jobs");
            byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(updated.Values, Json);
            try
            {
                File.WriteAllBytes(journal + ".tmp", ProtectedData.Protect(serialized, null, DataProtectionScope.CurrentUser));
                File.Move(journal + ".tmp", journal, overwrite: true);
                jobs = updated;
            }
            finally { CryptographicOperations.ZeroMemory(serialized); }
        }
        Changed?.Invoke();
    }

    private string DataPath(string id) => Path.Combine(folder, Guid.ParseExact(id, "N").ToString("N") + ".bin");

    private static string SafeError(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "The transfer could not be completed." : message[..Math.Min(message.Length, 240)];
}
