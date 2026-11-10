using Merchant.Api;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Merchant.Api.Tests;

public sealed class GpHttpClientTests
{
    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData(null)]
    public async Task HttpClientReadsCompressedProviderResponses(string? encoding)
    {
        var json = Encoding.UTF8.GetBytes("""{"token":"fake-access-token"}""");
        using var buffer = new MemoryStream();
        if (encoding is not null)
        {
            using Stream compressor = encoding == "gzip"
                ? new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true)
                : new DeflateStream(buffer, CompressionMode.Compress, leaveOpen: true);
            compressor.Write(json);
        }
        else buffer.Write(json);
        var body = buffer.ToArray();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }

            // Reproduce the provider's compressed JSON response on a real HTTP connection.
            var headers = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                $"Content-Length: {body.Length}\r\nConnection: close\r\n" +
                (encoding is null ? "" : $"Content-Encoding: {encoding}\r\n") + "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), timeout.Token);
            await stream.WriteAsync(body, timeout.Token);
        }, timeout.Token);

        using var client = GpClient.CreateHttpClient();
        Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
        try
        {
            var response = await client.GetFromJsonAsync<JsonElement>(
                $"http://127.0.0.1:{endpoint.Port}/accesstoken", timeout.Token);
            Assert.Equal("fake-access-token", response.GetProperty("token").GetString());
        }
        finally
        {
            await timeout.CancelAsync();
            listener.Stop();
            await server;
        }
    }
}
