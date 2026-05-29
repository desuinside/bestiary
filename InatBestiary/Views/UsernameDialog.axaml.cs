using Avalonia.Controls;
using Avalonia.Interactivity;

namespace InatBestiary.Views;

public partial class UsernameDialog : Window
{
    public UsernameDialog() => InitializeComponent();

    private void OkClick(object? sender, RoutedEventArgs e)
    {
        var text = UsernameBox.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
            Close(text);
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
