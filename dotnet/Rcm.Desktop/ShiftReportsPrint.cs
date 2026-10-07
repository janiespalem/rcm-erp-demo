using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Rcm.Desktop;

public static class ShiftReportsPrint
{
    public static FlowDocument Create(ShiftReportsEditorViewModel model)
    {
        var report = model.Current;
        var document = new FlowDocument { PageWidth = 793.7, PageHeight = 1122.5, PagePadding = new Thickness(45.35), ColumnWidth = 703, FontFamily = new FontFamily("Segoe UI"), FontSize = 12, Foreground = Brushes.Black, Background = Brushes.White };
        void Text(string value, bool bold = false) => document.Blocks.Add(new Paragraph(new Run(value)) { FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 0, 0, 8) });
        void Rows(IEnumerable<(string label, string value)> rows)
        {
            var table = new Table { CellSpacing = 0 }; table.Columns.Add(new TableColumn { Width = new GridLength(2, GridUnitType.Star) }); table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            var group = new TableRowGroup(); table.RowGroups.Add(group);
            foreach (var (label, value) in rows)
            {
                var row = new TableRow(); foreach (var item in new[] { label, string.IsNullOrEmpty(value) ? "—" : value }) row.Cells.Add(new TableCell(new Paragraph(new Run(item))) { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(.5), Padding = new Thickness(5) }); group.Rows.Add(row);
            }
            document.Blocks.Add(table);
        }
        Text("RAPORT ZMIANY – PRODUKCJA GWIAZDOBLOKÓW", true); Text(model.Heading); Text(model.Description + (model.Dirty ? " · NIEZATWIERDZONE ZMIANY" : "") + (report?.DeletedAt is not null ? " · USUNIĘTY" : ""), true);
        Rows(model.HeaderFields.Select(field => (field.Label, field.Value))); Text("1. ILOŚCI", true); Rows(model.Quantities.Select(field => (field.Label, field.Value)));
        Text("2. SPRZĘT NA PRODUKCJI", true); foreach (var row in model.Equipment) Rows([(row.Label + " · Stan", row.Condition), ("Powód / osoba", row.Reason), ("Uwaga / nr", row.Note)]);
        Text("3. KONTROLA KOŃCOWA", true); Rows(model.Checks.Select(row => (row.Label, row.Answer))); Text("4. UWAGI I NIEZGODNOŚCI", true); Rows(model.Notes.Concat(model.Signatures).Select(field => (field.Label, field.Value)));
        if (report is not null) Text($"Autor: {report.AuthorName} · Utworzono: {report.CreatedAt.LocalDateTime:g}\nZakończono: {report.FinalizedAt?.LocalDateTime.ToString("g") ?? "—"} · {report.FinalizedByName ?? "—"}\nOstatnia zmiana: {report.UpdatedAt.LocalDateTime:g} · Korekty: {report.CorrectionCount}");
        if (model.Correction) Text("Powód niezatwierdzonej korekty: " + model.CorrectionReason);
        return document;
    }
}
