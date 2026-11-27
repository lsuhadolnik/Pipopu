using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Pipopu;

internal sealed class LanTransfer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private LanTransfer(WebApplication app) => this.app = app;
    internal string[] Urls { get; private set; } = [];
    internal Task Completion => completed.Task;

    internal static async Task<LanTransfer> OpenAsync(TransferFile file, bool upload, CancellationToken cancellation,
        IProgress<int>? progress = null)
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Where(unicast => unicast.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                IsLocal(unicast.Address))
            .Select(unicast => unicast.Address).Distinct().Take(8).ToArray();
        if (addresses.Length == 0)
            throw new IOException("No local network address is available.");

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            foreach (var address in addresses)
                options.Listen(address, 0);
            options.Limits.MaxRequestBodySize = file.Size;
            options.Limits.MaxConcurrentConnections = 8;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
        });
        var app = builder.Build();
        var server = new LanTransfer(app);
        string path = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        int claimed = 0;
        app.MapMethods(path, ["HEAD", upload ? "PUT" : "GET"], async context =>
        {
            if (HttpMethods.IsHead(context.Request.Method))
                return;
            if (upload && context.Request.ContentLength != file.Size)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (Interlocked.Exchange(ref claimed, 1) != 0)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }

            using var operation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, cancellation);
            try
            {
                if (upload)
                {
                    await using (var output = File.Create(file.Path))
                    {
                        await context.Request.Body.CopyToAsync(output, operation.Token);
                        if (output.Length != file.Size)
                            throw new EndOfStreamException();
                    }
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                }
                else
                {
                    context.Response.ContentLength = file.Size;
                    context.Response.ContentType = "application/octet-stream";
                    await using var input = File.OpenRead(file.Path);
                    var buffer = new byte[256 * 1024];
                    int previous = -1;
                    long sent = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, operation.Token)) != 0)
                    {
                        await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), operation.Token);
                        sent += count;
                        int percent = (int)(95 * sent / file.Size);
                        if (percent != previous)
                        {
                            previous = percent;
                            progress?.Report(percent);
                        }
                    }
                }
                server.completed.TrySetResult();
            }
            catch (Exception error) when (error is OperationCanceledException or IOException)
            {
                context.Abort();
                server.completed.TrySetException(error);
            }
        });
        try
        {
            await app.StartAsync(cancellation);
            server.Urls = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Select(address => address + path).ToArray();
            return server;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await app.StopAsync(stop.Token); }
        finally { await app.DisposeAsync(); }
    }

    private static bool IsLocal(IPAddress address)
    {
        byte[] ip = address.GetAddressBytes();
        return ip[0] == 10 || ip[0] == 172 && ip[1] is >= 16 and <= 31 ||
            ip[0] == 192 && ip[1] == 168 || ip[0] == 169 && ip[1] == 254 ||
            ip[0] == 100 && ip[1] is >= 64 and <= 127;
    }
}
