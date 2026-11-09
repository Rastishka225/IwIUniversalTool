using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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

    private static readonly string[] SupportedExtensions = { ".iwi", ".dds", ".vtf" };

    private readonly List<TextureItem> _all = new();
    private readonly ObservableCollection<TextureItem> _view = new();
    private string? _outputDir;
    private CancellationTokenSource? _cts;
    private PreviewWindow? _previewWindow;

    public MainWindow()
    {
        InitializeComponent();

        FilesList.ItemsSource = _view;
        GameBox.ItemsSource = GameNames;
        GameBox.SelectedIndex = 0;
        OutputPathBox.Text = "next to source file";
        SetOutputMode(false);
        StatusText.Text = "Add .iwi, .dds or .vtf files to start.";
    }

    // ---------------------------------------------------------------- файлы

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

        AddPaths(picked.Select(p => p.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p))!);
    }

    private async void AddFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add a folder with textures",
            AllowMultiple = false
        });

        string? folder = picked.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(folder)) return;

        try
        {
            var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
            AddPaths(files);
        }
        catch (Exception ex)
        {
            Log("Folder error: " + ex.Message);
        }
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            if (_all.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }

            _all.Add(new TextureItem
            {
                Name = Path.GetFileName(path),
                Path = path,
                Type = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
                SizeText = FormatSize(size),
                Status = "Queued"
            });
            added++;
        }

        RefreshView();
        Log($"Added: {added}");
    }

    private void RemoveButton_Click(object? sender, RoutedEventArgs e)
    {
        var selected = FilesList.SelectedItems?.Cast<TextureItem>().ToList() ?? new List<TextureItem>();
        if (selected.Count == 0) return;

        foreach (var item in selected) _all.Remove(item);
        RefreshView();
        Log($"Removed: {selected.Count}");
    }

    private void ClearButton_Click(object? sender, RoutedEventArgs e)
    {
        _all.Clear();
        _view.Clear();
        EmptyHint.IsVisible = true;
        StatusText.Text = "List cleared.";
        Log("List cleared.");
    }

    private void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in _all)
        {
            try { item.SizeText = FormatSize(new FileInfo(item.Path).Length); } catch { }
            item.Status = "Queued";
        }
        RefreshView();
        Log("List refreshed.");
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => RefreshView();

    private void RefreshView()
    {
        string filter = SearchBox.Text?.Trim() ?? "";
        _view.Clear();
        foreach (var item in _all)
        {
            if (filter.Length == 0 || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                _view.Add(item);
        }
        StatusText.Text = $"Files: {_all.Count} · shown: {_view.Count}";
        EmptyHint.IsVisible = _view.Count == 0;
    }

    // ---------------------------------------------------------------- сохранение

    private void NearbyButton_Click(object? sender, RoutedEventArgs e) => SetOutputMode(false);

    private async void CustomButton_Click(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose output folder",
            AllowMultiple = false
        });

        string? folder = picked.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(folder)) return;

        _outputDir = folder;
        SetOutputMode(true);
        OutputPathBox.Text = folder;
    }

    private void SetOutputMode(bool custom)
    {
        if (!custom) _outputDir = null;

        NearbyButton.Background = new SolidColorBrush(Color.Parse(custom ? "#333337" : "#00A2ED"));
        CustomButton.Background = new SolidColorBrush(Color.Parse(custom ? "#00A2ED" : "#333337"));
        OutputPathBox.Text = custom && _outputDir != null ? _outputDir : "next to source file";
    }

    // ---------------------------------------------------------------- конвертация

    private async void ConvertSelected_Click(object? sender, RoutedEventArgs e)
    {
        var items = FilesList.SelectedItems?.Cast<TextureItem>().ToList() ?? new List<TextureItem>();
        if (items.Count == 0) items = _view.ToList();
        await ConvertAsync(items);
    }

    private async void ConvertAll_Click(object? sender, RoutedEventArgs e) => await ConvertAsync(_view.ToList());

    private void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log("Stopping…");
    }

    private async Task ConvertAsync(IReadOnlyList<TextureItem> items)
    {
        if (items.Count == 0) return;

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        SetBusy(true);

        int gameIndex = Math.Max(0, GameBox.SelectedIndex);
        bool mips = MipsBox.IsChecked == true;
        int done = 0;

        try
        {
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                item.Status = "Converting…";
                LogStatus.Text = $"{done}/{items.Count}";

                try
                {
                    var result = await Task.Run(() => ConvertOne(item.Path, gameIndex, mips, _outputDir), token);
                    await File.WriteAllBytesAsync(result.OutputPath, result.Data, token);
                    item.Status = "Done → " + Path.GetFileName(result.OutputPath);
                    Log($"{item.Name} → {Path.GetFileName(result.OutputPath)}");
                }
                catch (OperationCanceledException)
                {
                    item.Status = "Stopped";
                    throw;
                }
                catch (Exception ex)
                {
                    item.Status = "Error";
                    Log($"Error: {item.Name}: {ex.Message}");
                }

                done++;
                LogStatus.Text = $"{done}/{items.Count}";
            }

            StatusText.Text = "Conversion finished.";
            Log("Finished.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Stopped.";
            Log("Stopped by user.");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private static (byte[] Data, string OutputPath) ConvertOne(string path, int gameIndex, bool mips, string? outputDir)
    {
        byte[] data = File.ReadAllBytes(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string targetDir = !string.IsNullOrEmpty(outputDir)
            ? outputDir
            : Path.GetDirectoryName(path) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(path);

        switch (ext)
        {
            case ".iwi":
                return (IwiCodec.IwiToDds(data), Path.Combine(targetDir, baseName + ".dds"));

            case ".vtf":
                return (VtfCodec.VtfToDds(data), Path.Combine(targetDir, baseName + ".dds"));

            case ".dds":
            {
                var result = IwiCodec.TryDdsToIwi(data, gameIndex, mips, true, out byte[] iwi);
                if (result != IwiBuildResult.Ok)
                    throw new InvalidDataException($"DDS → IWI failed: {result}. Supported: DXT1/DXT3/DXT5, BC5, uncompressed BGRA/BGR.");
                return (iwi, Path.Combine(targetDir, baseName + ".iwi"));
            }

            default:
                throw new InvalidDataException("Unsupported input: " + ext);
        }
    }

    // ---------------------------------------------------------------- превью

    private async void FilesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FilesList.SelectedItem is TextureItem item)
            StatusText.Text = $"{item.Name} — {item.Type}, {item.SizeText}";
    }

    private async void PreviewButton_Click(object? sender, RoutedEventArgs e) => await OpenPreviewAsync();

    private async void FilesList_DoubleTapped(object? sender, TappedEventArgs e) => await OpenPreviewAsync();

    private async Task OpenPreviewAsync()
    {
        if (FilesList.SelectedItem is not TextureItem item) return;

        try
        {
            var decoded = await Task.Run(() => DecodePreview(item.Path));
            var bitmap = CreateBitmap(decoded.Width, decoded.Height, decoded.Bgra);
            string info = $"{item.Name} — {decoded.Info}";

            if (_previewWindow is { IsVisible: true })
            {
                _previewWindow.ShowImage(bitmap, info);
                _previewWindow.Activate();
            }
            else
            {
                _previewWindow = new PreviewWindow();
                _previewWindow.Closed += (_, _) => _previewWindow = null;
                _previewWindow.ShowImage(bitmap, info);
                _previewWindow.Show(this);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Preview error: " + ex.Message;
            Log("Preview error: " + ex.Message);
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
            Marshal.Copy(bgra, y * rowBytes, IntPtr.Add(frame.Address, y * frame.RowBytes), rowBytes);

        return bitmap;
    }

    // ---------------------------------------------------------------- служебное

    private void SetBusy(bool busy)
    {
        AddButton.IsEnabled = !busy;
        AddFolderButton.IsEnabled = !busy;
        RemoveButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        ConvertSelectedButton.IsEnabled = !busy;
        ConvertAllButton.IsEnabled = !busy;
        GameBox.IsEnabled = !busy;
        MipsBox.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
    }

    private void Log(string message)
    {
        LogText.Text += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        LogText.CaretIndex = LogText.Text.Length;
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
    public string Type { get; init; } = "";

    private string _sizeText = "";
    public string SizeText
    {
        get => _sizeText;
        set { if (_sizeText == value) return; _sizeText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText))); }
    }

    private string _status = "Queued";
    public string Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
