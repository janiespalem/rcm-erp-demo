using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using Rcm.Contracts;
using Xunit;

namespace Rcm.Desktop.Tests;

public sealed class InsightsTests
{
    [Theory]
    [InlineData("ceo", 6)] [InlineData("technolog", 6)] [InlineData("biuro", 1)] [InlineData("crm", 0)]
    public async Task Report_choices_enforce_role_even_if_server_advertises_all_reports(string role, int count)
    {
        using var api = new CrmClient(new Uri("http://localhost/"), new InsightsServer { Role = role });
        await api.Login(role, "0000", default); var vm = new InsightsViewModel(api); await vm.Activate(default);
        Assert.Equal(count, vm.Choices.Count); Assert.Equal(role is "ceo" or "technolog", vm.CanExport);
        using var main = new MainViewModel(api); Assert.Equal(count != 0, main.CanReadInsights);
        if (role == "biuro") Assert.Equal(InsightSection.ServiceHistory, vm.Section);
    }
    [Fact]
    public async Task Superseded_reads_cannot_replace_latest_page_and_filters_are_literal()
    {
        var server = new InsightsServer(); using var api = new CrmClient(new Uri("http://localhost/"), server); await api.Login("ceo", "0000", default);
        var vm = new InsightsViewModel(api); await vm.Activate(default); vm.Section = InsightSection.Schedule;
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); server.DelayedSchedule = old;
        var pending = vm.Load(default); vm.Search = "%_ Żółć"; vm.Page = 2; await vm.Load(default);
        old.SetResult(EditorTests.Reply(new Page<ScheduleInsight>([new(99,"OLD","Old","draft",null,null)],1,1,50))); await pending;
        Assert.Equal("NEW", Assert.Single(vm.Rows)["number"]); Assert.Contains("%25_", server.Queries.Last()); Assert.Equal(2, vm.Page);
        Assert.Equal("25,00%", InsightsViewModel.Percent(25)); Assert.Equal("—", InsightsViewModel.Money(null));
    }
    [Fact]
    public async Task Failed_or_cancelled_export_keeps_previous_file_and_removes_temporary_files()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rcm-insights-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var server = new InsightsServer(); using var api = new CrmClient(new Uri("http://localhost/"), server); await api.Login("ceo", "0000", default);
            var path = Path.Combine(directory,"report.xlsx"); await File.WriteAllTextAsync(path,"previous");
            server.ExportType = "text/html"; await Assert.ThrowsAsync<ApiFailure>(() => api.DownloadInsightsXlsx(path, default)); Assert.Equal("previous", await File.ReadAllTextAsync(path));
            server.ExportType = InsightsServer.Xlsx; server.ExportLength = 999; await Assert.ThrowsAsync<ApiFailure>(() => api.DownloadInsightsXlsx(path, default)); Assert.Equal("previous", await File.ReadAllTextAsync(path));
            server.ExportLength = null; server.BeforeExport = api.Logout; await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.DownloadInsightsXlsx(path, default)); Assert.Equal("previous", await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
}

internal sealed class InsightsServer : HttpMessageHandler
{
    internal const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    internal string Role = "ceo";
    internal string ExportType = Xlsx;
    internal long? ExportLength;
    internal Action? BeforeExport;
    internal bool WaitForExportCancellation;
    internal TaskCompletionSource<HttpResponseMessage>? DelayedSchedule;
    internal List<string> Queries { get; } = [];
    private static async Task<HttpResponseMessage> WaitForCancellation(CancellationToken ct) { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath; Queries.Add(request.RequestUri.Query);
        object result = path switch
        {
            "/api/v1/session/login" => new { access_token = "synthetic-insights" },
            "/api/v1/session" => new SessionDto(101, "Synthetic reports", null, null, Role),
            "/api/v1/shift-reports/features" => NativeShiftReportsServer.DisabledFeatures,
            "/api/v1/orders/features" => new OrderFeatures(true, true, true, true),
            "/api/v1/orders/questions" => new Page<OrderQuestionQueueRow>([], 3, 1, 1),
            "/api/v1/insights/features" => new InsightFeatures(true,true,true,true,true,true,true,true),
            "/api/v1/insights/analytics" => new AnalyticsInsight(100,5,5,60,40,25,12,8,10,2,110,[new("2026-10",3,12345.67)],[new("Żółć %_",3,12345.67)],[new(7,"7/2026","Żółć %_","in_production",new(2026,10,1),"Usługi")],1),
            "/api/v1/insights/production" => new Page<ProductionInsight>([new(7,"7/2026","Żółć %_","in_production",new(2026,10,1),"Obróbka","S355",["Cięcie","Spawanie"],1000)],51,1,50),
            "/api/v1/insights/schedule" => new Page<ScheduleInsight>([new(7,"NEW","Żółć %_","in_production",new(2026,10,1),"Usługi")],51,2,50),
            "/api/v1/insights/profitability" => new Page<ProfitabilityInsight>([new(7,"7/2026","Żółć %_","wydane",1000,200,550,750,250,25,5.5)],1,1,50),
            "/api/v1/insights/benchmark" => new BenchmarkInsight(14,10,20,3,null,new([new(7,new(2026,10,1),100,1400,14)],1,1,50)),
            "/api/v1/insights/service-history" => new Page<ServiceHistoryInsight>([new(7,new(2026,10,1),"Żółć %_","Usługa","Spawanie","S355",200,1,5,1000,"Kopia Lista zleceń usługi.xlsx","7/2026")],1,1,50),
            "/api/v1/insights/export.xlsx" => "export",
            _ => throw new InvalidOperationException($"Unexpected insights request {path}")
        };
        if (path.EndsWith("/schedule") && DelayedSchedule is { } delayed) { DelayedSchedule = null; return delayed.Task; }
        if (path.EndsWith("export.xlsx"))
        {
            Assert.Equal("synthetic-insights", request.Headers.Authorization?.Parameter);
            if (WaitForExportCancellation) return WaitForCancellation(ct);
            BeforeExport?.Invoke(); var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("PK synthetic worksheet")) };
            response.Content.Headers.ContentType = new(ExportType); if (ExportLength is not null) response.Content.Headers.ContentLength = ExportLength;
            return Task.FromResult(response);
        }
        return Task.FromResult(EditorTests.Reply(result));
    }
}
