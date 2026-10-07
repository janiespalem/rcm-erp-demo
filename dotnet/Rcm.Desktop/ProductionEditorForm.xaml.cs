using System.Windows;
using System.Windows.Controls;

namespace Rcm.Desktop;
public partial class ProductionEditorForm : UserControl
{
    public ProductionEditorForm() => InitializeComponent();
    private ProductionEditorWindow? Editor => Window.GetWindow(this) as ProductionEditorWindow;
    private async void Save_Click(object sender, RoutedEventArgs e) { if (Editor is { } editor) await editor.Run(ct => editor.Model.Save(editor.Api, ct)); }
    private async void Retry_Click(object sender, RoutedEventArgs e) { if (Editor is { } editor) await editor.Run(ct => editor.Model.Retry(editor.Api, ct)); }
    private async void Compare_Click(object sender, RoutedEventArgs e) { if (Editor is { } editor) await editor.Run(ct => editor.Model.Compare(editor.Api, ct)); }
    private void Accept_Click(object sender, RoutedEventArgs e) => Editor?.Model.AcceptComparison();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Editor?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Editor?.Close();
}
