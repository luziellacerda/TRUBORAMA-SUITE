using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TurboBoxManager;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LicenseBox.Text))
        {
            MessageBox.Show("Digite uma licença de teste.", "Turborama", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        LoginView.Visibility = Visibility.Collapsed;
        ShellView.Visibility = Visibility.Visible;
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string page }) return;
        HomePage.Visibility = page == "Home" ? Visibility.Visible : Visibility.Collapsed;
        CatalogPage.Visibility = page == "Catalogo" ? Visibility.Visible : Visibility.Collapsed;
        DownloadsPage.Visibility = page == "Downloads" ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderPage.Visibility = page is not ("Home" or "Catalogo" or "Downloads") ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderTitle.Text = page;
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Escolha a pasta de instalação" };
        if (dialog.ShowDialog() == true) InstallPathText.Text = dialog.FolderName;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) Maximize_Click(sender, e); else DragMove();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
