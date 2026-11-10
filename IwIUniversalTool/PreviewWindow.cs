using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace IwIUniversalTool
{
    internal sealed record TextureInfo(
        string FileName, string FullPath, string Container, string Compression,
        int Width, int Height, long FileSize, int Mips, bool HasAlpha);

    internal sealed record LoadedTexture(BitmapSource Image, byte[] Bgra, TextureInfo Info);

    /// <summary>Читает текстуру для превью и вытаскивает информацию из заголовков (формат, сжатие, мипы).</summary>
    internal static class TextureLoader
    {
        private static uint Cc(string s) => (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));
        private static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);

        public static LoadedTexture Load(string path, bool ru)
        {
            byte[] bytes = ArchiveSupport.ReadAllBytes(path); // обычный путь или файл внутри архива
            string ext = Path.GetExtension(path).ToLowerInvariant();
            var (container, compression, mips) = Inspect(bytes, ext, ru);

            byte[] decodeBytes = bytes;
            ImageMagick.MagickFormat readFormat = ext switch
            {
                ".png" => ImageMagick.MagickFormat.Png,
                ".tga" => ImageMagick.MagickFormat.Tga,
                _ => ImageMagick.MagickFormat.Dds
            };
            if (ext == ".iwi") decodeBytes = IwiCodec.IwiToDds(bytes);
            else if (ext == ".vtf") decodeBytes = VtfCodec.VtfToDds(bytes);

            // Magick ➔ быстрый PNG в памяти ➔ WPF-декодер ➔ BGRA32
            using var image = new ImageMagick.MagickImage(decodeBytes, new ImageMagick.MagickReadSettings { Format = readFormat });
            image.Format = ImageMagick.MagickFormat.Png;
            image.Quality = 10; // zlib уровень 1 без фильтра: для превью важна скорость
            using var ms = new MemoryStream();
            image.Write(ms);
            ms.Position = 0;

            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth, h = converted.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            converted.CopyPixels(px, stride, 0);

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
            bmp.Freeze();

            bool hasAlpha = false;
            for (int i = 3; i < px.Length; i += 4)
            {
                if (px[i] != 255) { hasAlpha = true; break; }
            }

            var info = new TextureInfo(Path.GetFileName(path), path, container, compression, w, h, bytes.LongLength, mips, hasAlpha);
            return new LoadedTexture(bmp, px, info);
        }

        private static (string Container, string Compression, int Mips) Inspect(byte[] b, string ext, bool ru)
        {
            try
            {
                switch (ext)
                {
                    case ".dds": return InspectDds(b, ru);
                    case ".iwi": return InspectIwi(b, ru);
                    case ".png": return InspectPng(b, ru);
                    case ".tga": return InspectTga(b, ru);
                    case ".vtf": return VtfCodec.Describe(b, ru);
                }
            }
            catch { }
            return (ext.TrimStart('.').ToUpperInvariant(), "—", 1);
        }

        private static readonly Dictionary<uint, string> FourCcNames = new()
        {
            [Cc("DXT1")] = "DXT1 (BC1)",
            [Cc("DXT2")] = "DXT2",
            [Cc("DXT3")] = "DXT3 (BC2)",
            [Cc("DXT4")] = "DXT4",
            [Cc("DXT5")] = "DXT5 (BC3)",
            [Cc("ATI1")] = "ATI1 (BC4)",
            [Cc("BC4U")] = "BC4",
            [Cc("ATI2")] = "ATI2 (BC5)",
            [Cc("BC5U")] = "BC5"
        };

        private static string DxgiName(uint f) => f switch
        {
            71 or 72 => "BC1 (DXT1)",
            74 or 75 => "BC2 (DXT3)",
            77 or 78 => "BC3 (DXT5)",
            80 or 81 => "BC4",
            83 or 84 => "BC5",
            95 or 96 => "BC6H",
            98 or 99 => "BC7",
            28 or 29 => "R8G8B8A8",
            87 or 91 => "B8G8R8A8",
            _ => $"DXGI {f}"
        };

        private static (string, string, int) InspectDds(byte[] b, bool ru)
        {
            string unc = ru ? "без сжатия" : "uncompressed";
            if (b.Length < 128 || U32(b, 0) != 0x20534444u)
                return ("DDS", ru ? "заголовок повреждён" : "damaged header", 1);

            uint flags = U32(b, 8);
            int mips = (flags & 0x20000u) != 0 ? (int)Math.Max(1u, U32(b, 28)) : 1;
            uint pf = U32(b, 80), fcc = U32(b, 84), bits = U32(b, 88);

            string comp;
            if ((pf & 0x4u) != 0)
            {
                if (fcc == Cc("DX10") && b.Length >= 148) comp = DxgiName(U32(b, 128));
                else if (FourCcNames.TryGetValue(fcc, out string? known))
                    comp = known;
                else
                    comp = Encoding.ASCII.GetString(BitConverter.GetBytes(fcc)).Trim('\0', ' ');
            }
            else if ((pf & 0x40u) != 0) comp = $"{((pf & 0x1u) != 0 ? "ARGB" : "RGB")} {bits}-bit ({unc})";
            else if ((pf & 0x20000u) != 0) comp = $"Luminance {bits}-bit ({unc})";
            else if ((pf & 0x2u) != 0) comp = $"Alpha {bits}-bit ({unc})";
            else comp = unc;

            return ("DDS", comp, mips);
        }

        private static (string, string, int) InspectIwi(byte[] b, bool ru)
        {
            try
            {
                IwiHeader h = IwiCodec.ParseIwiHeader(b);
                string games = h.Version switch
                {
                    5 => "CoD2",
                    6 => "CoD4 / WaW",
                    8 => "MW2 / MW3",
                    13 => "BO1",
                    27 => "BO2",
                    _ => "?"
                };
                string unc = ru ? "без сжатия" : "uncompressed";
                string comp = h.Format switch
                {
                    IwiFormat.Dxt1 => "DXT1 (BC1)",
                    IwiFormat.Dxt3 => "DXT3 (BC2)",
                    IwiFormat.Dxt5 => "DXT5 (BC3)",
                    IwiFormat.Bc5 => "BC5 (ATI2)",
                    IwiFormat.Argb32 => $"ARGB32 ({unc})",
                    IwiFormat.Rgb24 => $"RGB24 ({unc})",
                    IwiFormat.GrayAlpha16 => $"GA16 ({unc})",
                    IwiFormat.Alpha8 => $"A8 ({unc})",
                    _ => "?"
                };
                string? wavelet = h.Version == 27 ? null : IwiCodec.WaveletName((byte)h.Format);
                if (h.Version == 27 && (byte)h.Format == 0x05)
                    comp = ru ? "R8 (8 бит)" : "R8 (8 bit)";
                if (wavelet != null)
                    comp = wavelet + (ru ? " (сжатие CoD4)" : " (CoD4 compression)");
                return ($"IWI v{h.Version} ({games})", comp, IwiCodec.GetMipCount(b));
            }
            catch (InvalidDataException ex)
            {
                // wavelet-форматы читать не умеем, но заголовок показать можем
                if (b.Length >= 5 && b[0] == (byte)'I' && b[1] == (byte)'W' && b[2] == (byte)'i')
                {
                    string? wavelet = IwiCodec.WaveletName(b[4]);
                    if (wavelet != null)
                        return ($"IWI v{b[3]}", wavelet + (ru ? " (не поддерживается)" : " (not supported)"), 1);
                }
                return ("IWI", ex.Message, 1);
            }
        }

        private static (string, string, int) InspectPng(byte[] b, bool ru)
        {
            if (b.Length < 29 || b[0] != 0x89 || b[1] != 0x50) return ("PNG", "—", 1);
            int depth = b[24];
            string type = b[25] switch
            {
                0 => "Grayscale",
                2 => "RGB",
                3 => "Indexed",
                4 => "Grayscale + Alpha",
                6 => "RGBA",
                _ => "?"
            };
            return ("PNG", $"{type}, {depth}-bit, " + (ru ? "без потерь (Deflate)" : "lossless (Deflate)"), 1);
        }

        private static (string, string, int) InspectTga(byte[] b, bool ru)
        {
            if (b.Length < 18) return ("TGA", "—", 1);
            int type = b[2], bpp = b[16];
            bool rle = type >= 8;
            string name = (type & 7) switch
            {
                1 => "Indexed",
                2 => "True-color",
                3 => "Grayscale",
                _ => "?"
            };
            string packing = rle ? "RLE" : (ru ? "без сжатия" : "uncompressed");
            return ("TGA", $"{name}, {bpp}-bit, {packing}", 1);
        }
    }

    /// <summary>Лёгкий ползунок 0..1 (рисуется вручную, без стандартного Slider).</summary>
    internal sealed class MiniSlider : Grid
    {
        private readonly Border _fill = new()
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        private readonly System.Windows.Shapes.Ellipse _thumb = new()
        {
            Width = 14,
            Height = 14,
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        private double _value;
        private bool _drag;

        public event Action<double>? ValueChanged;

        public MiniSlider(Brush track, Brush fill, Brush thumb, Brush thumbBorder)
        {
            Width = 110;
            Height = 26;
            Margin = new Thickness(4, 0, 4, 0);
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            VerticalAlignment = VerticalAlignment.Center;

            Children.Add(new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = track,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });
            _fill.Background = fill;
            _thumb.Fill = thumb;
            _thumb.Stroke = thumbBorder;
            Children.Add(_fill);
            Children.Add(_thumb);

            SizeChanged += (_, _) => UpdateVisual();
            MouseLeftButtonDown += (_, e) => { _drag = true; CaptureMouse(); SetFromMouse(e); e.Handled = true; };
            MouseMove += (_, e) => { if (_drag) SetFromMouse(e); };
            MouseLeftButtonUp += (_, e) => { _drag = false; ReleaseMouseCapture(); e.Handled = true; };
        }

        /// <summary>Установка значения из кода (событие ValueChanged не вызывается).</summary>
        public double Value
        {
            get => _value;
            set { _value = Math.Clamp(value, 0.0, 1.0); UpdateVisual(); }
        }

        private void SetFromMouse(MouseEventArgs e)
        {
            double usable = ActualWidth - 14;
            if (usable <= 0) return;
            _value = Math.Clamp((e.GetPosition(this).X - 7) / usable, 0.0, 1.0);
            UpdateVisual();
            ValueChanged?.Invoke(_value);
        }

        private void UpdateVisual()
        {
            double usable = Math.Max(0, ActualWidth - 14);
            double x = usable * _value;
            _fill.Width = x + 7;
            _thumb.Margin = new Thickness(x, 0, 0, 0);
        }
    }

    /// <summary>Скруглённая кнопка-«плашка» (обычная или переключаемая) с подсветкой при наведении.</summary>
    internal sealed class PillButton : Border
    {
        private readonly TextBlock _label = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold
        };
        private readonly Brush _hover, _active, _fg, _activeFg;
        private bool _checked, _over;

        public bool IsToggle { get; }
        public Brush RestBackground { get; set; } = Brushes.Transparent;
        public event Action? Clicked;

        public bool IsChecked
        {
            get => _checked;
            set { _checked = value; Refresh(); }
        }

        public string Text
        {
            get => _label.Text;
            set => _label.Text = value;
        }

        public PillButton(string text, Brush hover, Brush active, Brush fg, Brush activeFg, bool toggle = false)
        {
            _hover = hover;
            _active = active;
            _fg = fg;
            _activeFg = activeFg;
            IsToggle = toggle;

            _label.Text = text;
            Child = _label;
            CornerRadius = new CornerRadius(14);
            Padding = new Thickness(12, 5, 12, 5);
            Margin = new Thickness(2, 0, 2, 0);
            Cursor = Cursors.Hand;
            Background = Brushes.Transparent;

            MouseEnter += (_, _) => { _over = true; Refresh(); };
            MouseLeave += (_, _) => { _over = false; Refresh(); };
            MouseLeftButtonUp += (_, e) =>
            {
                if (IsToggle) _checked = !_checked;
                Refresh();
                Clicked?.Invoke();
                e.Handled = true;
            };
            MouseLeftButtonDown += (_, e) => e.Handled = true; // клик по плашке не должен начинать перетаскивание картинки
            IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1.0 : 0.4;
            Refresh();
        }

        private void Refresh()
        {
            Background = _checked ? _active : (_over ? _hover : RestBackground);
            _label.Foreground = _checked ? _activeFg : _fg;
        }
    }

    /// <summary>
    /// Отдельное окно превью текстуры. Открывается/закрывается кнопкой 👁 или клавишей P.
    /// Колесо мыши — масштаб к курсору, ЛКМ — перетаскивание, двойной клик — по размеру окна ⇄ 1:1.
    /// </summary>
    public sealed class PreviewWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private readonly bool _isRu;
        private readonly bool _isDark;
        private readonly string _toggleBinding;

        /// <summary>Привязка «заменить файл» (клавиша или кнопка мыши), приходит из настроек.</summary>
        public string ReplaceHotkey { get; set; } = "";

        /// <summary>Превью просит открыть окно замены для указанного виртуального пути.</summary>
        public Action<string>? ReplaceRequested { get; set; }

        /// <summary>Включена ли горячая кнопка замены (из настроек).</summary>
        public bool ReplaceHotkeyEnabled { get; set; } = true;

        /// <summary>Включена ли горячая кнопка предпросмотра (из настроек).</summary>
        public bool PreviewHotkeyEnabled { get; set; } = true;

        /// <summary>До этого момента игнорируем мышь — чтобы клик, которым окно открыли, не закрыл его сразу.</summary>
        public DateTime IgnoreMouseUntil { get; set; } = DateTime.MinValue;

        private PillButton? _pillReplace;

        private readonly Brush _bg, _card, _text, _muted, _border, _viewerBg, _overlayBg, _hoverBg;
        private readonly Brush _accent = new SolidColorBrush(Color.FromRgb(0, 122, 204));

        // область просмотра
        private readonly Canvas _canvas = new() { Background = Brushes.Transparent, ClipToBounds = true };
        private readonly Border _checker = new() { IsHitTestVisible = false };
        private readonly Image _image = new() { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        private readonly MatrixTransform _matrix = new();
        private readonly StackPanel _placeholder = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        private readonly TextBlock _placeholderText = new()
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 380,
            FontSize = 14,
            Margin = new Thickness(0, 8, 0, 0)
        };
        private readonly Border _loadingChip = new() { Visibility = Visibility.Collapsed };
        private readonly TextBlock _chipRes = new();
        private readonly TextBlock _chipFormat = new();
        private readonly StackPanel _chipsPanel = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        private readonly TextBlock _zoomText = new()
        {
            Width = 58,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold
        };
        private PillButton _pillAlpha = null!;
        private PillButton _pillOnlyAlpha = null!;
        private PillButton _pillFit = null!;
        private MiniSlider _slider = null!;
        private const double MinScale = 0.02, MaxScale = 64.0;

        // панель информации
        private readonly TextBlock _titleText = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly WrapPanel _badges = new() { Margin = new Thickness(0, 10, 0, 0) };
        private readonly StackPanel _infoPanel = new();

        // состояние
        private int _loadId;
        private string? _currentPath;
        private BitmapSource? _original;
        private byte[]? _bgra;
        private int _w, _h;
        private double _scale = 1.0, _tx, _ty;
        private bool _fit = true;
        private bool _dragging;
        private Point _dragStart;
        private double _dragTx, _dragTy;

        public PreviewWindow(Window owner, bool isDark, bool isRu, string toggleBinding = "Key:P")
        {
            _isRu = isRu;
            _isDark = isDark;
            _toggleBinding = toggleBinding;

            _bg = new SolidColorBrush(isDark ? Color.FromRgb(28, 28, 30) : Color.FromRgb(238, 240, 244));
            _card = new SolidColorBrush(isDark ? Color.FromRgb(37, 37, 40) : Colors.White);
            _text = new SolidColorBrush(isDark ? Color.FromRgb(245, 245, 247) : Color.FromRgb(28, 30, 33));
            _muted = new SolidColorBrush(isDark ? Color.FromRgb(150, 150, 158) : Color.FromRgb(105, 110, 122));
            _border = new SolidColorBrush(isDark ? Color.FromRgb(58, 58, 62) : Color.FromRgb(218, 221, 228));
            _viewerBg = new SolidColorBrush(isDark ? Color.FromRgb(20, 20, 22) : Color.FromRgb(224, 227, 233));
            _overlayBg = new SolidColorBrush(isDark ? Color.FromArgb(0xDD, 30, 30, 33) : Color.FromArgb(0xEE, 255, 255, 255));
            _hoverBg = new SolidColorBrush(isDark ? Color.FromArgb(0x30, 255, 255, 255) : Color.FromArgb(0x1C, 0, 0, 0));

            Owner = null;
            Title = isRu ? "Предпросмотр" : "Preview";
            Width = 1060;
            Height = 700;
            MinWidth = 980;
            MinHeight = 460;
            ShowInTaskbar = true;
            ShowActivated = false; // фокус остаётся в списке — можно листать файлы стрелками
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = _bg;
            UseLayoutRounding = true;

            // Тёмная рамка окна в тёмной теме (Windows 10 20H1+ / 11)
            SourceInitialized += (_, _) =>
            {
                try
                {
                    int value = _isDark ? 1 : 0;
                    DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref value, sizeof(int));
                }
                catch { }
            };

            // ---------- левая часть: просмотр
            _checker.Background = MakeChecker(isDark);
            _checker.Visibility = Visibility.Collapsed;
            _image.RenderTransform = _matrix;
            _canvas.Children.Add(_checker);
            _canvas.Children.Add(_image);
            _canvas.Cursor = Cursors.Arrow;

            _placeholder.Children.Add(new TextBlock { Text = "🖼", FontSize = 52, Foreground = _muted, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.6 });
            _placeholderText.Foreground = _muted;
            _placeholder.Children.Add(_placeholderText);

            var viewerGrid = new Grid();
            viewerGrid.Children.Add(_canvas);
            viewerGrid.Children.Add(_placeholder);
            viewerGrid.Children.Add(BuildChips());
            viewerGrid.Children.Add(BuildAlphaToolbar());
            viewerGrid.Children.Add(BuildZoomToolbar());
            viewerGrid.Children.Add(BuildLoadingChip());
            viewerGrid.SizeChanged += (_, e) =>
                viewerGrid.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 10, 10);

            var viewerBorder = new Border
            {
                Background = _viewerBg,
                BorderBrush = _border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Margin = new Thickness(12, 12, 6, 12),
                Child = viewerGrid
            };

            _canvas.SizeChanged += (_, _) => { if (_fit) FitToView(); else ApplyTransform(); };
            _canvas.MouseWheel += Canvas_MouseWheel;
            _canvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;
            _canvas.MouseMove += Canvas_MouseMove;
            _canvas.MouseLeftButtonUp += (_, _) => EndDrag();
            _canvas.LostMouseCapture += (_, _) => EndDrag();

            // мышиная горячая кнопка замены работает над картинкой
            _canvas.PreviewMouseDown += (_, e) =>
            {
                if (DateTime.UtcNow < IgnoreMouseUntil) return;
                if (e.Handled) return;
                bool left = e.ChangedButton == MouseButton.Left;
                if (left)
                {
                    if (e.ClickCount < 2) return;
                    if (ReplaceHotkeyEnabled && !PreviewHotkeyEnabled && RequestReplace()) e.Handled = true;
                    return;
                }
                if (HotkeyBinding.MatchesMouse(ReplaceHotkey, e.ChangedButton) && RequestReplace())
                    e.Handled = true;
            };
            _canvas.PreviewMouseWheel += (_, e) =>
            {
                if (DateTime.UtcNow < IgnoreMouseUntil) return;
                if (ReplaceHotkeyEnabled && HotkeyBinding.MatchesWheel(ReplaceHotkey, e.Delta) && RequestReplace())
                    e.Handled = true;
            };

            // мышиная горячая кнопка предпросмотра (закрыть окно)
            _canvas.PreviewMouseDown += (_, e) =>
            {
                if (DateTime.UtcNow < IgnoreMouseUntil) return;
                if (e.Handled) return;
                bool left = e.ChangedButton == MouseButton.Left;
                if (left)
                {
                    // двойной клик ЛКМ не закрывает окно; закрытие — Esc / P / кнопка закрытия
                    return;
                }
                if (HotkeyBinding.MatchesMouse(_toggleBinding, e.ChangedButton))
                {
                    e.Handled = true;
                    Close();
                }
            };
            _canvas.PreviewMouseWheel += (_, e) =>
            {
                if (DateTime.UtcNow < IgnoreMouseUntil) return;
                if (e.Handled) return;
                if (HotkeyBinding.MatchesWheel(_toggleBinding, e.Delta))
                {
                    e.Handled = true;
                    Close();
                }
            };

            // ---------- правая часть: информация
            var infoCard = BuildInfoCard();

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            Grid.SetColumn(viewerBorder, 0);
            Grid.SetColumn(infoCard, 1);
            root.Children.Add(viewerBorder);
            root.Children.Add(infoCard);
            Content = root;

            PreviewKeyDown += (_, e) =>
            {
                if (HotkeyBinding.MatchesKey(ReplaceHotkey, e.Key == Key.System ? e.SystemKey : e.Key)
                    && Keyboard.Modifiers == ModifierKeys.None
                    && RequestReplace())
                {
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Escape || (Keyboard.Modifiers == ModifierKeys.None
                    && HotkeyBinding.MatchesKey(_toggleBinding, e.Key == Key.System ? e.SystemKey : e.Key)))
                {
                    e.Handled = true;
                    Close();
                }
            };

            ShowEmpty(isRu ? "Выберите файл в списке" : "Pick a file in the list");
        }

        // ============================================================ построение интерфейса

        private PillButton MakePill(string text, bool toggle = false)
            => new PillButton(text, _hoverBg, _accent, _text, Brushes.White, toggle);

        private Border MakeOverlay(UIElement child, double radius, double padding = 3)
        {
            return new Border
            {
                Background = _overlayBg,
                CornerRadius = new CornerRadius(radius),
                Padding = new Thickness(padding),
                Child = child,
                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.35, Color = Colors.Black }
            };
        }

        private UIElement BuildChips()
        {
            _chipRes.Foreground = _text;
            _chipRes.FontWeight = FontWeights.SemiBold;
            _chipRes.FontSize = 12.5;
            _chipFormat.Foreground = _muted;
            _chipFormat.FontSize = 12.5;

            var resBox = new Border { Padding = new Thickness(10, 4, 10, 4), Child = _chipRes };
            var fmtBox = new Border { Padding = new Thickness(0, 4, 10, 4), Child = _chipFormat };
            var inner = new StackPanel { Orientation = Orientation.Horizontal };
            inner.Children.Add(resBox);
            inner.Children.Add(fmtBox);

            var chip = MakeOverlay(inner, 13, 0);
            _chipsPanel.Children.Add(chip);
            _chipsPanel.HorizontalAlignment = HorizontalAlignment.Left;
            _chipsPanel.VerticalAlignment = VerticalAlignment.Top;
            _chipsPanel.Margin = new Thickness(14, 14, 0, 0);
            return _chipsPanel;
        }

        private UIElement BuildAlphaToolbar()
        {
            _pillAlpha = MakePill(_isRu ? "◐ Альфа" : "◐ Alpha", true);
            _pillAlpha.IsChecked = true;
            _pillOnlyAlpha = MakePill(_isRu ? "▣ Только альфа" : "▣ Alpha only", true);
            _pillAlpha.Clicked += UpdateImageVariant;
            _pillOnlyAlpha.Clicked += UpdateImageVariant;

            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            _pillReplace = MakePill(_isRu ? "✎ Заменить" : "✎ Replace");
            _pillReplace.Clicked += () => RequestReplace();
            _pillReplace.Visibility = Visibility.Collapsed;
            _pillReplace.ToolTip = _isRu ? "Заменить файл в архиве" : "Replace the file in the archive";
            stack.Children.Add(_pillReplace);
            stack.Children.Add(_pillAlpha);
            stack.Children.Add(_pillOnlyAlpha);

            var box = MakeOverlay(stack, 17);
            box.HorizontalAlignment = HorizontalAlignment.Right;
            box.VerticalAlignment = VerticalAlignment.Top;
            box.Margin = new Thickness(0, 12, 14, 0);
            return box;
        }

        private UIElement BuildZoomToolbar()
        {
            var zoomOut = MakePill("−");
            var zoomIn = MakePill("+");
            var one = MakePill("1:1");
            _pillFit = MakePill(_isRu ? "⤢ По размеру" : "⤢ Fit", true);
            _pillFit.IsChecked = true;

            zoomOut.Clicked += () => ZoomAt(ViewCenter(), _scale / 1.25);
            zoomIn.Clicked += () => ZoomAt(ViewCenter(), _scale * 1.25);
            one.Clicked += () => ZoomAt(ViewCenter(), 1.0);
            _pillFit.Clicked += () =>
            {
                // плашка всегда возвращает «по размеру окна»; включённая плашка = режим подгонки активен
                FitToView();
            };

            _zoomText.Foreground = _text;
            _zoomText.Text = "100%";

            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            _slider = new MiniSlider(
                new SolidColorBrush(_isDark ? Color.FromRgb(78, 78, 84) : Color.FromRgb(200, 203, 210)),
                _accent, Brushes.White, _accent);
            _slider.ValueChanged += t => ZoomAt(ViewCenter(), MinScale * Math.Pow(MaxScale / MinScale, t));

            stack.Children.Add(zoomOut);
            stack.Children.Add(_slider);
            stack.Children.Add(zoomIn);
            stack.Children.Add(_zoomText);
            stack.Children.Add(new Border { Width = 1, Margin = new Thickness(6, 5, 6, 5), Background = _border });
            stack.Children.Add(one);
            stack.Children.Add(_pillFit);

            var box = MakeOverlay(stack, 18, 4);
            box.HorizontalAlignment = HorizontalAlignment.Center;
            box.VerticalAlignment = VerticalAlignment.Bottom;
            box.Margin = new Thickness(0, 0, 0, 16);
            return box;
        }

        private UIElement BuildLoadingChip()
        {
            _loadingChip.Background = _overlayBg;
            _loadingChip.CornerRadius = new CornerRadius(13);
            _loadingChip.Padding = new Thickness(12, 5, 12, 5);
            _loadingChip.HorizontalAlignment = HorizontalAlignment.Center;
            _loadingChip.VerticalAlignment = VerticalAlignment.Top;
            _loadingChip.Margin = new Thickness(0, 16, 0, 0);
            _loadingChip.IsHitTestVisible = false;
            _loadingChip.Child = new TextBlock { Text = _isRu ? "⏳ Загружаю…" : "⏳ Loading…", Foreground = _text, FontSize = 12.5 };
            return _loadingChip;
        }

        private UIElement BuildInfoCard()
        {
            _titleText.Foreground = _text;
            _titleText.Text = "—";

            var header = new StackPanel { Margin = new Thickness(18, 18, 18, 12) };
            header.Children.Add(_titleText);
            header.Children.Add(_badges);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _infoPanel,
                Padding = new Thickness(18, 0, 18, 0)
            };

            var btnExplorer = MakePill(_isRu ? "📂 В проводнике" : "📂 Show in folder");
            var btnCopy = MakePill(_isRu ? "📋 Копировать путь" : "📋 Copy path");
            foreach (var b in new[] { btnExplorer, btnCopy })
            {
                b.RestBackground = _viewerBg;
                b.Background = _viewerBg;
                b.Margin = new Thickness(0, 0, 8, 0);
                b.BorderBrush = _border;
                b.BorderThickness = new Thickness(1);
            }
            btnExplorer.Clicked += OpenInExplorer;
            btnCopy.Clicked += CopyPath;

            var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 12, 18, 16) };
            footer.Children.Add(btnExplorer);
            footer.Children.Add(btnCopy);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(scroll, 1);
            Grid.SetRow(footer, 2);
            grid.Children.Add(header);
            grid.Children.Add(scroll);
            grid.Children.Add(footer);

            return new Border
            {
                Background = _card,
                BorderBrush = _border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Margin = new Thickness(6, 12, 12, 12),
                Child = grid
            };
        }

        private static Brush MakeChecker(bool isDark)
        {
            Color a = isDark ? Color.FromRgb(66, 66, 70) : Color.FromRgb(255, 255, 255);
            Color b = isDark ? Color.FromRgb(46, 46, 49) : Color.FromRgb(206, 208, 214);

            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(a), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(b), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(b), null, new RectangleGeometry(new Rect(8, 8, 8, 8))));

            var brush = new DrawingBrush(group)
            {
                TileMode = TileMode.Tile,
                Viewbox = new Rect(0, 0, 16, 16),
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 16, 16),
                ViewportUnits = BrushMappingMode.Absolute
            };
            brush.Freeze();
            return brush;
        }

        // ============================================================ публичный API

        /// <summary>Просит открыть окно замены для текущей записи архива.</summary>
        private bool RequestReplace()
        {
            string? path = _currentPath;
            if (string.IsNullOrEmpty(path) || path.IndexOf('>') < 0) return false;

            Action<string>? handler = ReplaceRequested;
            if (handler == null) return false;

            handler(path);
            return true;
        }

        public async void ShowFile(string? path)
        {
            int id = ++_loadId;
            _currentPath = path;

            if (_pillReplace != null)
            {
                _pillReplace.Visibility = path != null && path.IndexOf('>') >= 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                _pillReplace.ToolTip = (_isRu ? "Заменить файл в архиве" : "Replace the file in the archive")
                    + " (" + HotkeyBinding.Display(ReplaceHotkey, _isRu) + ")";
            }

            if (string.IsNullOrEmpty(path))
            {
                ShowEmpty(_isRu ? "Выберите файл в списке" : "Pick a file in the list");
                return;
            }

            Title = (_isRu ? "Предпросмотр — " : "Preview — ") + Path.GetFileName(path);
            _loadingChip.Visibility = Visibility.Visible;

            try
            {
                await Task.Delay(60); // при быстром листании стрелками не декодируем каждый промежуточный файл
                if (id != _loadId) return;

                bool ru = _isRu;
                LoadedTexture loaded = await Task.Run(() => TextureLoader.Load(path, ru));
                if (id != _loadId) return;

                bool sameSize = loaded.Info.Width == _w && loaded.Info.Height == _h;
                _original = loaded.Image;
                _bgra = loaded.Bgra;
                _w = loaded.Info.Width;
                _h = loaded.Info.Height;

                _placeholder.Visibility = Visibility.Collapsed;
                _image.Visibility = Visibility.Visible;
                _checker.Visibility = Visibility.Visible;
                _canvas.Cursor = Cursors.Hand;
                _image.Width = _w;
                _image.Height = _h;
                UpdateImageVariant();

                if (_fit || !sameSize) FitToView();
                else ApplyTransform();

                _image.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(150)));
                BuildInfo(loaded.Info);
                _loadingChip.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                if (id != _loadId) return;
                ShowEmpty((_isRu ? "Не получилось открыть файл:\n" : "Couldn't open the file:\n") + ex.Message);
                BuildBasicInfo(path);
            }
        }

        // ============================================================ масштаб и перемещение

        private Point ViewCenter() => new Point(_canvas.ActualWidth / 2, _canvas.ActualHeight / 2);

        private void FitToView()
        {
            double vw = _canvas.ActualWidth, vh = _canvas.ActualHeight;
            if (_original == null || vw <= 0 || vh <= 0 || _w <= 0 || _h <= 0) return;

            const double margin = 24;
            double fit = Math.Min(Math.Max(vw - margin * 2, 40) / _w, Math.Max(vh - margin * 2, 40) / _h);
            _scale = Math.Min(fit, 32.0);
            _fit = true;
            ApplyTransform();
        }

        private void ZoomAt(Point p, double newScale)
        {
            if (_original == null || _scale <= 0) return;
            newScale = Math.Clamp(newScale, MinScale, MaxScale);
            double k = newScale / _scale;
            _tx = p.X - (p.X - _tx) * k;
            _ty = p.Y - (p.Y - _ty) * k;
            _scale = newScale;
            _fit = false;
            ApplyTransform();
        }

        private void ApplyTransform()
        {
            double vw = _canvas.ActualWidth, vh = _canvas.ActualHeight;
            _pillFit.IsChecked = _fit;
            _zoomText.Text = $"{Math.Round(_scale * 100)}%";
            _slider.Value = Math.Log(Math.Max(_scale, MinScale) / MinScale) / Math.Log(MaxScale / MinScale);
            if (_original == null || vw <= 0 || vh <= 0) return;

            double sw = _w * _scale, sh = _h * _scale;

            // картинка меньше области — по центру; больше — не даём «уехать» за край
            _tx = sw <= vw ? (vw - sw) / 2 : Math.Min(0, Math.Max(vw - sw, _tx));
            _ty = sh <= vh ? (vh - sh) / 2 : Math.Min(0, Math.Max(vh - sh, _ty));

            _matrix.Matrix = new Matrix(_scale, 0, 0, _scale, _tx, _ty);
            RenderOptions.SetBitmapScalingMode(_image, _scale >= 2.0 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

            // шахматный фон — под видимой частью картинки, с постоянным размером клеток
            double l = Math.Max(0, _tx), t = Math.Max(0, _ty);
            double r = Math.Min(vw, _tx + sw), b = Math.Min(vh, _ty + sh);
            Canvas.SetLeft(_checker, l);
            Canvas.SetTop(_checker, t);
            _checker.Width = Math.Max(0, r - l);
            _checker.Height = Math.Max(0, b - t);
        }

        private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_original == null) return;
            ZoomAt(e.GetPosition(_canvas), _scale * Math.Pow(1.0015, e.Delta));
            e.Handled = true;
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_original == null) return;

            if (e.ClickCount == 2)
            {
                // двойной клик: по размеру окна ⇄ 100% в точке курсора
                if (_fit) ZoomAt(e.GetPosition(_canvas), 1.0);
                else FitToView();
                return;
            }

            _dragging = true;
            _dragStart = e.GetPosition(_canvas);
            _dragTx = _tx;
            _dragTy = _ty;
            _canvas.CaptureMouse();
            _canvas.Cursor = Cursors.SizeAll;
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Point p = e.GetPosition(_canvas);
            _tx = _dragTx + (p.X - _dragStart.X);
            _ty = _dragTy + (p.Y - _dragStart.Y);
            ApplyTransform();
        }

        private void EndDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            _canvas.ReleaseMouseCapture();
            _canvas.Cursor = _original != null ? Cursors.Hand : Cursors.Arrow;
        }

        // ============================================================ отображение

        private void ShowEmpty(string message)
        {
            _original = null;
            _bgra = null;
            _w = _h = 0;
            _image.Source = null;
            _image.Visibility = Visibility.Collapsed;
            _checker.Visibility = Visibility.Collapsed;
            _chipsPanel.Visibility = Visibility.Collapsed;
            _loadingChip.Visibility = Visibility.Collapsed;
            _canvas.Cursor = Cursors.Arrow;
            _placeholderText.Text = message;
            _placeholder.Visibility = Visibility.Visible;
            _titleText.Text = "—";
            _badges.Children.Clear();
            _infoPanel.Children.Clear();
        }

        private void UpdateImageVariant()
        {
            _pillAlpha.IsEnabled = !_pillOnlyAlpha.IsChecked;

            if (_original == null || _bgra == null) return;

            BitmapSource shown = _original;
            if (_pillOnlyAlpha.IsChecked) shown = MakeVariant(true);
            else if (!_pillAlpha.IsChecked) shown = MakeVariant(false);
            _image.Source = shown;
        }

        /// <summary>Вариант изображения: только альфа (серым) или без альфы (полностью непрозрачный).</summary>
        private BitmapSource MakeVariant(bool onlyAlpha)
        {
            byte[] src = _bgra!;
            var dst = new byte[src.Length];
            if (onlyAlpha)
            {
                for (int i = 0; i < src.Length; i += 4)
                {
                    byte a = src[i + 3];
                    dst[i] = a; dst[i + 1] = a; dst[i + 2] = a; dst[i + 3] = 255;
                }
            }
            else
            {
                for (int i = 0; i < src.Length; i += 4)
                {
                    dst[i] = src[i]; dst[i + 1] = src[i + 1]; dst[i + 2] = src[i + 2]; dst[i + 3] = 255;
                }
            }
            var bmp = BitmapSource.Create(_w, _h, 96, 96, PixelFormats.Bgra32, null, dst, _w * 4);
            bmp.Freeze();
            return bmp;
        }

        // ============================================================ информация о файле

        private Border MakeBadge(string text, Color back, Color fore)
        {
            return new Border
            {
                Background = new SolidColorBrush(back),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(0, 0, 6, 6),
                Child = new TextBlock { Text = text, Foreground = new SolidColorBrush(fore), FontSize = 11.5, FontWeight = FontWeights.SemiBold }
            };
        }

        private void BuildInfo(TextureInfo i)
        {
            bool ru = _isRu;

            _titleText.Text = i.FileName;

            // бейджи: цветной формат, сжатие, альфа
            Color accent = i.Container.StartsWith("IWI") ? Color.FromRgb(156, 74, 214)
                         : i.Container.StartsWith("DDS") ? Color.FromRgb(0, 122, 204)
                         : i.Container.StartsWith("PNG") ? Color.FromRgb(46, 160, 67)
                         : i.Container.StartsWith("VTF") ? Color.FromRgb(214, 69, 65)
                         : Color.FromRgb(224, 128, 36);
            _badges.Children.Clear();
            _badges.Children.Add(MakeBadge(i.Container, accent, Colors.White));
            _badges.Children.Add(MakeBadge(i.Compression, _isDark ? Color.FromRgb(58, 58, 64) : Color.FromRgb(232, 234, 240), _isDark ? Colors.White : Color.FromRgb(40, 42, 48)));
            _badges.Children.Add(i.HasAlpha
                ? MakeBadge(ru ? "альфа" : "alpha", Color.FromRgb(38, 120, 70), Colors.White)
                : MakeBadge(ru ? "без альфы" : "no alpha", _isDark ? Color.FromRgb(58, 58, 64) : Color.FromRgb(232, 234, 240), _isDark ? Color.FromRgb(170, 170, 178) : Color.FromRgb(90, 94, 104)));

            // плашки поверх картинки
            _chipRes.Text = $"{i.Width} × {i.Height}";
            _chipFormat.Text = "· " + i.Compression;
            _chipsPanel.Visibility = Visibility.Visible;

            _infoPanel.Children.Clear();

            bool pot = IsPow2(i.Width) && IsPow2(i.Height);
            string res = $"{i.Width} × {i.Height}";
            if (!pot) res += ru ? "  · не степень двойки" : "  · not a power of two";

            AddRow("📐", ru ? "Разрешение" : "Resolution", res);
            AddRow("🧱", ru ? "Формат" : "Format", i.Container);
            AddRow("🗜", ru ? "Сжатие" : "Compression", i.Compression);
            AddRow("💾", ru ? "Размер файла" : "File size", FormatBytes(i.FileSize, ru));
            AddRow("🎞", ru ? "Мип-уровни" : "Mip levels", i.Mips.ToString());
            AddRow("🎨", ru ? "Альфа-канал" : "Alpha channel",
                i.HasAlpha
                    ? (ru ? "есть прозрачность" : "has transparency")
                    : (ru ? "нет, всё непрозрачное" : "none, fully opaque"));
            AddRow("📁", ru ? "Путь" : "Path", i.FullPath);
        }

        private void BuildBasicInfo(string path)
        {
            _titleText.Text = Path.GetFileName(path);
            _badges.Children.Clear();
            _infoPanel.Children.Clear();
            try { AddRow("💾", _isRu ? "Размер файла" : "File size", FormatBytes(new FileInfo(path).Length, _isRu)); } catch { }
            AddRow("📁", _isRu ? "Путь" : "Path", path);
        }

        private void AddRow(string icon, string label, string value)
        {
            if (_infoPanel.Children.Count > 0)
                _infoPanel.Children.Add(new Border { Height = 1, Background = _border, Opacity = 0.7 });

            var grid = new Grid { Margin = new Thickness(0, 10, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var stack = new StackPanel();
            Grid.SetColumn(stack, 1);
            stack.Children.Add(new TextBlock { Text = label, Foreground = _muted, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
            stack.Children.Add(new TextBox
            {
                Text = value,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = _text,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(0),
                FontSize = 13
            });

            grid.Children.Add(new TextBlock { Text = icon, FontSize = 16, Foreground = _text, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
            grid.Children.Add(stack);
            _infoPanel.Children.Add(grid);
        }

        private static bool IsPow2(int v) => v > 0 && (v & (v - 1)) == 0;

        private static string FormatBytes(long bytes, bool ru)
        {
            string[] suf = ru ? new[] { "Б", "КБ", "МБ", "ГБ" } : new[] { "B", "KB", "MB", "GB" };
            double v = bytes;
            int p = 0;
            while (v >= 1024 && p < suf.Length - 1) { v /= 1024; p++; }
            string shortText = p == 0 ? $"{bytes} {suf[0]}" : $"{v:0.##} {suf[p]}";
            return p == 0 ? shortText : $"{shortText}  ({bytes:N0} {suf[0]})";
        }

        private void OpenInExplorer()
        {
            try
            {
                // для файла из архива показываем в проводнике сам архив
                string real = string.IsNullOrEmpty(_currentPath) ? "" : ArchiveSupport.RealPath(_currentPath);
                if (real.Length > 0 && File.Exists(real))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{real}\"") { UseShellExecute = true });
            }
            catch { }
        }

        private void CopyPath()
        {
            try
            {
                if (!string.IsNullOrEmpty(_currentPath)) Clipboard.SetText(_currentPath);
            }
            catch { }
        }
    }
}