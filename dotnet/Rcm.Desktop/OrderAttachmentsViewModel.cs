using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Rcm.Contracts;

namespace Rcm.Desktop;

public sealed partial class OrderAttachmentsViewModel(OrderDto order) : ObservableObject
{
    public OrderDto Order { get; } = order;
    public ObservableCollection<OrderAttachmentDto> Rows { get; } = [];
    [ObservableProperty] private OrderAttachmentDto? selected;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string uploadPath = "";
    [ObservableProperty] private string? role;
    private Guid uploadRequestId = Guid.NewGuid();
    private Guid removalRequestId = Guid.NewGuid();
    private long? removalTarget;
    public bool CanReload => !Busy;
    public bool CanDownload => !Busy && Selected is not null;
    public bool CanChooseFile => !Busy && Order.ArchivedAt is null && Role is "biuro" or "technolog" or "ceo";
    public bool CanUpload => CanChooseFile && !string.IsNullOrWhiteSpace(UploadPath);
    public bool CanRemove => !Busy && Order.ArchivedAt is null && Role == "technolog" && Selected is not null;
    public bool HasPendingUpload => !string.IsNullOrWhiteSpace(UploadPath);
    partial void OnUploadPathChanged(string value)
    { uploadRequestId = Guid.NewGuid(); OnPropertyChanged(nameof(CanUpload)); OnPropertyChanged(nameof(HasPendingUpload)); }
    partial void OnRoleChanged(string? value) => ChangedActions();
    public string SelectionDetails => Selected is { } item
        ? $"{item.Filename}\nRozmiar: {(item.SizeBytes is { } size ? $"{size:N0} B" : "brak informacji")} · Typ: {item.MimeType ?? "—"}\n" +
          $"Dodano: {item.UploadedAt.ToLocalTime():dd.MM.yyyy HH:mm} · {item.UploadedBy ?? "—"}"
        : "Wybierz załącznik, aby zapisać go na komputerze.";

    partial void OnSelectedChanged(OrderAttachmentDto? value)
    { OnPropertyChanged(nameof(CanDownload)); OnPropertyChanged(nameof(CanRemove)); OnPropertyChanged(nameof(SelectionDetails)); }
    partial void OnBusyChanged(bool value)
    { OnPropertyChanged(nameof(CanReload)); OnPropertyChanged(nameof(CanDownload)); ChangedActions(); }
    private void ChangedActions()
    { OnPropertyChanged(nameof(CanChooseFile)); OnPropertyChanged(nameof(CanUpload)); OnPropertyChanged(nameof(CanRemove)); }

    public async Task Upload(CrmClient api, CancellationToken ct)
    {
        if (!CanUpload) return;
        Busy = true; Status = "Przesyłanie załącznika…";
        try
        {
            var item = await api.UploadOrderAttachment(Order.Id, UploadPath, uploadRequestId, ct);
            if (item.OrderId != Order.Id) throw new ApiFailure(502, "Nieprawidłowe potwierdzenie zapisu.");
            var existing = Rows.FirstOrDefault(row => row.Id == item.Id);
            if (existing is null) Rows.Add(item);
            Selected = existing ?? item;
            UploadPath = "";
            Status = "Załącznik zapisany.";
        }
        finally { Busy = false; }
    }

    public async Task Remove(CrmClient api, CancellationToken ct)
    {
        if (!CanRemove || Selected is not { } item) return;
        if (item.OrderId != Order.Id || !Rows.Contains(item)) throw new ApiFailure(409, "Wybierz załącznik z bieżącego zlecenia.");
        if (removalTarget != item.Id) { removalTarget = item.Id; removalRequestId = Guid.NewGuid(); }
        Busy = true; Status = "Usuwanie załącznika…";
        try
        {
            var result = await api.WriteOrder<RemovedOrderAttachment>(System.Net.Http.HttpMethod.Post,
                $"/{Order.Id}/attachments/{item.Id}/remove", new RemoveOrderAttachment(removalRequestId), ct);
            if (result.Id != item.Id) throw new ApiFailure(502, "Nieprawidłowe potwierdzenie usunięcia.");
            Rows.Remove(item); Selected = null; removalTarget = null;
            Status = "Załącznik usunięty.";
        }
        finally { Busy = false; }
    }

    public async Task Load(CrmClient api, CancellationToken ct)
    {
        if (Busy) return;
        Busy = true; Status = "Wczytywanie załączników…";
        try
        {
            var items = await api.ReadOrders<OrderAttachmentDto[]>($"/{Order.Id}/attachments", ct);
            ct.ThrowIfCancellationRequested();
            if (items.Any(item => item.OrderId != Order.Id)) throw new ApiFailure(502, "Nieprawidłowa lista załączników.");
            var selectedId = Selected?.Id;
            Rows.Clear(); foreach (var item in items) Rows.Add(item);
            Selected = items.FirstOrDefault(item => item.Id == selectedId);
            Status = items.Length == 0 ? "To zlecenie nie ma załączników." : $"Załączniki: {items.Length}.";
        }
        finally { Busy = false; }
    }

    public async Task Download(CrmClient api, string destination, CancellationToken ct)
    {
        if (!CanDownload || Selected is not { } item) return;
        if (item.OrderId != Order.Id || !Rows.Contains(item)) throw new ApiFailure(409, "Wybierz załącznik z bieżącego zlecenia.");
        Busy = true; Status = $"Pobieranie: {item.Filename}…";
        try
        {
            await api.DownloadOrderAttachment(Order.Id, item.Id, destination, ct);
            Status = $"Zapisano plik: {destination}";
        }
        finally { Busy = false; }
    }

    public static string SafeFilename(OrderAttachmentDto attachment) => SafeFilename(attachment.Filename, $"zalacznik-{attachment.Id}");
    public static string SafeFilename(string filename, string fallback)
    {
        var name = filename.Replace('\\', '/').Split('/').Last();
        name = new string(name.Select(c => c < ' ' || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        var extension = Path.GetExtension(name);
        if (extension.Length > 16) extension = "";
        if (name.Length > 180) name = name[..(180 - extension.Length)].TrimEnd(' ', '.') + extension;
        var stem = name.Split('.')[0].TrimEnd(' ');
        var reserved = stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && "123456789¹²³".Contains(stem[3]);
        return string.IsNullOrWhiteSpace(name) || reserved ? $"{fallback}{extension}" : name;
    }
}
