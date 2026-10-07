using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Rcm.Contracts;
using Rcm.Crm;
using Rcm.Orders;

namespace Rcm.Host;

internal sealed class LegacyOrders(IHttpClientFactory clients)
{
    private static readonly JsonSerializerOptions LegacyJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<IResult> DownloadDocument(HttpContext http, long id, string kind, CancellationToken ct)
    {
        var route = kind switch
        {
            "arkusz" => "pdf", "oferta" => "oferta", "operations" => "pdf/split",
            _ => throw new CrmFault(404, "Nieznany rodzaj dokumentu.")
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/orders/{id}/{route}");
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(http.Request.Headers.Authorization.ToString());
        try
        {
            using var response = await clients.CreateClient("legacy-documents").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new CrmFault((int)response.StatusCode >= 500 ? 503 : (int)response.StatusCode, "Nie udało się wygenerować dokumentu.");
            const int limit = 64 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw new CrmFault(422, "Dokument przekracza dozwolony rozmiar.");
            var mime = response.Content.Headers.ContentType?.MediaType;
            if (mime is not ("application/pdf" or "text/html" or "application/zip"))
                throw new CrmFault(503, "Nieprawidłowy format dokumentu z serwera.");
            var extension = mime switch { "application/pdf" => ".pdf", "application/zip" => ".zip", _ => ".html" };
            var filename = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
            filename = Path.GetFileName(filename?.Trim('"').Replace('\\', '/') ?? $"{kind}_{id}{extension}");
            if (string.IsNullOrWhiteSpace(filename) || !filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                filename = $"{kind}_{id}{extension}";
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) != 0)
            {
                if (buffer.Length + read > limit) throw new CrmFault(422, "Dokument przekracza dozwolony rozmiar.");
                await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
            }
            return Results.File(buffer.ToArray(), mime, filename);
        }
        catch (HttpRequestException) { throw new CrmFault(503, "Brak połączenia z generowaniem dokumentów."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CrmFault(503, "Upłynął czas generowania dokumentu. Spróbuj ponownie."); }
    }

    public async Task<Page<OrderDto>> Query(HttpContext http, string? q, string? status, bool archived, int page, int pageSize, CancellationToken ct)
    {
        if (page is < 1 or > 1_000_000 || pageSize is < 1 or > 100 || q?.Length > 200)
            throw CrmFault.Invalid("page", "Wybierz stronę i rozmiar od 1 do 100 pozycji; wyszukiwanie do 200 znaków.");
        var result = await Send<Page<OrderDto>>(http, HttpMethod.Get,
            $"api/orders/page?q={Uri.EscapeDataString(q ?? "")}&status={Uri.EscapeDataString(status ?? "")}&archived={archived.ToString().ToLowerInvariant()}&page={page}&page_size={pageSize}", null, ct);
        if (http.Items["orderUser"] is not LegacyUser { Role: "biuro" or "technolog" })
            return result with { Items = result.Items.Select(row => row with { PendingQuestions = 0 }).ToArray() };
        if (result.Items.Length == 0) return result;
        try
        {
            var pending = await Send<LegacyPendingQuestion[]>(http, HttpMethod.Get, "api/params?status=pending", null, ct);
            var ids = result.Items.Select(row => row.Id).ToHashSet();
            var counts = pending.Where(row => row.Status == "pending" && ids.Contains(row.OrderId)).GroupBy(row => row.OrderId).ToDictionary(group => group.Key, group => group.Count());
            return result with { Items = result.Items.Select(row => row with { PendingQuestions = counts.GetValueOrDefault(row.Id) }).ToArray() };
        }
        catch (CrmFault) { return result with { Items = result.Items.Select(row => row with { PendingQuestions = 0 }).ToArray() }; }
    }
    public Task<OrderDto> Detail(HttpContext http, long id, CancellationToken ct) => Send<OrderDto>(http, HttpMethod.Get, $"api/orders/{id}", null, ct);
    public Task<OrderEventDto[]> Events(HttpContext http, long id, CancellationToken ct) => Send<OrderEventDto[]>(http, HttpMethod.Get, $"api/orders/{id}/events", null, ct);
    public Task<OrderDto> Create(HttpContext http, CreateOrder command, CancellationToken ct)
    {
        if (command.RequestId == Guid.Empty || command.Fields is null) throw CrmFault.Invalid("requestId", "Identyfikator zapisu i dane zlecenia są wymagane.");
        return Send<OrderDto>(http, HttpMethod.Post, "api/orders/native", command, ct);
    }
    public Task<OrderDto> Edit(HttpContext http, long id, EditOrder command, CancellationToken ct)
    {
        if (command.ExpectedVersion < 1 || command.Fields is null) throw CrmFault.Invalid("expectedVersion", "Wczytaj aktualną wersję zlecenia.");
        var payload = JsonSerializer.SerializeToNode(command.Fields, new JsonSerializerOptions(LegacyJson) { DefaultIgnoreCondition = JsonIgnoreCondition.Never })!.AsObject();
        payload.Remove("is_internal"); payload.Remove("template_id"); payload.Remove("materials_json");
        payload["version_id"] = command.ExpectedVersion;
        return Send<OrderDto>(http, HttpMethod.Patch, $"api/orders/{id}", payload, ct);
    }
    public Task<OrderTriageResult> Triage(HttpContext http, long id, CancellationToken ct) => Send<OrderTriageResult>(http, HttpMethod.Post, $"api/orders/{id}/triage", null, ct);
    public Task<OrderDto> Transition(HttpContext http, long id, string action, CancellationToken ct)
    {
        if (action is not ("confirm" or "complete" or "deliver" or "archive" or "restore")) throw new ArgumentOutOfRangeException(nameof(action));
        return Send<OrderDto>(http, HttpMethod.Post, $"api/orders/{id}/{action}", null, ct);
    }
    public async Task<OrderQuoteDto> Quote(HttpContext http, long id, CancellationToken ct)
    {
        var payload = await Send<JsonObject>(http, HttpMethod.Get, $"api/orders/{id}/quote", null, ct);
        static void Alias(JsonObject row, string field, params string[] aliases)
        {
            if (row[field] is JsonValue value && value.TryGetValue<string>(out var current) && !string.IsNullOrEmpty(current)) return;
            foreach (var alias in aliases)
                if (row[alias] is JsonValue candidate && candidate.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
                { row[field] = text; return; }
        }
        if (payload["processes_json"] is JsonArray processes)
            foreach (var row in processes.OfType<JsonObject>())
            { Alias(row, "name", "op"); Alias(row, "department", "wydział"); Alias(row, "material", "materiał"); }
        if (payload["materials_json"] is JsonArray materials)
            foreach (var row in materials.OfType<JsonObject>()) Alias(row, "name", "material", "mat", "materiał");
        if (payload["pricing_method"] is null)
            payload["pricing_method"] = payload["estimate_version"]?.GetValue<string>() == "manual" ? "reczna" : "kalkulacja";
        OrderQuoteDto quote;
        try { quote = payload.Deserialize<OrderQuoteDto>(LegacyJson) ?? throw new JsonException(); }
        catch (JsonException) { throw new CrmFault(503, "Nie można odczytać zapisanej wyceny. Zachowaj formularz."); }
        // Older saves expanded absent hours/rate to zero while retaining computed fixed cost.
        return quote with { ProcessesJson = quote.ProcessesJson?.Select(p =>
            p.Hours == 0 && p.RatePerHour == 0 && p.Cost > 0 ? p with { Hours = null, RatePerHour = null } : p).ToArray() };
    }
    public Task<OrderQuoteDto> SaveQuote(HttpContext http, long id, OrderQuoteInput input, CancellationToken ct)
    {
        ValidateQuote(input);
        return Send<OrderQuoteDto>(http, HttpMethod.Post, $"api/orders/{id}/quote/structured", input, ct);
    }
    public Task<OrderQuoteDto> SaveManualQuote(HttpContext http, long id, OrderManualQuote input, CancellationToken ct)
    {
        if (!double.IsFinite(input.TotalNet) || input.TotalNet is < 0 or > 99_999_999.99)
            throw CrmFault.Invalid("totalNet", "Podaj nieujemną kwotę do 99 999 999,99 zł.");
        return Send<OrderQuoteDto>(http, HttpMethod.Post, $"api/orders/{id}/quote/manual", input, ct);
    }
    public async Task<OrderLookups> Lookups(HttpContext http, CancellationToken ct)
    {
        var templates = await Send<OrderTemplateDto[]>(http, HttpMethod.Get, "api/templates", null, ct);
        var materials = await Send<OrderApprovedMaterialDto[]>(http, HttpMethod.Get, "api/approved-materials", null, ct);
        var operations = await Send<OrderOperationDto[]>(http, HttpMethod.Get, "api/operation-catalog/", null, ct);
        return new(templates, materials, operations, await LaborRate(http, ct));
    }
    private async Task<double> LaborRate(HttpContext http, CancellationToken ct)
    {
        var settings = await Send<LegacySetting[]>(http, HttpMethod.Get, "api/settings", null, ct);
        var value = settings.FirstOrDefault(s => s.Key == "labor_rate_pln")?.Value;
        if (value is null) return 90;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) || !double.IsFinite(rate) || rate is <= 0 or > 10_000)
            throw new CrmFault(503, "Nieprawidłowa stawka robocizny na serwerze.");
        return rate;
    }
    public async Task<OrderQuotePreview> Preview(HttpContext http, OrderQuoteInput input, CancellationToken ct)
    {
        ValidateQuote(input);
        var processes = input.Processes.Select(p =>
        {
            var line = new ProcessLine { Cost = p.Cost };
            if (p.Hours is { } hours) line = line with { Hours = hours };
            if (p.RatePerHour is { } rate) line = line with { RatePerHour = rate };
            return line;
        }).ToArray();
        var result = PricingCalculator.Calculate(new StructuredQuoteInput
        {
            Processes = processes,
            Materials = input.Materials.Select(m => new MaterialLine { QtyKg = m.QtyKg, PricePerKg = m.PricePerKg, Cost = m.Cost }).ToArray(),
            MaterialCost = input.MaterialCost, MaterialWeightKg = input.MaterialWeightKg, MaterialPricePerKg = input.MaterialPricePerKg,
            LaborHours = input.LaborHours, LaborRate = await LaborRate(http, ct), OverheadPct = input.OverheadPct,
            MarginPct = input.MarginPct, TransportCost = input.TransportCost, WeightKg = input.WeightKg,
            WeightRatePlnKg = input.WeightRatePlnKg, Method = input.Method
        });
        return new(result.OpsTotal, result.MaterialTotal, result.ExtraLabor, result.WeightTotal, result.Base,
            result.Subtotal, result.TotalNet, result.PricingMethod, input.WeightBasis);
    }
    private static void ValidateQuote(OrderQuoteInput input)
    {
        var errors = new Dictionary<string, string[]>();
        void Number(string name, double value, double max)
        { if (!double.IsFinite(value) || value < 0 || value > max) errors[name] = ["Podaj nieujemną liczbę w dozwolonym zakresie."]; }
        const double money = 99_999_999.99, weight = 9_999_999.999, rate = 1_000_000;
        if (input.Method is not ("kalkulacja" or "od_masy")) errors["method"] = ["Wybierz metodę wyceny."];
        if (input.WeightBasis is not ("netto" or "brutto")) errors["weightBasis"] = ["Wybierz masę netto lub brutto."];
        Number("materialWeightKg", input.MaterialWeightKg, weight); Number("materialPricePerKg", input.MaterialPricePerKg, rate);
        Number("materialCost", input.MaterialCost, money); Number("weightNettoKg", input.WeightNettoKg, weight);
        Number("weightBruttoKg", input.WeightBruttoKg, weight); Number("laborHours", input.LaborHours, rate);
        Number("overheadPct", input.OverheadPct, 1); Number("marginPct", input.MarginPct, 1);
        Number("transportCost", input.TransportCost, money); Number("weightKg", input.WeightKg, weight);
        Number("weightRatePlnKg", input.WeightRatePlnKg, rate);
        if (input.Processes is null) errors["processes"] = ["Podaj listę operacji."];
        else for (var i = 0; i < input.Processes.Length; i++)
        {
            var p = input.Processes[i];
            if (p is null || p.Name is null) { errors[$"processes.{i}"] = ["Podaj nazwę operacji."]; continue; }
            Number($"processes.{i}.hours", p.Hours ?? 0, rate); Number($"processes.{i}.ratePerHour", p.RatePerHour ?? 0, rate); Number($"processes.{i}.cost", p.Cost, money);
        }
        if (input.Materials is null) errors["materials"] = ["Podaj listę materiałów."];
        else for (var i = 0; i < input.Materials.Length; i++)
        {
            var m = input.Materials[i];
            if (m is null) { errors[$"materials.{i}"] = ["Podaj materiał."]; continue; }
            Number($"materials.{i}.qtyKg", m.QtyKg, weight); Number($"materials.{i}.pricePerKg", m.PricePerKg, rate); Number($"materials.{i}.cost", m.Cost, money);
        }
        if (errors.Count > 0) throw new CrmFault(422, "Sprawdź dane wyceny.", errors);
    }
    private async Task<T> Send<T>(HttpContext http, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(http.Request.Headers.Authorization.ToString());
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: LegacyJson);
        try
        {
            using var response = await clients.CreateClient("legacy").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                if (status >= 500 || status is >= 300 and < 400) throw new CrmFault(503, "Nie można potwierdzić operacji. Zachowaj formularz i sprawdź stan zlecenia.");
                var message = "Serwer nie potwierdził operacji.";
                Dictionary<string, string[]>? errors = null;
                try
                {
                    var error = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                    if (error.TryGetProperty("detail", out var detail))
                    {
                        if (detail.ValueKind == JsonValueKind.String) message = detail.GetString()!;
                        else if (detail.ValueKind == JsonValueKind.Array)
                        {
                            message = "Sprawdź wymagane pola i poprawność danych.";
                            errors = new();
                            foreach (var item in detail.EnumerateArray())
                            {
                                var field = string.Join(".", item.GetProperty("loc").EnumerateArray().Skip(1).Select(x => x.ToString()));
                                errors[field] = [item.GetProperty("msg").GetString() ?? message];
                            }
                        }
                    }
                }
                catch (JsonException) { }
                throw new CrmFault(status, message, errors);
            }
            var result = await response.Content.ReadFromJsonAsync<T>(LegacyJson, ct);
            if (result is null || result is OrderDto { Id: <= 0 } or OrderDto { VersionId: <= 0 })
                throw new CrmFault(503, "Nie można odczytać potwierdzenia operacji. Zachowaj formularz.");
            return result;
        }
        catch (HttpRequestException) { throw new CrmFault(503, "Brak połączenia z obsługą zleceń. Zachowaj formularz i sprawdź stan zapisu."); }
        catch (JsonException) { throw new CrmFault(503, "Nie można odczytać odpowiedzi obsługi zleceń. Zachowaj formularz."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CrmFault(503, "Upłynął czas oczekiwania. Zachowaj formularz i sprawdź stan zapisu."); }
    }
    private sealed record LegacySetting(string Key, string? Value);
    private sealed record LegacyPendingQuestion(long OrderId, string? Status);
}
