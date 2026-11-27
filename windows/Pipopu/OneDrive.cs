using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Pipopu;

internal sealed class OneDrive
{
    private const string Identity = "https://login.microsoftonline.com/common/oauth2/v2.0/";
    private const string Graph = "https://graph.microsoft.com/v1.0";
    private const string Scope = "https://graph.microsoft.com/Files.ReadWrite offline_access";
    private const string ClientId = "bdd12f15-6b87-42a7-8d1d-fa2aeea32793";
    private const int UploadBlock = 10 * 320 * 1024;
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim authentication = new(1);
    private string? access;
    private string? refresh;
    private DateTimeOffset expires;

    internal bool IsSignedIn => Volatile.Read(ref access) is not null;

    internal async Task SignInAsync(Action<string, Uri> showCode, CancellationToken cancellation)
    {
        await authentication.WaitAsync(cancellation);
        try
        {
            using var grant = await PostAsync(Identity + "devicecode", new() { ["client_id"] = ClientId, ["scope"] = Scope }, cancellation);
            var root = grant.RootElement;
            string code = root.GetProperty("device_code").GetString()!;
            var interval = TimeSpan.FromSeconds(root.GetProperty("interval").GetInt32());
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(root.GetProperty("expires_in").GetInt32()));
            showCode(root.GetProperty("user_code").GetString()!, Https(root.GetProperty("verification_uri").GetString()));
            while (true)
            {
                await Task.Delay(interval, deadline.Token);
                using var reply = await PostAsync(Identity + "token", new()
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = ClientId,
                    ["device_code"] = code
                }, deadline.Token, allowError: true);
                switch (reply.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null)
                {
                    case "authorization_pending": continue;
                    case "slow_down": interval += TimeSpan.FromSeconds(5); continue;
                    case not null: throw new IOException("Microsoft did not authorize OneDrive access.");
                    default:
                        refresh = null;
                        SetToken(reply.RootElement);
                        return;
                }
            }
        }
        finally { authentication.Release(); }
    }

    internal async Task SignOutAsync()
    {
        await authentication.WaitAsync();
        try { access = refresh = null; }
        finally { authentication.Release(); }
    }

    internal async Task<Uri> CreateUploadAsync(string id, CancellationToken cancellation)
        => await CreateUploadAsync(Item(id), """{"item":{"@microsoft.graph.conflictBehavior":"fail"}}""", cancellation);

    private async Task<Uri> CreateUploadAsync(string path, string options, CancellationToken cancellation)
    {
        using var content = new StringContent(options, Encoding.UTF8, "application/json");
        using var result = await GraphAsync(HttpMethod.Post, path + ":/createUploadSession", content, cancellation);
        return Https(result.RootElement.GetProperty("uploadUrl").GetString());
    }

    internal async Task UploadAsync(Uri url, TransferFile file, CancellationToken cancellation, IProgress<int>? progress)
    {
        await using var input = File.OpenRead(file.Path);
        await UploadAsync(url, input, file.Size, cancellation, progress);
    }

    private static async Task UploadAsync(Uri url, Stream input, long size, CancellationToken cancellation, IProgress<int>? progress)
    {
        var block = new byte[UploadBlock];
        for (long offset = 0; offset < size;)
        {
            int count = (int)Math.Min(block.Length, size - offset);
            await input.ReadExactlyAsync(block.AsMemory(0, count), cancellation);
            using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(block, 0, count) };
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, size);
            using var response = await Http.SendAsync(request, cancellation);
            offset += count;
            if (offset < size ? response.StatusCode != HttpStatusCode.Accepted :
                response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
                throw new IOException($"OneDrive upload failed ({(int)response.StatusCode}).");
            progress?.Report((int)(90 * offset / size));
        }
    }

    internal async Task<OneDriveFolder> RootFolderAsync(CancellationToken cancellation)
    {
        string drive = await DriveIdAsync(cancellation);
        using var result = await GraphAsync(HttpMethod.Get, $"/drives/{Uri.EscapeDataString(drive)}/root?$select=id",
            null, cancellation);
        return new OneDriveFolder(drive, result.RootElement.GetProperty("id").GetString()!, "OneDrive");
    }

    internal async Task<OneDriveFolder[]> ListFoldersAsync(OneDriveFolder folder, CancellationToken cancellation)
    {
        var folders = new List<OneDriveFolder>();
        string? page = FolderPath(folder) + "/children?$select=id,name,folder";
        while (page is not null)
        {
            using var result = await GraphAsync(HttpMethod.Get, page, null, cancellation);
            foreach (var item in result.RootElement.GetProperty("value").EnumerateArray())
            {
                if (item.TryGetProperty("folder", out var facet) && facet.ValueKind == JsonValueKind.Object)
                    folders.Add(new OneDriveFolder(folder.DriveId, item.GetProperty("id").GetString()!,
                        folder.Path + "/" + item.GetProperty("name").GetString()));
            }

            page = result.RootElement.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind == JsonValueKind.String
                ? GraphPagePath(next.GetString()) : null;
        }
        return folders.OrderBy(entry => entry.Path, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal async Task SaveReceivedAsync(OneDriveFolder folder, string name, Stream input, long size,
        CancellationToken cancellation)
    {
        if (!IsSignedIn)
            throw new IOException("Sign in to OneDrive from Pipopu Settings.");
        if (await DriveIdAsync(cancellation) != folder.DriveId)
            throw new IOException("The selected folder belongs to another account. Choose a OneDrive folder again.");

        if (size == 0)
        {
            // An upload session needs a nonempty byte range. Use a unique name for an empty item.
            string emptyName = "Empty-" + Guid.NewGuid().ToString("N")[..8] + "-" + name;
            using var content = new ByteArrayContent([]);
            using var result = await GraphAsync(HttpMethod.Put,
                FolderPath(folder) + ":/" + Uri.EscapeDataString(emptyName) + ":/content", content, cancellation);
            return;
        }

        var options = JsonSerializer.Serialize(new
        {
            item = new Dictionary<string, string>
            {
                ["@microsoft.graph.conflictBehavior"] = "rename",
                ["name"] = name
            }
        });
        Uri upload = await CreateUploadAsync(FolderPath(folder) + ":/" + Uri.EscapeDataString(name), options, cancellation);
        try { await UploadAsync(upload, input, size, cancellation, null); }
        catch
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await CancelUploadAsync(upload, deadline.Token);
            throw;
        }
    }

    private async Task<string> DriveIdAsync(CancellationToken cancellation)
    {
        using var result = await GraphAsync(HttpMethod.Get, "/me/drive?$select=id", null, cancellation);
        return result.RootElement.GetProperty("id").GetString()!;
    }

    private static string FolderPath(OneDriveFolder folder) =>
        $"/drives/{Uri.EscapeDataString(folder.DriveId)}/items/{Uri.EscapeDataString(folder.Id)}";

    private static string GraphPagePath(string? value)
    {
        const string version = "/v1.0";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != "graph.microsoft.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !uri.AbsolutePath.StartsWith(version + "/", StringComparison.Ordinal))
            throw new InvalidDataException("OneDrive returned an invalid folder page.");
        return uri.PathAndQuery[version.Length..];
    }

    internal async Task<Uri> DownloadUrlAsync(string id, CancellationToken cancellation)
    {
        using var item = await GraphAsync(HttpMethod.Get, Item(id) + ":?$select=id,%40microsoft.graph.downloadUrl", null, cancellation);
        return Https(item.RootElement.GetProperty("@microsoft.graph.downloadUrl").GetString());
    }

    internal async Task<TransferFile> DownloadAsync(string id, long size, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, await DownloadUrlAsync(id, cancellation));
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != size)
            throw new InvalidDataException("OneDrive returned the wrong file size.");
        var file = new TransferFile(TransferFile.TemporaryPath(), size);
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellation);
            await using var output = File.Create(file.Path);
            var buffer = new byte[256 * 1024];
            for (long left = size; left > 0;)
            {
                int count = (int)Math.Min(left, buffer.Length);
                await input.ReadExactlyAsync(buffer.AsMemory(0, count), cancellation);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                left -= count;
            }
            if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellation) != 0)
                throw new InvalidDataException("OneDrive returned the wrong file size.");
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    internal async Task CleanupAsync(string id, Uri? upload = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        if (upload is not null)
            await CancelUploadAsync(upload, deadline.Token);
        try { using var response = await GraphAsync(HttpMethod.Delete, Item(id) + ":", null, deadline.Token); }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException) { }
    }

    private static async Task CancelUploadAsync(Uri upload, CancellationToken cancellation)
    {
        try { using var response = await Http.DeleteAsync(upload, cancellation); }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
    }

    private async Task<JsonDocument> GraphAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, Graph + path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(cancellation));
        using var result = await Http.SendAsync(request, cancellation);
        if (!result.IsSuccessStatusCode)
            throw new IOException($"OneDrive request failed ({(int)result.StatusCode}).");
        return result.StatusCode == HttpStatusCode.NoContent ? JsonDocument.Parse("{}") :
            JsonDocument.Parse(await result.Content.ReadAsByteArrayAsync(cancellation));
    }

    private async Task<string> TokenAsync(CancellationToken cancellation)
    {
        await authentication.WaitAsync(cancellation);
        try
        {
            if (access is not null && expires > DateTimeOffset.UtcNow.AddMinutes(1))
                return access;
            if (refresh is null)
                throw new IOException("Sign in to OneDrive from Pipopu Settings to use the fallback.");
            using var result = await PostAsync(Identity + "token", new()
            {
                ["client_id"] = ClientId, ["scope"] = Scope, ["grant_type"] = "refresh_token", ["refresh_token"] = refresh
            }, cancellation);
            SetToken(result.RootElement);
            return access!;
        }
        finally { authentication.Release(); }
    }

    private void SetToken(JsonElement token)
    {
        access = token.GetProperty("access_token").GetString();
        expires = DateTimeOffset.UtcNow.AddSeconds(token.GetProperty("expires_in").GetInt32());
        if (token.TryGetProperty("refresh_token", out var next))
            refresh = next.GetString();
    }

    private static string Item(string id) => "/me/drive/root:/Pipopu-" + Guid.ParseExact(id, "N").ToString("N") + ".bin";

    private static Uri Https(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
            ? uri : throw new InvalidDataException("Microsoft did not return a secure transfer URL.");

    private static async Task<JsonDocument> PostAsync(
        string endpoint, Dictionary<string, string> form, CancellationToken cancellation, bool allowError = false)
    {
        using var body = new FormUrlEncodedContent(form);
        using var response = await Http.PostAsync(endpoint, body, cancellation);
        if (!response.IsSuccessStatusCode && !allowError)
            throw new IOException($"Microsoft sign-in failed ({(int)response.StatusCode}). Check the app registration and try again.");
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellation));
    }
}
