using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class OrderAttachmentMutationTests
{
    [Fact]
    public async Task Uncertain_upload_preserves_file_and_reuses_request_id_without_duplicate_row()
    {
        var folder = Directory.CreateTempSubdirectory("rcm-upload-client-");
        try
        {
            var source = Path.Combine(folder.FullName, "synthetic.pdf");
            var bytes = "%PDF-1.7\nSynthetic"u8.ToArray(); await File.WriteAllBytesAsync(source, bytes);
            var item = new OrderAttachmentDto(42, 1, "synthetic.pdf", bytes.Length, "application/pdf", "technolog", DateTimeOffset.UtcNow);
            var handler = new Requests(); string? query = null;
            handler.Replies.Enqueue(async request =>
            {
                query = request.RequestUri!.Query;
                Assert.Equal(bytes, await request.Content!.ReadAsByteArrayAsync());
                throw new HttpRequestException("Synthetic lost response after commit");
            });
            handler.Replies.Enqueue(async request =>
            {
                Assert.Equal(query, request.RequestUri!.Query);
                Assert.Equal(bytes, await request.Content!.ReadAsByteArrayAsync());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(item) };
            });
            using var api = new CrmClient(new Uri("http://localhost/"), handler);
            var model = new OrderAttachmentsViewModel(new OrderDto { Id = 1 }) { Role = "technolog", UploadPath = source };
            await Assert.ThrowsAsync<HttpRequestException>(() => model.Upload(api, CancellationToken.None));
            Assert.Equal(source, model.UploadPath); Assert.True(model.CanUpload); Assert.False(model.Busy);
            await model.Upload(api, CancellationToken.None);
            Assert.Empty(model.UploadPath); Assert.Single(model.Rows); Assert.Equal(item, model.Selected);
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public async Task Uncertain_removal_keeps_selection_and_retries_the_same_command()
    {
        var item = new OrderAttachmentDto(42, 1, "synthetic.pdf", 12, "application/pdf", "technolog", DateTimeOffset.UtcNow);
        var handler = new Requests(); string? payload = null;
        handler.Replies.Enqueue(async request =>
        { payload = await request.Content!.ReadAsStringAsync(); throw new HttpRequestException("Synthetic lost response"); });
        handler.Replies.Enqueue(async request =>
        {
            Assert.Equal(payload, await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new RemovedOrderAttachment(42)) };
        });
        using var api = new CrmClient(new Uri("http://localhost/"), handler);
        var model = new OrderAttachmentsViewModel(new OrderDto { Id = 1 }) { Role = "technolog", Selected = item };
        model.Rows.Add(item);
        await Assert.ThrowsAsync<HttpRequestException>(() => model.Remove(api, CancellationToken.None));
        Assert.Equal(item, model.Selected); Assert.Single(model.Rows); Assert.True(model.CanRemove);
        await model.Remove(api, CancellationToken.None);
        Assert.Null(model.Selected); Assert.Empty(model.Rows);
    }

    [Theory]
    [InlineData("ceo", false, true, false)]
    [InlineData("biuro", false, true, false)]
    [InlineData("technolog", false, true, true)]
    [InlineData("technolog", true, false, false)]
    [InlineData("crm", false, false, false)]
    public void Mutations_follow_existing_roles_and_archive_state(string role, bool archived, bool upload, bool remove)
    {
        var model = new OrderAttachmentsViewModel(new OrderDto { Id = 1, ArchivedAt = archived ? DateTimeOffset.UtcNow : null })
        { Role = role, Selected = new(42, 1, "synthetic.pdf", 12, "application/pdf", "technolog", DateTimeOffset.UtcNow), UploadPath = "synthetic.pdf" };
        Assert.Equal(upload, model.CanUpload); Assert.Equal(remove, model.CanRemove);
        model.Busy = true;
        Assert.False(model.CanUpload); Assert.False(model.CanRemove);
    }

    private sealed class Requests : HttpMessageHandler
    {
        public Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> Replies { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Replies.Dequeue()(request);
    }
}
