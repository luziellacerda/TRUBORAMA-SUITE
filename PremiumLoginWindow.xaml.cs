using System.Windows;
using System.Windows.Input;

namespace TurboBoxManager;

public partial class PremiumLoginWindow : Window
{
    public PremiumLoginWindow() => InitializeComponent();

    private void Enter_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LicenseInput.Text))
        {
            StatusText.Text = "INFORME SUA CHAVE DE ACESSO";
            StatusText.Visibility = Visibility.Visible;
            return;
        }

        new StoreWindow(true).Show();
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize(); else DragMove();
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
