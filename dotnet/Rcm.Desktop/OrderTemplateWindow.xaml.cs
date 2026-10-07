using System.Windows;
using Rcm.Contracts;

namespace Rcm.Desktop;

public partial class OrderTemplateWindow : Window
{
    public OrderTemplateViewModel Model { get; }
    private readonly CrmClient api;
    private readonly CancellationTokenSource lifetime = new();
    public OrderTemplateWindow(CrmClient api, OrderDto order)
    {
        this.api = api; Model = new(order) { Role = api.Session?.Role };
        InitializeComponent(); DataContext = Model;
        Heading.Text = $"Szablon · {order.OrderNumber ?? $"#{order.Id}"}";
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        api.SessionChanged += SessionChanged;
        Closing += (_, e) =>
        {
            if (Model.Busy) { e.Cancel = true; Model.Status = "Zapis trwa. Poczekaj na odpowiedź."; }
            else if (Model.Dirty && MessageBox.Show(this,
                "Zamknąć formularz i odrzucić niezapisane dane? Przy niepotwierdzonym zapisie ponów operację przed zamknięciem, aby uniknąć duplikatu szablonu.",
                "Niezapisany szablon", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true;
        };
        Closed += (_, _) => { lifetime.Cancel(); api.SessionChanged -= SessionChanged; };
    }
    private void SessionChanged(object? sender, EventArgs e) => Model.Role = api.Session?.Role;
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try { if (await Model.Save(api, lifetime.Token)) DialogResult = true; }
        catch (ApiFailure ex) when (ex.Status == 401)
        {
            if (lifetime.IsCancellationRequested) return;
            new LoginWindow(api) { Owner = this }.ShowDialog();
            Model.Role = api.Session?.Role;
        }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
