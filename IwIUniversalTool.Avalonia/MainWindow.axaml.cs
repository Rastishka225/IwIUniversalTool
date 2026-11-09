using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using IwiThumbs;
using IwIUniversalTool;

namespace IwIUniversalTool.Avalonia;

public partial class MainWindow : Window
{
    private static readonly string[] GameNames =
    {
        "Black Ops 2 (v27)",
        "Modern Warfare 3 (v8)",
        "Black Ops 1 (v13)",
        "Modern Warfare 2 (v8)",
        "World at War (v6)",
        "Modern Warfare 1 (v6)",
        "CoD2 (v5)"
    };

    private readonly ObservableCollection<TextureItem> _files = new();

    public MainWindow()
    {
        InitializeComponent();

        FilesList.ItemsSource = _files;
        GameBox.ItemsSource = GameNames;
        GameBox.SelectedIndex = 0;
        StatusText.Text = "Add .iwi, .dds or .vtf files to start.";
    }

    private async void AddButton_Click(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add textures",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Call of Duty textures")
                {
                    Patterns = new[] { "*.iwi", "*.dds", "*.vtf" }
                }
            }
        });

        foreach (var file in picked)
        {
            string? path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) continue;
            if (_files.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }

            _files.Add(new TextureItem
            {
                Name = Path.GetFileName(path),
                Path = path,
                SizeText = FormatSize(size),
                Status = "Queued"
            });
        }

        StatusText.Text = $"Files: {_files.Count}";
    }

    private void ClearButton_Click(object? sender, RoutedEventArgs e)
    {
        _files.Clear();
        PreviewImage.Source = null;
        StatusText.Text = "List cleared.";
    }

    private async void ConvertSelected_Click(object? sender, RoutedEventArgs e)
    {
        var items = FilesList.SelectedItems?.Cast<TextureItem>().ToList() ?? new List<TextureItem>();
        if (items.Count == 0) items = _files.ToList();
        await ConvertAsync(items);
    }

    private async void ConvertAll_Click(object? sender, RoutedEventArgs e) => await ConvertAsync(_files.ToList());

    private async Task ConvertAsync(IEnumerable<TextureItem> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;

        SetBusy(true);
        int gameIndex = Math.Max(0, GameBox.SelectedIndex);
        bool mips = MipsBox.IsChecked == true;

        try
        {
            foreach (var item in list)
            {
                item.Status = "Converting…";
                try
                {
                    var result = await Task.Run(() => ConvertOne(item.Path, gameIndex, mips));
                    await File.WriteAllBytesAsync(result.OutputPath, result.Data);
                    item.Status = "Done → " + Path.GetFileName(result.OutputPath);
                }
                catch (Exception ex)
                {
                    item.Status = "Error: " + ex.Message;
                }
            }
            StatusText.Text = "Conversion finished.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static (byte[] Data, string OutputPath) ConvertOne(string path, int gameIndex, bool mips)
    {
        byte[] data = File.ReadAllBytes(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();

        switch (ext)
        {
            case ".iwi":
                return (IwiCodec.IwiToDds(data), Path.ChangeExtension(path, ".dds"));

            case ".vtf":
                return (VtfCodec.VtfToDds(data), Path.ChangeExtension(path, ".dds"));

            case ".dds":
            {
                var result = IwiCodec.TryDdsToIwi(data, gameIndex, mips, true, out byte[] iwi);
                if (result != IwiBuildResult.Ok)
                    throw new InvalidDataException($"DDS → IWI failed: {result}. Supported: DXT1/DXT3/DXT5, BC5, uncompressed BGRA/BGR.");
                return (iwi, Path.ChangeExtension(path, ".iwi"));
            }

            default:
                throw new InvalidDataException("Unsupported input: " + ext);
        }
    }

    private async void FilesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FilesList.SelectedItem is not TextureItem item)
        {
            PreviewImage.Source = null;
            return;
        }

        try
        {
            var decoded = await Task.Run(() => DecodePreview(item.Path));
            PreviewImage.Source = CreateBitmap(decoded.Width, decoded.Height, decoded.Bgra);
            StatusText.Text = $"{item.Name} — {decoded.Info}";
        }
        catch (Exception ex)
        {
            PreviewImage.Source = null;
            StatusText.Text = "Preview error: " + ex.Message;
        }
    }

    private static (int Width, int Height, byte[] Bgra, string Info) DecodePreview(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();

        byte[] dds = ext switch
        {
            ".iwi" => IwiCodec.IwiToDds(data),
            ".vtf" => VtfCodec.VtfToDds(data),
            ".dds" => data,
            _ => throw new InvalidDataException("Preview supports .iwi, .dds, .vtf")
        };

        TextureBitmap.DecodeTopLevel(dds, out int w, out int h, out byte[] bgra);
        return (w, h, bgra, $"{w}×{h}, {FormatSize(data.LongLength)}");
    }

    private static WriteableBitmap CreateBitmap(int width, int height, byte[] bgra)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using var frame = bitmap.Lock();
        int rowBytes = width * 4;
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(bgra, y * rowBytes, IntPtr.Add(frame.Address, y * frame.RowBytes), rowBytes);
        }
        return bitmap;
    }

    private void SetBusy(bool busy)
    {
        AddButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
        ConvertSelectedButton.IsEnabled = !busy;
        ConvertAllButton.IsEnabled = !busy;
        GameBox.IsEnabled = !busy;
        MipsBox.IsEnabled = !busy;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("0.##") + " MB";
    }
}

public sealed class TextureItem : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string SizeText { get; init; } = "";

    private string _status = "Queued";
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
