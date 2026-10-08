using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed partial class CustomerLifecycleViewModel : ObservableObject
{
    public CustomerDto Customer { get; }
    public bool Restore { get; }
    public string CustomerSummary => Format.Customer(Customer);
    public long ExpectedVersion { get; private set; }
    public CustomerDto? Saved { get; private set; }
    public bool CanSave => !IsSaving && !HasConflict && Saved is null;
    public bool CanClose => !IsSaving;
    public bool CanAcceptComparison => HasConflict && compared is not null && !IsSaving;
    private ChangeCustomerLifecycle? pending;
    private CustomerDto? compared;
    private int comparisonVersion;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(CanClose), nameof(CanAcceptComparison))]
    private bool isSaving;
    [ObservableProperty] private bool isUncertain;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(CanAcceptComparison))]
    private bool hasConflict;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string comparison = "";

    public CustomerLifecycleViewModel(CustomerDto customer, bool restore)
    {
        Customer = customer;
        Restore = restore;
        ExpectedVersion = customer.Version;
    }

    public async Task<bool> Save(CrmClient api, CancellationToken ct)
    {
        if (!CanSave) return false;
        IsSaving = true;
        Status = "Zapisywanie…";
        pending ??= new(Guid.NewGuid(), ExpectedVersion);
        try
        {
            var result = await api.Save<CustomerDto>(HttpMethod.Post,
                $"customers/{Customer.Id}/{(Restore ? "restore" : "archive")}", pending, ct);
            if (result.Id != Customer.Id || (result.ArchivedAt is null) != Restore)
                throw new ApiFailure(502, "Odpowiedź serwera nie potwierdza wybranej operacji.");
            Saved = result;
            OnPropertyChanged(nameof(Saved));
            IsUncertain = false;
            Status = Restore ? "Przywrócono klienta." : "Przeniesiono klienta do archiwum.";
            return true;
        }
        catch (ApiFailure error)
        {
            Status = error.Message;
            if (error.Status >= 500) MarkUncertain();
            else if (error.Status == 409)
            {
                IsUncertain = false;
                HasConflict = true;
                compared = null;
                Comparison = "";
                Status += "\nWczytaj aktualne dane i zaakceptuj porównaną wersję przed ponowieniem operacji.";
            }
            else if (error.Status is 404 or 410)
            {
                IsUncertain = false;
                pending = null;
                Status += "\nNie potwierdzono operacji. Klient nie jest dostępny w wymaganym stanie. Odśwież listę klientów.";
            }
            if (error.Status == 401) throw;
            return false;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        {
            MarkUncertain();
            return false;
        }
        finally { IsSaving = false; }
    }

    private void MarkUncertain()
    {
        IsUncertain = true;
        Status = "Nie można potwierdzić operacji. Ponów tę samą operację po odzyskaniu połączenia.";
    }

    public async Task ReloadComparison(CrmClient api, CancellationToken ct)
    {
        if (!HasConflict || IsSaving) return;
        var version = Interlocked.Increment(ref comparisonVersion);
        compared = null;
        Comparison = "";
        OnPropertyChanged(nameof(CanAcceptComparison));
        var current = await api.Get<CustomerDto>($"customers/{Customer.Id}/record", ct);
        ct.ThrowIfCancellationRequested();
        if (version != Volatile.Read(ref comparisonVersion) || !HasConflict) return;
        if (current.Id != Customer.Id) throw new ApiFailure(502, "Serwer zwrócił dane innego klienta.");
        compared = current;
        Comparison = $"Aktualne dane (wersja {current.Version}, {(current.ArchivedAt is null ? "aktywny" : "archiwum")}):\n{Format.Customer(current)}";
        OnPropertyChanged(nameof(CanAcceptComparison));
    }

    public void AcceptComparedVersion()
    {
        if (!CanAcceptComparison) return;
        ExpectedVersion = compared!.Version;
        OnPropertyChanged(nameof(ExpectedVersion));
        compared = null;
        pending = null;
        HasConflict = false;
        Status = "Zaakceptowano aktualną wersję. Potwierdź operację ponownie.";
    }
}
