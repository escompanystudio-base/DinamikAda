using System.IO.Compression;
using System.Text;
using DinamikAda.Modules.Productivity;
using Xunit;

namespace DinamikAda.Tests;

public sealed class LanDropProtocolTests : IDisposable
{
    private const string Prefix = "/s/test-token";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dinamikada-protocol-{Guid.NewGuid():N}");

    public LanDropProtocolTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task UnauthorizedUploadDoesNotReadTheBody()
    {
        using var server = new LanDropServer(_root);
        string headers = "POST /s/wrong/upload HTTP/1.1\r\nHost: localhost\r\nContent-Length: 500\r\n\r\n";
        using var transport = new DuplexTestStream(headers + new string('x', 500));
        await server.HandleRequestAsync(transport, Prefix, [], null, CancellationToken.None);
        Assert.Contains("404 Not Found", transport.Response);
        Assert.Equal(headers.Length, transport.BytesRead);
    }

    [Fact]
    public async Task InvalidBodyLengthsAndTruncatedUploadsReturnExplicitErrors()
    {
        using var server = new LanDropServer(_root);
        Assert.Contains("400 Bad Request", await RequestAsync(server, "Content-Length: -1\r\n", ""));
        Assert.Contains("400 Bad Request", await RequestAsync(server, "Content-Length: 2\r\nContent-Length: 3\r\n", "abc"));
        Assert.Contains("413 Payload Too Large", await RequestAsync(server, "Content-Length: 134217729\r\n", ""));
        Assert.Contains("400 Bad Request", await RequestAsync(server, "Content-Length: 200\r\n", "short"));
        Assert.Contains("400 Bad Request", await RequestAsync(server, "Transfer-Encoding: chunked\r\nContent-Length: 0\r\n", "0\r\n\r\n"));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task MultipartUploadPreservesBoundaryLikeBytesAndZeroByteFiles()
    {
        using var server = new LanDropServer(_root);
        string payload = "prefix--b\0\r\n--bX\r\nend";
        string body = $"--b\r\nContent-Disposition: form-data; name=\"file\"; filename=\"data.bin\"\r\n\r\n{payload}\r\n--b\r\nContent-Disposition: form-data; name=\"file\"; filename=\"empty.bin\"\r\n\r\n\r\n--b--\r\n";
        string response = await RequestAsync(server, $"Content-Type: multipart/form-data; boundary=b\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n", body);
        Assert.Contains("200 OK", response);
        Assert.Equal(payload, File.ReadAllText(Path.Combine(_root, "data.bin")));
        Assert.Equal(0, new FileInfo(Path.Combine(_root, "empty.bin")).Length);
    }

    [Fact]
    public async Task IncompleteMultipartNeverSavesAPartialFile()
    {
        using var server = new LanDropServer(_root);
        string body = "--b\r\nContent-Disposition: form-data; name=\"file\"; filename=\"broken.bin\"\r\n\r\npartial";
        string response = await RequestAsync(server, $"Content-Type: multipart/form-data; boundary=b\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n", body);
        Assert.Contains("400 Bad Request", response);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task ChunkedUploadAcceptsTrailersAndContinueHandshake()
    {
        using var server = new LanDropServer(_root);
        string body = "--b\r\nContent-Disposition: form-data; name=\"file\"; filename=\"chunk.txt\"\r\n\r\nhello\r\n--b--\r\n";
        string chunked = $"{Encoding.UTF8.GetByteCount(body):X}\r\n{body}\r\n0\r\nX-Test: trailer\r\n\r\n";
        string response = await RequestAsync(server, "Content-Type: multipart/form-data; boundary=b\r\nTransfer-Encoding: chunked\r\nExpect: 100-continue\r\n", chunked);
        Assert.Contains("100 Continue", response);
        Assert.Contains("200 OK", response);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_root, "chunk.txt")));
    }

    [Fact]
    public async Task InvalidOrOversizedChunksAreRejected()
    {
        using var server = new LanDropServer(_root);
        Assert.Contains("400 Bad Request", await RequestAsync(server, "Transfer-Encoding: chunked\r\n", "not-hex\r\n"));
        Assert.Contains("413 Payload Too Large", await RequestAsync(server, "Transfer-Encoding: chunked\r\n", "8000001\r\n"));
        Assert.Contains("400 Bad Request", await RequestAsync(server, "Transfer-Encoding: chunked\r\n", "5\r\nshortINVALID\r\n0\r\n\r\n"));
    }

    [Fact]
    public async Task ConcurrentRequestsUseUniqueDestinationFiles()
    {
        using var server = new LanDropServer(_root);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            string body = $"--b\r\nContent-Disposition: form-data; name=\"file\"; filename=\"same.txt\"\r\n\r\n{i}\r\n--b--\r\n";
            string response = await RequestAsync(server, $"Content-Type: multipart/form-data; boundary=b\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n", body);
            Assert.Contains("200 OK", response);
        }));
        Assert.Equal(8, Directory.GetFiles(_root).Length);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => i.ToString()), Directory.GetFiles(_root).Select(File.ReadAllText).Order());
    }

    [Fact]
    public async Task LandingPageEncodesUserContentAndSupportsCopyOverHttp()
    {
        using var server = new LanDropServer(_root);
        using var transport = new DuplexTestStream($"GET {Prefix} HTTP/1.1\r\nHost: localhost\r\n\r\n");
        await server.HandleRequestAsync(transport, Prefix, [], "<script>alert('x')</script>", CancellationToken.None);
        Assert.Contains("&lt;script&gt;", transport.Response);
        Assert.DoesNotContain("<script>alert", transport.Response);
        Assert.Contains("execCommand('copy')", transport.Response);
    }

    [Fact]
    public async Task DownloadsAndZipBundlesPreserveContentAndDuplicateNames()
    {
        string firstDir = Path.Combine(_root, "first");
        string secondDir = Path.Combine(_root, "second");
        Directory.CreateDirectory(firstDir);
        Directory.CreateDirectory(secondDir);
        string first = Path.Combine(firstDir, "same.txt");
        string second = Path.Combine(secondDir, "same.txt");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");
        using var server = new LanDropServer(_root);
        using var direct = new DuplexTestStream($"GET {Prefix}/dl/0 HTTP/1.1\r\nHost: localhost\r\n\r\n");
        await server.HandleRequestAsync(direct, Prefix, [first, second], null, CancellationToken.None);
        Assert.EndsWith("first", direct.Response);
        using var bundle = new DuplexTestStream($"GET {Prefix}/bundle.zip HTTP/1.1\r\nHost: localhost\r\n\r\n");
        await server.HandleRequestAsync(bundle, Prefix, [first, second], null, CancellationToken.None);
        byte[] response = bundle.ResponseBytes;
        int start = response.AsSpan().IndexOf("\r\n\r\n"u8) + 4;
        using var zipStream = new MemoryStream(response, start, response.Length - start);
        using var zip = new ZipArchive(zipStream);
        Assert.Equal(new[] { "same.txt", "same_1.txt" }, zip.Entries.Select(e => e.FullName));
    }

    private static async Task<string> RequestAsync(LanDropServer server, string headers, string body)
    {
        using var transport = new DuplexTestStream($"POST {Prefix}/upload HTTP/1.1\r\nHost: localhost\r\n{headers}\r\n{body}");
        await server.HandleRequestAsync(transport, Prefix, [], null, CancellationToken.None);
        return transport.Response;
    }

    private sealed class DuplexTestStream(string request) : Stream
    {
        private readonly MemoryStream _input = new(Encoding.UTF8.GetBytes(request));
        private readonly MemoryStream _output = new();
        public string Response => Encoding.UTF8.GetString(_output.ToArray());
        public byte[] ResponseBytes => _output.ToArray();
        public long BytesRead => _input.Position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _output.WriteAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _input.Dispose(); _output.Dispose(); }
            base.Dispose(disposing);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
