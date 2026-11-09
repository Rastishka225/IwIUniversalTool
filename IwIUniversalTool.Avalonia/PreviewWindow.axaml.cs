using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace IwIUniversalTool.Avalonia;

public partial class PreviewWindow : Window
{
    public PreviewWindow()
    {
        InitializeComponent();
    }

    public void ShowImage(WriteableBitmap bitmap, string info)
    {
        PreviewImage.Source = bitmap;
        InfoText.Text = info;
        Title = "Preview — " + info;
    }
}
