using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class OrderDocumentTests
{
    [Theory]
    [InlineData("biuro", false, false)]
    [InlineData("ceo", false, false)]
    [InlineData("technolog", true, false)]
    [InlineData("technolog", true, true)]
    public async Task Documents_respect_role_internal_order_and_archive(string role, bool operations, bool internalOrder)
    {
        var server = new NativeOrdersServer { Role = role };
        using var api = new CrmClient(new Uri("http://localhost/"), server);
        await api.Login(role, "0000", default);
        var model = new OrderDocumentsViewModel(server.Order with { IsInternal = internalOrder, ArchivedAt = DateTimeOffset.UtcNow }) { Role = api.Session?.Role };
        Assert.True(model.CanDownloadKind("arkusz"));
        Assert.Equal(!internalOrder, model.CanDownloadKind("oferta"));
        Assert.Equal(operations, model.CanDownloadKind("operations"));
        Assert.False(model.CanDownloadKind("unrecognized"));
        Assert.Equal("Arkusz_1_2026.pdf", model.Filename("arkusz"));
        model.Busy = true; Assert.False(model.CanDownloadKind("arkusz"));
        model.Busy = false; model.Role = null; Assert.False(model.CanDownloadKind("arkusz"));
    }

    [Theory]
    [InlineData("arkusz", "application/pdf", "%PDF-1.7\nSynthetic")]
    [InlineData("oferta", "application/pdf", "%PDF-1.7\nSynthetic")]
    [InlineData("operations", "application/zip", "PK\u0003\u0004Synthetic")]
    public async Task Complete_document_download_replaces_target_safely(string kind, string mime, string body)
    {
        var folder = Path.Combine(Path.GetTempPath(), "rcm-documents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var target = Path.Combine(folder, "synthetic.bin"); await File.WriteAllTextAsync(target, "previous document");
            var handler = new EditorTests.Responses();
            handler.Replies.Enqueue(_ => Task.FromResult(Document(mime, body)));
            using var api = new CrmClient(new Uri("http://localhost/"), handler);
            await api.DownloadOrderDocument(1, kind, target, default);
            Assert.Equal(body, await File.ReadAllTextAsync(target));
            Assert.Single(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("text/html", 12)]
    [InlineData("application/pdf", 999)]
    [InlineData("application/pdf", 104857601)]
    public async Task Invalid_or_incomplete_document_keeps_existing_file(string mime, long length)
    {
        var folder = Path.Combine(Path.GetTempPath(), "rcm-documents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var target = Path.Combine(folder, "synthetic.pdf"); await File.WriteAllTextAsync(target, "previous document");
            var handler = new EditorTests.Responses();
            handler.Replies.Enqueue(_ =>
            {
                var response = Document(mime, "%PDF-1.7\n"); response.Content.Headers.ContentLength = length;
                return Task.FromResult(response);
            });
            using var api = new CrmClient(new Uri("http://localhost/"), handler);
            await Assert.ThrowsAsync<ApiFailure>(() => api.DownloadOrderDocument(1, "arkusz", target, default));
            Assert.Equal("previous document", await File.ReadAllTextAsync(target));
            Assert.Single(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Cancelled_document_read_preserves_existing_file_and_allows_retry()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rcm-documents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var target = Path.Combine(folder, "synthetic.pdf"); await File.WriteAllTextAsync(target, "previous document");
            using var cancellation = new CancellationTokenSource();
            var server = new CancelDocumentServer(cancellation);
            using var api = new CrmClient(new Uri("http://localhost/"), server);
            await api.Login("technolog", "0000", default);
            var model = new OrderDocumentsViewModel(new() { Id = 1 }) { Role = "technolog" };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.Download(api, "arkusz", target, cancellation.Token));
            Assert.False(model.Busy); Assert.True(model.CanDownload);
            Assert.Equal("previous document", await File.ReadAllTextAsync(target));
            Assert.Single(Directory.GetFiles(folder));
            await model.Download(api, "arkusz", target, default);
            Assert.Equal("%PDF-1.7\nSynthetic", await File.ReadAllTextAsync(target));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Legacy_html_document_is_saved_with_html_extension()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rcm-documents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var target = Path.Combine(folder, "synthetic.html");
            var handler = new EditorTests.Responses();
            handler.Replies.Enqueue(_ => Task.FromResult(Document("text/html", "<html><body>Synthetic order</body></html>")));
            using var api = new CrmClient(new Uri("http://localhost/"), handler);
            await api.DownloadOrderDocument(1, "arkusz", target, default);
            Assert.Equal("<html><body>Synthetic order</body></html>", await File.ReadAllTextAsync(target));
            Assert.Single(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, true); }
    }

    private static HttpResponseMessage Document(string mime, string body)
    {
        var content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        return new(HttpStatusCode.OK) { Content = content };
    }
    private sealed class CancelDocumentServer(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        private bool cancel = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/login")) return Task.FromResult(EditorTests.Reply(new { access_token = "synthetic" }));
            if (request.RequestUri.AbsolutePath.EndsWith("/session")) return Task.FromResult(EditorTests.Reply(new SessionDto(101, "Synthetic", null, null, "technolog")));
            if (cancel)
            {
                cancel = false;
                var content = new StreamContent(new CancellingStream(cancellation));
                content.Headers.ContentType = new("application/pdf");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
            return Task.FromResult(Document("application/pdf", "%PDF-1.7\nSynthetic"));
        }
    }
    private sealed class CancellingStream(CancellationTokenSource cancellation) : MemoryStream("%PDF-1.7\nSynthetic"u8.ToArray())
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position > 0) { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
            return base.ReadAsync(buffer[..Math.Min(4, buffer.Length)], ct);
        }
    }
}
