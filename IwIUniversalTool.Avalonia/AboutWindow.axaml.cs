using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace IwIUniversalTool.Avalonia;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    private void GitHub_Click(object? sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/Rastishka225/IwIUniversalTool",
            UseShellExecute = true
        });
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
