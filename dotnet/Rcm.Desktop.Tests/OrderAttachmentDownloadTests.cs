using System.IO;
using System.Net;
using System.Net.Http;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class OrderAttachmentDownloadTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(104857601)]
    public async Task Incomplete_or_oversized_download_keeps_existing_file(long declaredLength)
    {
        var folder = Path.Combine(Path.GetTempPath(), "rcm-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var target = Path.Combine(folder, "synthetic.pdf"); await File.WriteAllTextAsync(target, "existing file");
            var handler = new EditorTests.Responses();
            handler.Replies.Enqueue(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("%PDF-1.7\n"u8.ToArray()) };
                response.Content.Headers.ContentLength = declaredLength; return Task.FromResult(response);
            });
            using var api = new CrmClient(new Uri("http://localhost/"), handler);
            await Assert.ThrowsAsync<ApiFailure>(() => api.DownloadOrderAttachment(1, 2, target, CancellationToken.None));
            Assert.Equal("existing file", await File.ReadAllTextAsync(target)); Assert.Single(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task Complete_download_replaces_target_and_removes_temporary_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rcm-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var target = Path.Combine(folder, "synthetic.pdf"); await File.WriteAllTextAsync(target, "old");
            var body = "%PDF-1.7\nSynthetic"u8.ToArray();
            var handler = new EditorTests.Responses();
            handler.Replies.Enqueue(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }));
            using var api = new CrmClient(new Uri("http://localhost/"), handler);
            await api.DownloadOrderAttachment(1, 2, target, CancellationToken.None);
            Assert.Equal(body, await File.ReadAllBytesAsync(target)); Assert.Single(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
