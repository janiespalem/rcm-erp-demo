using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed partial class OrderTemplateViewModel : ObservableObject
{
    private readonly OrderDto order;
    private readonly string initialName;
    private readonly string initialCategory;
    private SaveOrderAsTemplate? pending;
    private bool blocked;
    public OrderSavedTemplate? Saved { get; private set; }
    [ObservableProperty] private string name;
    [ObservableProperty] private string category;
    [ObservableProperty] private string? role;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool uncertain;
    [ObservableProperty] private string status = "Zapisz dane zlecenia i wyceny jako szablon do ponownego użycia.";
    [ObservableProperty] private string nameError = "";
    [ObservableProperty] private string categoryError = "";
    public bool Dirty => Saved is null && (Name != initialName || Category != initialCategory || Uncertain);
    private bool Allowed => Role == "technolog" && order.ArchivedAt is null && !blocked;
    public bool CanEdit => !Busy && !Uncertain && Saved is null && Allowed;
    public bool CanSave => !Busy && Saved is null && Allowed;
    public string SaveLabel => Uncertain ? "Ponów ten sam zapis" : "Zapisz szablon";
    public OrderTemplateViewModel(OrderDto order)
    {
        this.order = order;
        name = initialName = $"Z zlecenia {order.OrderNumber ?? $"#{order.Id}"}";
        category = initialCategory = order.OrderType ?? "remont";
    }
    partial void OnNameChanged(string value) { if (!Uncertain) pending = null; Notify(); }
    partial void OnCategoryChanged(string value) { if (!Uncertain) pending = null; Notify(); }
    partial void OnRoleChanged(string? value) => Notify();
    partial void OnBusyChanged(bool value) => Notify();
    partial void OnUncertainChanged(bool value) => Notify();
    private void Notify()
    {
        foreach (var name in new[] { nameof(Dirty), nameof(CanEdit), nameof(CanSave), nameof(SaveLabel), nameof(Saved) }) OnPropertyChanged(name);
    }
    public async Task<bool> Save(CrmClient api, CancellationToken ct)
    {
        Role = api.Session?.Role;
        if (!CanSave) return false;
        if (pending is null)
        {
            NameError = Name.Trim().Length is < 1 or > 200 ? "Wpisz nazwę od 1 do 200 znaków." : "";
            CategoryError = Category.Trim().Length is < 1 or > 50 ? "Wpisz kategorię od 1 do 50 znaków." : "";
            if (NameError.Length != 0 || CategoryError.Length != 0) { Status = "Popraw zaznaczone pola. Wpisane dane zachowano."; return false; }
            pending = new(Guid.NewGuid(), Name.Trim(), Category.Trim());
        }
        Busy = true; Status = "Zapisywanie szablonu…";
        try
        {
            var result = await api.WriteOrder<OrderSavedTemplate>(HttpMethod.Post, $"/{order.Id}/save-as-template", pending, ct);
            if (result.Id <= 0 || string.IsNullOrWhiteSpace(result.Name) || string.IsNullOrWhiteSpace(result.Category))
                throw new ApiFailure(502, "Nieprawidłowe potwierdzenie zapisu szablonu.");
            Saved = result; pending = null; Uncertain = false; Status = $"Zapisano szablon: {result.Name}."; Notify(); return true;
        }
        catch (ApiFailure ex) when (ex.Status == 401)
        { MarkUncertain("Zaloguj się ponownie i ponów ten sam zapis. Wpisane dane zachowano."); throw; }
        catch (ApiFailure ex) when (ex.Status >= 500)
        { MarkUncertain("Brak potwierdzenia zapisu. Ponów ten sam zapis; dane zachowano."); return false; }
        catch (ApiFailure ex)
        {
            var wasUncertain = Uncertain;
            if (!wasUncertain) pending = null;
            blocked = ex.Status is 403 or 404 or 409 or 410;
            NameError = ex.Errors.TryGetValue("name", out var names) ? string.Join("\n", names) : "";
            CategoryError = ex.Errors.TryGetValue("category", out var categories) ? string.Join("\n", categories) : "";
            Status = ex.Message + (blocked ? " Dane zachowano. Sprawdź dostęp i stan zlecenia." : " Dane zachowano.");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { MarkUncertain("Wynik zapisu nieznany. Ponów ten sam zapis; dane i identyfikator zachowano."); return false; }
        finally { Busy = false; }
    }
    private void MarkUncertain(string message) { Uncertain = true; Status = message; }
}
