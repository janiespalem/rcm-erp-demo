using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed partial class OrderDocumentsViewModel(OrderDto order) : ObservableObject
{
    public OrderDto Order { get; } = order;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private string status = "Wybierz dokument i folder zapisu.";
    [ObservableProperty] private string? role;
    public bool ShowOferta => !Order.IsInternal;
    public bool ShowOperations => Role == "technolog";
    public bool CanDownload => !Busy && Role is "biuro" or "technolog" or "ceo";
    public bool CanOferta => CanDownload && ShowOferta;
    public bool CanOperations => CanDownload && ShowOperations;
    partial void OnBusyChanged(bool value) => Notify();
    partial void OnRoleChanged(string? value) => Notify();
    private void Notify()
    {
        foreach (var name in new[] { nameof(CanDownload), nameof(CanOferta), nameof(CanOperations), nameof(ShowOperations) }) OnPropertyChanged(name);
    }
    public bool CanDownloadKind(string kind) => kind switch
    { "arkusz" => CanDownload, "oferta" => CanOferta, "operations" => CanOperations, _ => false };
    public string Filename(string kind)
    {
        var number = (Order.OrderNumber ?? Order.Id.ToString()).Replace('/', '_').Replace('\\', '_');
        var filename = kind switch
        {
            "arkusz" => $"Arkusz_{number}.pdf",
            "oferta" => $"Oferta_{number}.pdf",
            "operations" => $"Arkusze_operacji_{number}.zip",
            _ => throw new ArgumentException("Nieznany rodzaj dokumentu.", nameof(kind))
        };
        return OrderAttachmentsViewModel.SafeFilename(filename, $"Dokument_{Order.Id}");
    }
    public async Task Download(CrmClient api, string kind, string destination, CancellationToken ct)
    {
        Role = api.Session?.Role;
        if (!CanDownloadKind(kind)) return;
        Busy = true; Status = "Generowanie i pobieranie dokumentu…";
        try
        {
            await api.DownloadOrderDocument(Order.Id, kind, destination, ct);
            Status = $"Zapisano dokument: {destination}";
        }
        finally { Busy = false; }
    }
}
