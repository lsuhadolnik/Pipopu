using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Pipopu;

internal sealed class OneDrive
{
    private const string Identity = "https://login.microsoftonline.com/common/oauth2/v2.0/";
    private const string Graph = "https://graph.microsoft.com/v1.0";
    private const string Scope = "https://graph.microsoft.com/Files.ReadWrite offline_access";
    private const int UploadBlock = 10 * 320 * 1024;
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim authentication = new(1);
    private string? access;
    private string? refresh;
    private DateTimeOffset expires;

    internal static string ClientId
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Pipopu");
            return key?.GetValue("OneDriveClientId") as string ?? "";
        }
    }

    internal async Task SignInAsync(string id, Action<string, Uri> showCode, CancellationToken cancellation)
    {
        if (!Guid.TryParse(id, out _))
            throw new ArgumentException("Enter the application (client) ID from the Entra app registration.");

        await authentication.WaitAsync(cancellation);
        try
        {
            using var grant = await PostAsync(Identity + "devicecode", new() { ["client_id"] = id, ["scope"] = Scope }, cancellation);
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
                    ["client_id"] = id,
                    ["device_code"] = code
                }, deadline.Token, allowError: true);
                switch (reply.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null)
                {
                    case "authorization_pending": continue;
                    case "slow_down": interval += TimeSpan.FromSeconds(5); continue;
                    case not null: throw new IOException("Microsoft did not authorize OneDrive access.");
                    default:
                        SetToken(reply.RootElement);
                        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Pipopu"))
                            key.SetValue("OneDriveClientId", id);
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
    {
        using var content = new StringContent("""{"item":{"@microsoft.graph.conflictBehavior":"fail"}}""", Encoding.UTF8, "application/json");
        using var result = await GraphAsync(HttpMethod.Post, Item(id) + ":/createUploadSession", content, cancellation);
        return Https(result.RootElement.GetProperty("uploadUrl").GetString());
    }

    internal async Task UploadAsync(Uri url, TransferFile file, CancellationToken cancellation, IProgress<int>? progress)
    {
        await using var input = File.OpenRead(file.Path);
        var block = new byte[UploadBlock];
        for (long offset = 0; offset < file.Size;)
        {
            int count = (int)Math.Min(block.Length, file.Size - offset);
            await input.ReadExactlyAsync(block.AsMemory(0, count), cancellation);
            using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(block, 0, count) };
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, file.Size);
            using var response = await Http.SendAsync(request, cancellation);
            offset += count;
            if (offset < file.Size ? response.StatusCode != HttpStatusCode.Accepted :
                response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
                throw new IOException($"OneDrive upload failed ({(int)response.StatusCode}).");
            progress?.Report((int)(90 * offset / file.Size));
        }
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
        {
            try { using var response = await Http.DeleteAsync(upload, deadline.Token); }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
        }
        try { using var response = await GraphAsync(HttpMethod.Delete, Item(id) + ":", null, deadline.Token); }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException) { }
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
                throw new IOException("Local Wi-Fi is unreachable. Sign in to OneDrive from Pipopu's tray menu to use the fallback.");
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
