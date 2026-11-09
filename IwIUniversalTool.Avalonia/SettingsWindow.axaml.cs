using Avalonia.Controls;
using Avalonia.Interactivity;

namespace IwIUniversalTool.Avalonia;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
