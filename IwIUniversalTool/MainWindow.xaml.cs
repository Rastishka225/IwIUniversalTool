using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using System.Windows.Threading;
using ImageMagick;
using Microsoft.Win32;

namespace IwIUniversalTool
{
    public partial class MainWindow : Window
    {
        public BulkObservableCollection<TextureFile> FilesList { get; }
        private readonly HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _sourceFolders = new(StringComparer.OrdinalIgnoreCase); // папки, добавленные целиком
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly string _settingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        private AppSettings _settings = new();

        // Механизм Паузы / Продолжения
        private readonly ManualResetEventSlim _pauseEvent = new(true);
        private bool _isPaused = false;

        public MainWindow()
        {
            InitializeComponent();
            FilesList = new BulkObservableCollection<TextureFile>();
            GridFiles.ItemsSource = FilesList;
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            // клик по пустому месту окна убирает фокус из поля поиска — подсказка возвращается
            MouseDown += (_, _) => { if (TxtSearch.IsKeyboardFocused || TxtLogSearch.IsKeyboardFocused) Keyboard.ClearFocus(); };
            SourceInitialized += (_, _) => ApplyTitleBar(_settings.IsDarkTheme);

            _logTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
            _logTimer.Tick += (_, _) => FlushLog();
            _logTimer.Start();

            _logFilterTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
            _logFilterTimer.Tick += (_, _) => { _logFilterTimer?.Stop(); RebuildLogView(); };

            LoadSettings();
            ApplyColumnLayout(_settings.ColumnLayout);
            InitPackUi();

            InitHardwareAcceleration();
            ContentRendered += async (_, _) => { await Task.Delay(1500); TrimMemory(); };
            LogInfo(_settings.IsRussian ? "Готов к работе." : "Ready.");
            UpdateStatusCount();
        }

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);

        /// <summary>Сборка мусора и сброс неиспользуемых страниц из рабочего набора (в диспетчере задач цифра заметно падает).</summary>
        private static void TrimMemory()
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }

        private void InitHardwareAcceleration()
        {
            try
            {
                if (_settings.EnableGpu)
                {
                    OpenCL.IsEnabled = true;
                    var devices = OpenCL.Devices;
                    if (devices != null && devices.Any())
                    {
                        var primaryGpu = devices.FirstOrDefault(d =>
                            !string.IsNullOrEmpty(d.Name) && (
                                d.Name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                d.Name.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                d.Name.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                d.Name.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                d.Name.IndexOf("GeForce", StringComparison.OrdinalIgnoreCase) >= 0))
                            ?? devices.First();

                        LogInfo(_settings.IsRussian
                            ? $"OpenCL включён. Устройство: {primaryGpu.Name}"
                            : $"OpenCL enabled. Device: {primaryGpu.Name}");
                    }
                    else
                    {
                        LogInfo(_settings.IsRussian
                            ? "OpenCL включён."
                            : "OpenCL enabled.");
                    }
                }
                else
                {
                    OpenCL.IsEnabled = false;
                }
            }
            catch (Exception ex)
            {
                OpenCL.IsEnabled = false;
                LogError(_settings.IsRussian ? $"OpenCL не запустился: {ex.Message}. Работаю на процессоре." : $"OpenCL failed to start: {ex.Message}. Using the CPU instead.");
            }
        }

        // --- ЛОКАЛИЗАЦИЯ ИНТЕРФЕЙСА (РУССКИЙ / ENGLISH) ---
        public void ApplyLanguage(bool isRussian)
        {
            _settings.IsRussian = isRussian;
            bool ru = isRussian;

            TxtSettingsHeader.Text = ru ? "Конвертер текстур CoD" : "CoD texture converter";
            BtnOpenSettings.ToolTip = ru ? "Настройки программы" : "Program settings";

            TxtCodVerLabel.Text = ru ? "ВЕРСИЯ CALL OF DUTY" : "CALL OF DUTY VERSION";
            TxtOutFormatLabel.Text = ru ? "ВЫБОР ФОРМАТА" : "CHOOSE FORMAT";
            RbModeAuto.Content = ru ? "Авто" : "Auto";
            RbModeFixed.Content = ru ? "Один формат" : "Single format";
            TxtSaveDirLabel.Text = ru ? "КУДА СОХРАНЯТЬ" : "SAVE TO";
            RbSaveOriginal.Content = ru ? "Рядом" : "Nearby";
            RbSaveOriginal.ToolTip = ru
                ? "Рядом с исходным файлом. Для файлов из архива — в подпапку с именем архива (внутри сохраняется структура папок архива)."
                : "Next to the source file. For files from an archive: a subfolder named after the archive (the archive's folder structure is kept inside).";
            RbSaveCustom.Content = ru ? "Другая папка" : "Another folder";
            BtnOpenOutputDir.Content = ru ? "Открыть папку с результатом" : "Open output folder";

            BtnConvertAll.Content = ru ? "Конвертировать всё" : "Convert all";
            BtnConvertSelected.Content = ru ? "Конвертировать выбранные" : "Convert selected";
            BtnPause.Content = _isPaused ? (ru ? "Продолжить" : "Resume") : (ru ? "Пауза" : "Pause");
            BtnStop.Content = ru ? "Остановить" : "Stop";

            BtnAddFiles.Content = ru ? "Добавить файлы" : "Add files";
            BtnAddFolder.Content = ru ? "Добавить папку" : "Add folder";
            BtnRemoveSelected.Content = ru ? "Убрать выбранные" : "Remove selected";
            BtnClear.Content = ru ? "Очистить список" : "Clear list";
            UpdatePreviewTooltip();
            BtnAbout.ToolTip = ru ? "О программе и справка" : "About and help";
            BtnRefresh.ToolTip = ru ? "Обновить: найти новые файлы в добавленных папках" : "Refresh: look for new files in the added folders";
            TxtDoneLabel.Text = ru ? "ГОТОВЫЕ ФАЙЛЫ" : "FINISHED FILES";
            RbDoneAuto.Content = ru ? "Авто" : "Auto";
            RbDoneSkip.Content = ru ? "Пропускать" : "Skip";
            RbDoneRedo.Content = ru ? "Заново" : "Redo";
            RbDoneAuto.ToolTip = ru
                ? "Само решает: берёт новые файлы и те, для которых вы сменили настройки. Если всё уже готово — делает заново."
                : "Decides by itself: takes new files and those whose settings changed. If everything is done, redoes it all.";
            RbDoneSkip.ToolTip = ru ? "Готовые файлы не трогает." : "Leaves finished files alone.";
            RbDoneRedo.ToolTip = ru ? "Переделывает все файлы в списке." : "Redoes every file in the list.";
            TxtDropHint.Text = ru ? "Отпустите файлы или папки здесь" : "Drop files or folders here";
            TxtSearchLabel.Text = ru ? "Поиск по имени…" : "Search by name…";

            TxtLogsHeader.Text = ru ? "Журнал" : "Log";
            ChkClearLogsAfter.Content = ru ? "Автоочистка" : "Auto-clear";
            ChkClearLogsAfter.ToolTip = ru ? "Очищать журнал после каждой конвертации" : "Clear the log after every conversion";
            ChkErrorsOnly.Content = ru ? "Смотреть ошибки" : "Show errors";
            ChkErrorsOnly.ToolTip = ru ? "Показывать в журнале только ошибки" : "Show only errors in the log";
            TxtLogSearchHint.Text = ru ? "Поиск в журнале…" : "Search the log…";
            TxtElapsed.ToolTip = ru ? "Сколько идёт конвертация" : "How long the conversion has been running";
            BtnExportLogs.Content = ru ? "Сохранить…" : "Save…";
            BtnExportLogs.ToolTip = ru ? "Сохранить журнал в файл" : "Save the log to a file";
            BtnClearLogs.Content = ru ? "Очистить" : "Clear";

            ColFileName.Header = ru ? "Имя файла" : "Name";
            ColFileType.Header = ru ? "Тип" : "Type";
            ColFileSize.Header = ru ? "Размер" : "Size";
            ColFullPath.Header = ru ? "Путь" : "Path";
            ColStatus.Header = ru ? "Статус" : "Status";

            ApplyPackLanguage(ru);
            UpdateModeHint();
            RefreshStatusTexts();
            UpdateStatusCount();
        }

        public void ApplyTheme(bool isDark)
        {
            _settings.IsDarkTheme = isDark;
            if (isDark)
            {
                Resources["WindowBgBrush"] = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                Resources["PanelBgBrush"] = new SolidColorBrush(Color.FromRgb(37, 37, 38));
                Resources["InputBgBrush"] = new SolidColorBrush(Color.FromRgb(51, 51, 51));
                Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(42, 42, 45));
                Resources["TextBrush"] = new SolidColorBrush(Colors.White);
                Resources["TextMutedBrush"] = new SolidColorBrush(Color.FromRgb(170, 170, 170));
                Resources["BorderBrush"] = new SolidColorBrush(Color.FromRgb(62, 62, 66));
                Resources["RowAltBrush"] = new SolidColorBrush(Color.FromRgb(45, 45, 48));
                Resources["LogBgBrush"] = new SolidColorBrush(Color.FromRgb(20, 20, 20));

                Resources["ScrollTrackBrush"] = new SolidColorBrush(Colors.Transparent);
                Resources["ScrollThumbBrush"] = new SolidColorBrush(Color.FromRgb(85, 85, 90));
                Resources["ScrollThumbHoverBrush"] = new SolidColorBrush(Color.FromRgb(128, 128, 138));
                Resources["HoverOverlayBrush"] = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255));
            }
            else
            {
                Resources["WindowBgBrush"] = new SolidColorBrush(Color.FromRgb(240, 242, 245));
                Resources["PanelBgBrush"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                Resources["InputBgBrush"] = new SolidColorBrush(Color.FromRgb(230, 233, 238));
                Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(245, 246, 248));
                Resources["TextBrush"] = new SolidColorBrush(Color.FromRgb(28, 30, 33));
                Resources["TextMutedBrush"] = new SolidColorBrush(Color.FromRgb(85, 90, 100));
                Resources["BorderBrush"] = new SolidColorBrush(Color.FromRgb(205, 210, 218));
                Resources["RowAltBrush"] = new SolidColorBrush(Color.FromRgb(247, 248, 250));
                Resources["LogBgBrush"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));

                Resources["ScrollTrackBrush"] = new SolidColorBrush(Colors.Transparent);
                Resources["ScrollThumbBrush"] = new SolidColorBrush(Color.FromRgb(194, 197, 202));
                Resources["ScrollThumbHoverBrush"] = new SolidColorBrush(Color.FromRgb(159, 162, 168));
                Resources["HoverOverlayBrush"] = new SolidColorBrush(Color.FromArgb(0x16, 0, 0, 0));
            }
            // Те же кисти кладём в ресурсы приложения: окна настроек и «О программе» не видят ресурсы главного окна
            foreach (string key in new[] { "ScrollThumbBrush", "ScrollThumbHoverBrush", "AccentBrush" })
            {
                if (Resources[key] != null) Application.Current.Resources[key] = Resources[key];
            }

            ApplyTitleBar(isDark);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Тёмная/светлая рамка окна под тему (Windows 10 20H1+ и Windows 11).</summary>
        private void ApplyTitleBar(bool isDark)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int value = isDark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
            }
            catch { }
        }

        private void OutputMode_Changed(object? sender, RoutedEventArgs? e)
        {
            if (ChocolateTileAutoContainer == null || ChocolateTileFixedContainer == null) return;
            bool isAuto = RbModeAuto.IsChecked == true;
            ChocolateTileAutoContainer.Visibility = isAuto ? Visibility.Visible : Visibility.Collapsed;
            ChocolateTileFixedContainer.Visibility = isAuto ? Visibility.Collapsed : Visibility.Visible;
            UpdateModeHint();
        }

        private void UpdateModeHint()
        {
            if (TxtModeHint == null || RbModeAuto == null) return;
            bool ru = _settings.IsRussian;
            TxtModeHint.Text = RbModeAuto.IsChecked == true
                ? (ru ? "Каждый файл идёт в обратную сторону: IWI → выбранная картинка, PNG / TGA / DDS → IWI. В списке могут быть файлы разных форматов." : "Each file goes the opposite way: IWI → the chosen image format, PNG / TGA / DDS → IWI. The list can mix formats.")
                : (ru ? "Все файлы из списка сохраняются в выбранном формате." : "Every file in the list is saved in the chosen format.");
        }

        private void SaveLocation_Changed(object? sender, RoutedEventArgs? e)
        {
            if (PnlCustomPath == null) return;
            bool isCustom = RbSaveCustom.IsChecked == true;
            PnlCustomPath.IsEnabled = isCustom;
            PnlCustomPath.Opacity = isCustom ? 1.0 : 0.55;
        }

        private void BtnAbout_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AboutDialog(this, _settings.IsDarkTheme, _settings.IsRussian);
            dialog.ShowDialog();
        }

        private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SettingsDialog(this, _settings, isDark => ApplyTheme(isDark), isRu => ApplyLanguage(isRu));
            if (dialog.ShowDialog() == true)
            {
                InitHardwareAcceleration();
                if (_settings.ColumnLayout.Count == 0) ApplyColumnLayout(DefaultColumnLayout()); // нажали «Сбросить колонки»
                UpdatePreviewTooltip();
                SaveSettings();
                LogInfo(_settings.IsRussian ? "Настройки сохранены." : "Settings saved.");
            }
            RefreshPreviewWindow();
        }

        private void Window_Closing(object sender, CancelEventArgs e) => SaveSettings();

        private void SaveSettings()
        {
            try
            {
                int autoTileFormat = 0;
                if (RbTileTga.IsChecked == true) autoTileFormat = 1;
                else if (RbTileDds.IsChecked == true) autoTileFormat = 2;

                int fixedTileFormat = 0;
                if (RbFixedPng.IsChecked == true) fixedTileFormat = 1;
                else if (RbFixedTga.IsChecked == true) fixedTileFormat = 2;
                else if (RbFixedDds.IsChecked == true) fixedTileFormat = 3;

                SavePackSettings();
                _settings.CodVersionIndex = CmbCodVersion.SelectedIndex;
                _settings.IsAutoMode = RbModeAuto.IsChecked == true;
                _settings.AutoExtractFormat = autoTileFormat;
                _settings.FixedFormatIndex = fixedTileFormat;
                _settings.SaveToOriginal = RbSaveOriginal.IsChecked == true;
                _settings.CustomSavePath = TxtCustomPath.Text;
                _settings.ClearLogsAfter = ChkClearLogsAfter.IsChecked == true;
                _settings.DoneMode = RbDoneSkip.IsChecked == true ? 1 : (RbDoneRedo.IsChecked == true ? 2 : 0);
                _settings.ColumnLayout = CaptureColumnLayout();

                string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, json);
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                    if (loaded != null) _settings = loaded;
                }

                ApplyTheme(_settings.IsDarkTheme);
                ApplyLanguage(_settings.IsRussian);

                CmbCodVersion.SelectedIndex = _settings.CodVersionIndex;

                if (_settings.IsAutoMode) RbModeAuto.IsChecked = true; else RbModeFixed.IsChecked = true;
                OutputMode_Changed(null, null);

                if (_settings.AutoExtractFormat == 1) RbTileTga.IsChecked = true;
                else if (_settings.AutoExtractFormat == 2) RbTileDds.IsChecked = true;
                else RbTilePng.IsChecked = true;

                if (_settings.FixedFormatIndex == 1) RbFixedPng.IsChecked = true;
                else if (_settings.FixedFormatIndex == 2) RbFixedTga.IsChecked = true;
                else if (_settings.FixedFormatIndex == 3) RbFixedDds.IsChecked = true;
                else RbFixedIwi.IsChecked = true;

                if (_settings.SaveToOriginal) RbSaveOriginal.IsChecked = true; else RbSaveCustom.IsChecked = true;
                SaveLocation_Changed(null, null);

                TxtCustomPath.Text = _settings.CustomSavePath;
                ChkClearLogsAfter.IsChecked = _settings.ClearLogsAfter;
                RbDoneAuto.IsChecked = _settings.DoneMode == 0;
                RbDoneSkip.IsChecked = _settings.DoneMode == 1;
                RbDoneRedo.IsChecked = _settings.DoneMode == 2;
            }
            catch { }
        }

        // ----- Лог: потокобезопасная очередь + пакетный вывод в UI (раз в ~120 мс) -----
        private readonly record struct LogEntry(DateTime Time, string Message, Color Color, bool IsError);

        private readonly ConcurrentQueue<LogEntry> _logQueue = new();
        private readonly Stopwatch _convStopwatch = new();      // время конвертации (на паузе не тикает)
        private readonly List<LogEntry> _logHistory = new();   // весь журнал (для поиска, фильтра и сохранения)
        private const int MaxLogHistory = 20000;
        private DispatcherTimer? _logFilterTimer;
        private readonly DispatcherTimer _logTimer;
        private const int MaxLogBlocks = 3000;

        private void LogSuccess(string message) => AppendLog(message, Color.FromRgb(46, 125, 50));
        private void LogError(string message) => AppendLog(message, Color.FromRgb(211, 47, 47), true);
        private void LogInfo(string message) => AppendLog(message, Color.FromRgb(2, 119, 189));

        private void AppendLog(string message, Color color, bool isError = false)
        {
            _logQueue.Enqueue(new LogEntry(DateTime.Now, message, color, isError));
            if (Dispatcher.CheckAccess()) FlushLog(); // из UI-потока показываем сразу, из рабочих — по таймеру
        }

        private void FlushLog()
        {
            if (_logQueue.IsEmpty) return;

            var batch = new List<LogEntry>();
            while (_logQueue.TryDequeue(out var entry)) batch.Add(entry);

            _logHistory.AddRange(batch);
            if (_logHistory.Count > MaxLogHistory)
                _logHistory.RemoveRange(0, _logHistory.Count - MaxLogHistory);

            // в окне показываем только то, что проходит поиск и фильтр
            var visible = batch.Where(PassesLogFilter).ToList();
            if (visible.Count == 0) return;

            // Если за тик накопилось слишком много — выбрасываем самые старые «OK», ошибки оставляем
            if (visible.Count > MaxLogBlocks)
            {
                int excess = visible.Count - MaxLogBlocks;
                visible.RemoveAll(x => !x.IsError && excess-- > 0);
            }

            AppendLogBlocks(visible);
            RtbLog.ScrollToEnd();
        }

        private bool PassesLogFilter(LogEntry entry)
        {
            if (ChkErrorsOnly.IsChecked == true && !entry.IsError) return false;
            string query = TxtLogSearch.Text;
            return string.IsNullOrWhiteSpace(query) || entry.Message.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private void AppendLogBlocks(IEnumerable<LogEntry> entries)
        {
            var mutedBrush = Resources["TextMutedBrush"] as Brush ?? Brushes.Gray;
            var blocks = RtbLog.Document.Blocks;
            foreach (var le in entries)
            {
                var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                p.Inlines.Add(new Run($"[{le.Time:HH:mm:ss}] ") { Foreground = mutedBrush });
                var brush = new SolidColorBrush(le.Color);
                brush.Freeze();

                string[] lines = le.Message.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0) p.Inlines.Add(new LineBreak());
                    p.Inlines.Add(new Run(lines[i].TrimEnd('\r')) { Foreground = brush });
                }
                blocks.Add(p);
            }

            while (blocks.Count > MaxLogBlocks && blocks.FirstBlock != null)
                blocks.Remove(blocks.FirstBlock);
        }

        /// <summary>Перерисовывает журнал заново с учётом поиска и «Только ошибки».</summary>
        private void RebuildLogView()
        {
            FlushLog();
            RtbLog.Document.Blocks.Clear();
            var visible = _logHistory.Where(PassesLogFilter).ToList();
            if (visible.Count > MaxLogBlocks) visible = visible.GetRange(visible.Count - MaxLogBlocks, MaxLogBlocks);
            AppendLogBlocks(visible);
            RtbLog.ScrollToEnd();
        }

        private void ClearLog()
        {
            while (_logQueue.TryDequeue(out _)) { }
            _logHistory.Clear();
            RtbLog.Document.Blocks.Clear();
        }

        private void UpdateLogSearchHint()
        {
            if (TxtLogSearchHint == null || TxtLogSearch == null) return;
            TxtLogSearchHint.Visibility = string.IsNullOrEmpty(TxtLogSearch.Text) && !TxtLogSearch.IsKeyboardFocused
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void TxtLogSearch_FocusChanged(object sender, KeyboardFocusChangedEventArgs e) => UpdateLogSearchHint();

        private void TxtLogSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateLogSearchHint();
            _logFilterTimer?.Stop();   // небольшая задержка, чтобы не перерисовывать журнал на каждую букву
            _logFilterTimer?.Start();
        }

        private void ChkErrorsOnly_Click(object sender, RoutedEventArgs e) => RebuildLogView();

        /// <summary>Обрезаем содержимое карточки со списком по скруглённым углам, чтобы ничего не торчало квадратом.</summary>
        // ----- Расположение колонок: запоминаем ширину и порядок, чтобы таблицу можно было настроить под себя -----
        private Dictionary<string, DataGridColumn> ColumnMap() => new()
        {
            ["FileName"] = ColFileName,
            ["FileType"] = ColFileType,
            ["FileSize"] = ColFileSize,
            ["FullPath"] = ColFullPath,
            ["Status"] = ColStatus
        };

        private static List<ColumnState> DefaultColumnLayout() => new()
        {
            new ColumnState { Key = "FileName", Width = 3, Unit = "Star", Index = 0 },
            new ColumnState { Key = "FileType", Width = 0.8, Unit = "Star", Index = 1 },
            new ColumnState { Key = "FileSize", Width = 1.1, Unit = "Star", Index = 2 },
            new ColumnState { Key = "FullPath", Width = 4, Unit = "Star", Index = 3 },
            new ColumnState { Key = "Status", Width = 1.2, Unit = "Star", Index = 4 }
        };

        private List<ColumnState> CaptureColumnLayout()
        {
            var list = new List<ColumnState>();
            foreach (var pair in ColumnMap())
            {
                DataGridColumn col = pair.Value;
                bool star = col.Width.UnitType == DataGridLengthUnitType.Star && !double.IsNaN(col.Width.Value);
                double width = star ? col.Width.Value : col.ActualWidth;
                if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) continue;
                list.Add(new ColumnState { Key = pair.Key, Width = width, Unit = star ? "Star" : "Pixel", Index = col.DisplayIndex });
            }
            return list;
        }

        private void ApplyColumnLayout(List<ColumnState>? layout)
        {
            if (layout == null || layout.Count == 0) return;
            try
            {
                var map = ColumnMap();
                foreach (var state in layout)
                {
                    if (map.TryGetValue(state.Key, out var col) && state.Width > 0 && !double.IsNaN(state.Width) && !double.IsInfinity(state.Width))
                        col.Width = new DataGridLength(state.Width, state.Unit == "Star" ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel);
                }

                // порядок: идём от первой колонки к последней и ставим каждую на своё место
                foreach (var state in layout.OrderBy(s => s.Index))
                {
                    if (map.TryGetValue(state.Key, out var col))
                        col.DisplayIndex = Math.Clamp(state.Index, 0, map.Count - 1);
                }
            }
            catch { }
        }

        private void ListCard_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ListCard.Clip = new RectangleGeometry(new Rect(1, 1, Math.Max(0, e.NewSize.Width - 2), Math.Max(0, e.NewSize.Height - 2)), 11, 11);
        }

        private void BtnClearLogs_Click(object sender, RoutedEventArgs e) => ClearLog();

        private void BtnExportLogs_Click(object sender, RoutedEventArgs e)
        {
            FlushLog();
            if (_logHistory.Count == 0) return;

            var sfd = new SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt",
                FileName = $"Log_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt"
            };

            if (sfd.ShowDialog() == true)
            {
                File.WriteAllText(sfd.FileName, string.Join(Environment.NewLine, _logHistory.Select(x => $"[{x.Time:HH:mm:ss}] {x.Message}")));
                LogSuccess(_settings.IsRussian ? $"Журнал сохранён: {Path.GetFileName(sfd.FileName)}" : $"Log saved: {Path.GetFileName(sfd.FileName)}");
            }
        }

        private void LogDebug(string message) => AppendLog(message, Color.FromRgb(138, 143, 154));

        private static string FormatElapsed(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

        /// <summary>Режим Debug: что за файл (размер, кодек, заголовок, первые байты).</summary>
        private static string DescribeInput(string path, bool ru)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var head = new byte[(int)Math.Min(64, fs.Length)];
                fs.ReadExactly(head, 0, head.Length);
                string size = fs.Length.ToString("N0") + (ru ? " байт" : " bytes");
                return $"[DEBUG] {Path.GetFileName(path)} · {size}\n" + IwiCodec.DescribeHead(head, fs.Length, Path.GetExtension(path), ru);
            }
            catch (Exception ex)
            {
                return $"[DEBUG] {Path.GetFileName(path)}: {(ru ? "не удалось прочитать файл" : "could not read the file")} ({ex.Message})";
            }
        }

        private static string ErrorDetails(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            int depth = 0;
            for (Exception? e = ex; e != null && depth < 4; e = e.InnerException, depth++)
            {
                sb.Append("    ").Append(e.GetType().FullName).Append(": ").Append(e.Message).Append('\n');
                foreach (string line in (e.StackTrace ?? "").Split('\n').Take(6))
                    sb.Append("      ").Append(line.Trim()).Append('\n');
            }
            return sb.ToString().TrimEnd();
        }

        private void BtnOpenOutputDir_Click(object sender, RoutedEventArgs e)
        {
            string folderToOpen = "";
            var selected = GridFiles.SelectedItem as TextureFile ?? FilesList.FirstOrDefault();

            if (RbSaveCustom.IsChecked == true && !string.IsNullOrWhiteSpace(TxtCustomPath.Text) && Directory.Exists(TxtCustomPath.Text))
            {
                folderToOpen = TxtCustomPath.Text;
                // для файла из архива результат лежит в подпапке с именем архива
                if (selected is { IsArchiveEntry: true })
                {
                    string sub = Path.Combine(folderToOpen, ArchiveSupport.OutputFolderName(selected.ArchivePath!));
                    if (Directory.Exists(sub)) folderToOpen = sub;
                }
            }
            else if (selected != null && !string.IsNullOrWhiteSpace(selected.FullPath))
            {
                if (selected.IsArchiveEntry)
                {
                    folderToOpen = Path.GetDirectoryName(selected.ArchivePath!) ?? "";
                    string sub = Path.Combine(folderToOpen, ArchiveSupport.OutputFolderName(selected.ArchivePath!));
                    if (Directory.Exists(sub)) folderToOpen = sub;
                }
                else
                {
                    folderToOpen = Path.GetDirectoryName(selected.FullPath) ?? "";
                }
            }

            if (!string.IsNullOrEmpty(folderToOpen) && Directory.Exists(folderToOpen))
            {
                Process.Start(new ProcessStartInfo { FileName = folderToOpen, UseShellExecute = true });
            }
            else
            {
                LogError(_settings.IsRussian ? "Папка с результатом не найдена." : "Output folder not found.");
            }
        }

        internal static readonly HashSet<string> SupportedExts = new(StringComparer.OrdinalIgnoreCase) { ".png", ".tga", ".dds", ".iwi", ".vtf" };
        // то, что можно добавить в список: текстуры и архивы с текстурами (ZIP, 7Z, RAR, IWD)
        internal static readonly HashSet<string> InputExts = new(SupportedExts.Concat(ArchiveSupport.ArchiveExts), StringComparer.OrdinalIgnoreCase);

        private async void BtnAddFiles_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Textures and archives|*.png;*.tga;*.dds;*.iwi;*.vtf;*.zip;*.7z;*.rar;*.iwd" +
                         "|Textures (*.png;*.tga;*.dds;*.iwi;*.vtf)|*.png;*.tga;*.dds;*.iwi;*.vtf" +
                         "|Archives (*.zip;*.7z;*.rar;*.iwd)|*.zip;*.7z;*.rar;*.iwd" +
                         "|All files (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                int added = (await AddPathsAsync(dlg.FileNames)).Count;
                LogInfo(_settings.IsRussian ? $"Добавлено: {added}" : $"Added: {added}");
                UpdateStatusCount();
            }
        }

        private async void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderDialog dlg = new OpenFolderDialog();
            if (dlg.ShowDialog() == true)
            {
                string folder = dlg.FolderName;
                _sourceFolders.Add(folder);
                LogInfo(_settings.IsRussian ? "Ищу файлы в папке…" : "Looking for files…");
                List<string> found = await Task.Run(() => ScanFolder(folder));
                int added = (await AddPathsAsync(found)).Count;
                LogInfo(_settings.IsRussian ? $"Из папки добавлено: {added}" : $"Added from folder: {added}");
                UpdateStatusCount();
            }
        }

        internal static List<string> ScanFolder(string folder)
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            var result = new List<string>();
            foreach (string path in Directory.EnumerateFiles(folder, "*", options))
            {
                if (InputExts.Contains(Path.GetExtension(path))) result.Add(path);
            }
            return result;
        }

        /// <summary>Добавляет файлы одной пачкой (одно уведомление UI), без O(n²)-проверки дублей.</summary>
        private async Task<List<TextureFile>> AddPathsAsync(IEnumerable<string> paths)
        {
            bool ru = _settings.IsRussian;
            var fresh = new List<string>();     // обычные файлы
            var archives = new List<string>();  // архивы: заглядываем внутрь и добавляем найденные текстуры
            var seenArchives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in paths)
            {
                try
                {
                    string full = Path.GetFullPath(p);
                    if (ArchiveSupport.IsArchive(full))
                    {
                        if (seenArchives.Add(full)) archives.Add(full);
                    }
                    else if (_knownPaths.Add(full)) fresh.Add(full);
                }
                catch { }
            }
            if (fresh.Count == 0 && archives.Count == 0) return new List<TextureFile>();

            string pendingText = Localize(ConvState.Pending);
            var scanned = await Task.Run(() =>
            {
                var list = new List<TextureFile>(fresh.Count);
                foreach (string path in fresh)
                {
                    try
                    {
                        FileInfo info = new FileInfo(path);
                        list.Add(new TextureFile
                        {
                            FileName = info.Name,
                            FullPath = path,
                            FileType = info.Extension.TrimStart('.').ToUpperInvariant(),
                            FileSizeBytes = info.Length,
                            FileSize = FormatSize(info.Length),
                            Status = pendingText
                        });
                    }
                    catch { }
                }

                // читаем только оглавление архива — сами файлы пока не распаковываем
                var fromArchives = new List<TextureFile>();
                foreach (string archive in archives)
                {
                    string archiveName = Path.GetFileName(archive);
                    try
                    {
                        var entries = ArchiveSupport.ListFiles(archive, SupportedExts, out string format);
                        if (entries.Count == 0)
                        {
                            LogInfo(ru ? $"[АРХИВ] {archiveName} ({format}): подходящих текстур внутри нет." : $"[ARCHIVE] {archiveName} ({format}): no matching textures inside.");
                            continue;
                        }
                        foreach (var (key, size) in entries)
                        {
                            fromArchives.Add(new TextureFile
                            {
                                FileName = Path.GetFileName(key),
                                FullPath = ArchiveSupport.MakeVirtualPath(archive, key),
                                ArchivePath = archive,
                                EntryPath = key,
                                FileType = Path.GetExtension(key).TrimStart('.').ToUpperInvariant(),
                                FileSizeBytes = size,
                                FileSize = FormatSize(size),
                                Status = pendingText
                            });
                        }
                        LogInfo(ru ? $"[АРХИВ] {archiveName} ({format}): найдено текстур — {entries.Count}" : $"[ARCHIVE] {archiveName} ({format}): textures found — {entries.Count}");
                    }
                    catch (Exception ex)
                    {
                        LogError(ru ? $"[АРХИВ] {archiveName}: не удалось открыть ({ex.Message})" : $"[ARCHIVE] {archiveName}: could not open ({ex.Message})");
                    }
                }
                return (Plain: list, FromArchives: fromArchives);
            });

            // дубли отсеиваем уже здесь, в потоке интерфейса: набор _knownPaths не потокобезопасный
            var items = scanned.Plain;
            int plainCount = items.Count;
            items.AddRange(scanned.FromArchives.Where(f => _knownPaths.Add(f.FullPath)));

            // если часть найденного не попала в список (например, эти файлы уже были добавлены раньше) — говорим об этом
            int newFromArchives = items.Count - plainCount;
            if (newFromArchives != scanned.FromArchives.Count)
                LogInfo(ru
                    ? $"[АРХИВ] В списке уже были {scanned.FromArchives.Count - newFromArchives} из {scanned.FromArchives.Count} найденных файлов, добавлено новых: {newFromArchives}."
                    : $"[ARCHIVE] {scanned.FromArchives.Count - newFromArchives} of {scanned.FromArchives.Count} found files were already in the list, new ones added: {newFromArchives}.");

            FilesList.AddRange(items);
            return items;
        }

        private string FormatSize(long bytes)
        {
            string[] suf = _settings.IsRussian ? new[] { "Б", "КБ", "МБ", "ГБ" } : new[] { "B", "KB", "MB", "GB" };
            if (bytes <= 0) return $"0 {suf[0]}";
            int place = Math.Min((int)Math.Floor(Math.Log(bytes, 1024)), suf.Length - 1);
            double num = Math.Round(bytes / Math.Pow(1024, place), 1);
            return $"{num} {suf[place]}";
        }

        private void GridFiles_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete || Keyboard.Modifiers != ModifierKeys.None) return;
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
            if (GridFiles.SelectedItems.Count == 0) return;

            e.Handled = true;
            BtnRemoveSelected_Click(sender, e);
        }

        private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = GridFiles.SelectedItems.Cast<TextureFile>().ToList();
            FilesList.RemoveRange(selected);
            foreach (var item in selected) _knownPaths.Remove(item.FullPath);
            LogInfo(_settings.IsRussian ? $"Убрано из списка: {selected.Count}" : $"Removed from list: {selected.Count}");
            UpdateStatusCount();
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            FilesList.Clear();
            _knownPaths.Clear();
            _sourceFolders.Clear();
            LogInfo(_settings.IsRussian ? "Список очищен." : "List cleared.");
            UpdateProgress(0, 0);
            UpdateStatusCount();
        }

        // ----- Обновление списка -----
        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            bool ru = _settings.IsRussian;
            var folders = _sourceFolders.ToList();
            var known = FilesList.Select(f => f.FullPath).ToList();
            var knownArchives = FilesList.Where(f => f.IsArchiveEntry).Select(f => f.ArchivePath!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (known.Count == 0 && folders.Count == 0)
            {
                LogInfo(ru ? "Обновлять нечего: список пуст." : "Nothing to refresh: the list is empty.");
                return;
            }

            BtnRefresh.IsEnabled = false;
            try
            {
                // тяжёлую работу с диском делаем в фоне
                var result = await Task.Run(() =>
                {
                    // для файла из архива проверяем, что сам архив на месте
                    var missing = new HashSet<string>(known.Where(p => !File.Exists(ArchiveSupport.RealPath(p))), StringComparer.OrdinalIgnoreCase);
                    var fresh = new List<string>();
                    foreach (string folder in folders)
                    {
                        if (Directory.Exists(folder)) fresh.AddRange(ScanFolder(folder));
                    }
                    // архивы, добавленные по одному: заглядываем внутрь ещё раз — вдруг они изменились
                    fresh.AddRange(knownArchives.Where(File.Exists));
                    return (Missing: missing, Fresh: fresh);
                });

                int removed = 0;
                if (result.Missing.Count > 0)
                {
                    var gone = FilesList.Where(f => result.Missing.Contains(f.FullPath)).ToList();
                    FilesList.RemoveRange(gone);
                    foreach (var f in gone) _knownPaths.Remove(f.FullPath);
                    removed = gone.Count;
                }

                var added = await AddPathsAsync(result.Fresh);

                LogInfo(ru
                    ? $"Список обновлён. Новых файлов: {added.Count}, исчезло с диска: {removed}."
                    : $"List refreshed. New files: {added.Count}, gone from disk: {removed}.");
                UpdateStatusCount();
            }
            finally
            {
                BtnRefresh.IsEnabled = true;
            }
        }

        // ----- Перетаскивание файлов и папок из проводника -----
        private static bool HasDroppedFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

        private void Window_PreviewDragEnter(object sender, DragEventArgs e) => Window_PreviewDragOver(sender, e);

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (HasDroppedFiles(e))
            {
                e.Effects = DragDropEffects.Copy;
                ShowDropOverlay();
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_PreviewDragLeave(object sender, DragEventArgs e)
        {
            // подсказку прячем только когда курсор реально вышел за окно (а не перешёл на другой элемент)
            Point p = e.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight)
                HideDropOverlay();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        private DispatcherTimer? _dropGuard;

        private void ShowDropOverlay()
        {
            DropOverlay.Visibility = Visibility.Visible;
            if (_dropGuard == null)
            {
                // Страховка: если перетаскивание отменили (Esc) или курсор ушёл за окно, а событие «покинул» не пришло —
                // сами проверяем состояние мыши и прячем подсказку
                _dropGuard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                _dropGuard.Tick += (_, _) => { if (!IsDragStillOverWindow()) HideDropOverlay(); };
            }
            _dropGuard.Start();
        }

        private void HideDropOverlay()
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            _dropGuard?.Stop();
        }

        private bool IsDragStillOverWindow()
        {
            bool buttonDown = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (!buttonDown) return false;
            if (!GetCursorPos(out NativePoint cursor)) return true;

            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            Point origin = PointToScreen(new Point(0, 0));
            double width = ActualWidth * dpi.DpiScaleX, height = ActualHeight * dpi.DpiScaleY;
            return cursor.X >= origin.X && cursor.Y >= origin.Y && cursor.X <= origin.X + width && cursor.Y <= origin.Y + height;
        }

        private async void Window_PreviewDrop(object sender, DragEventArgs e)
        {
            HideDropOverlay();
            if (!HasDroppedFiles(e)) return;
            e.Handled = true;

            if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0) return;
            bool ru = _settings.IsRussian;

            var scan = await Task.Run(() =>
            {
                var files = new List<string>();
                var dirs = new List<string>();
                foreach (string path in dropped)
                {
                    try
                    {
                        if (Directory.Exists(path))
                        {
                            dirs.Add(path);
                            files.AddRange(ScanFolder(path));
                        }
                        else if (File.Exists(path) && InputExts.Contains(Path.GetExtension(path)))
                        {
                            files.Add(path);
                        }
                    }
                    catch { }
                }
                return (Files: files, Dirs: dirs);
            });

            foreach (string dir in scan.Dirs) _sourceFolders.Add(dir);

            var added = await AddPathsAsync(scan.Files);
            LogInfo(added.Count > 0
                ? (ru ? $"Перетащено: {added.Count}" : $"Dropped: {added.Count}")
                : (ru ? "Новых подходящих файлов нет (нужны PNG, TGA, DDS, IWI, VTF или архивы ZIP, 7Z, RAR, IWD)." : "No new matching files (PNG, TGA, DDS, IWI, VTF or ZIP, 7Z, RAR, IWD archives are needed)."));
            UpdateStatusCount();

            // «Быстрый режим»: сразу конвертируем только что добавленное
            if (added.Count > 0 && _settings.AutoConvertOnDrop && !_archiveMode && BtnConvertAll.IsEnabled)
                await StartConversion(added, 0);
        }

        // Статусы храним как enum, а текст берём по языку — смена языка больше не ломает логику "уже готово"
        private string Localize(ConvState s) => (s, _settings.IsRussian) switch
        {
            (ConvState.Pending, true) => "В очереди",
            (ConvState.Pending, false) => "Queued",
            (ConvState.Done, true) => "Готово",
            (ConvState.Done, false) => "Done",
            (ConvState.Error, true) => "Ошибка",
            _ => "Error"
        };

        private void RefreshStatusTexts()
        {
            if (FilesList == null) return;
            foreach (var f in FilesList)
                f.Status = f.HasReplace
                    ? (_settings.IsRussian ? "Заменится" : "Will be replaced")
                    : Localize(f.State);
        }

        /// <summary>Подсказка в поиске видна, только когда поле пустое и в него не кликнули.</summary>
        private void UpdateSearchHint()
        {
            if (TxtSearchLabel == null || TxtSearch == null) return;
            TxtSearchLabel.Visibility = string.IsNullOrEmpty(TxtSearch.Text) && !TxtSearch.IsKeyboardFocused
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void TxtSearch_FocusChanged(object sender, KeyboardFocusChangedEventArgs e) => UpdateSearchHint();

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSearchHint();
            ICollectionView view = CollectionViewSource.GetDefaultView(FilesList);
            view.Filter = item =>
            {
                if (string.IsNullOrWhiteSpace(TxtSearch.Text)) return true;
                if (item is TextureFile tf)
                {
                    return tf.FileName.IndexOf(TxtSearch.Text, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                return false;
            };
            UpdateStatusCount();
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e) => TxtSearch.Text = "";
        private void GridFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateStatusCount();
            UpdatePreview();
        }

        // ----- Превью текстуры (кнопка 👁 или клавиша P) -----
        private PreviewWindow? _preview;

        private void BtnPreview_Click(object sender, RoutedEventArgs e) => TogglePreview();

        private string EffectivePreviewBinding => string.IsNullOrEmpty(_settings.PreviewHotkeyBinding)
            ? HotkeyBinding.KeyToken(_settings.PreviewHotkey)
            : _settings.PreviewHotkeyBinding;

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            // C (или что назначено) — окно замены для выбранного файла из архива
            if (Keyboard.Modifiers == ModifierKeys.None
                && HotkeyBinding.MatchesKey(_settings.ReplaceHotkey, key)
                && GridFiles.SelectedItem is TextureFile replaceTarget
                && replaceTarget.IsArchiveEntry)
            {
                e.Handled = true;
                OpenReplaceDialog(replaceTarget);
                return;
            }

            // P (или что назначено) — открыть/закрыть предпросмотр
            if (Keyboard.Modifiers == ModifierKeys.None && HotkeyBinding.MatchesKey(EffectivePreviewBinding, key))
            {
                e.Handled = true;
                TogglePreview();
            }
        }

        private void TogglePreview()
        {
            if (_preview != null)
            {
                _preview.Close(); // Closed-обработчик обнулит _preview
                return;
            }

            var window = new PreviewWindow(this, _settings.IsDarkTheme, _settings.IsRussian, EffectivePreviewBinding);
            // клик, которым окно открыли, не должен сразу его закрыть
            window.IgnoreMouseUntil = DateTime.UtcNow.AddMilliseconds(350);
            window.ReplaceHotkey = _settings.ReplaceHotkey;
            window.ReplaceHotkeyEnabled = _settings.ReplaceHotkeyEnabled;
            window.PreviewHotkeyEnabled = _settings.PreviewHotkeyEnabled;
            window.ReplaceRequested = PreviewReplaceRequested;
            window.Closed += (s, _) => { if (ReferenceEquals(_preview, s)) _preview = null; TrimMemory(); };
            _preview = window;
            window.Show();
            UpdatePreview();
        }

        private void UpdatePreviewTooltip()
            => BtnPreview.ToolTip = _settings.IsRussian
                ? $"Предпросмотр ({HotkeyBinding.Display(EffectivePreviewBinding, true)})"
                : $"Preview ({HotkeyBinding.Display(EffectivePreviewBinding, false)})";

        private void UpdatePreview() => _preview?.ShowFile((GridFiles.SelectedItem as TextureFile)?.FullPath);

        /// <summary>Из превью попросили замену файла — находим эту запись в списке и открываем диалог.</summary>
        private void PreviewReplaceRequested(string path)
        {
            var file = FilesList.FirstOrDefault(f => string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase));
            if (file == null || !file.IsArchiveEntry) return;
            OpenReplaceDialog(file);
        }

        /// <summary>Файл из строки под курсором (для двойного клика и мышиных горячих кнопок).</summary>
        private TextureFile? FileUnderMouse(DependencyObject? source)
        {
            var row = ItemsControl.ContainerFromElement(GridFiles, source) as DataGridRow;
            return row?.Item as TextureFile ?? GridFiles.SelectedItem as TextureFile;
        }

        /// <summary>Горячая кнопка мыши (ПКМ/СКМ/боковые): замена файла в архиве.</summary>
        private void GridFiles_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) return; // ЛКМ — только двойным кликом (см. ниже)

            var row = ItemsControl.ContainerFromElement(GridFiles, e.OriginalSource as DependencyObject) as DataGridRow;
            var file = row?.Item as TextureFile;

            // замена файла в архиве (ПКМ/СКМ/боковые) — галочка влияет только на ЛКМ
            if (HotkeyBinding.MatchesMouse(_settings.ReplaceHotkey, e.ChangedButton)
                && file != null && file.IsArchiveEntry)
            {
                e.Handled = true;
                OpenReplaceDialog(file);
                return;
            }

            // предпросмотр (ПКМ/СКМ/боковые)
            if (HotkeyBinding.MatchesMouse(EffectivePreviewBinding, e.ChangedButton))
            {
                e.Handled = true;
                TogglePreview();
            }
        }

        /// <summary>Колесо мыши как горячая кнопка замены или предпросмотра.</summary>
        private void GridFiles_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (HotkeyBinding.MatchesWheel(_settings.ReplaceHotkey, e.Delta)
                && GridFiles.SelectedItem is TextureFile replaceFile && replaceFile.IsArchiveEntry)
            {
                e.Handled = true;
                OpenReplaceDialog(replaceFile);
                return;
            }

            if (HotkeyBinding.MatchesWheel(EffectivePreviewBinding, e.Delta))
            {
                e.Handled = true;
                TogglePreview();
            }
        }

        /// <summary>Двойной клик ЛКМ: включает та галочка, у которой стоит «открывать двойным кликом для ЛКМ».</summary>
        private void GridFiles_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2) return;

            var row = ItemsControl.ContainerFromElement(GridFiles, e.OriginalSource as DependencyObject) as DataGridRow;
            var file = row?.Item as TextureFile ?? GridFiles.SelectedItem as TextureFile;
            if (file == null) return;

            if (_settings.PreviewHotkeyEnabled)
            {
                e.Handled = true;
                // двойной клик только открывает предпросмотр, но не закрывает уже открытое окно
                if (_preview == null) TogglePreview();
                return;
            }

            if (_settings.ReplaceHotkeyEnabled && file.IsArchiveEntry)
            {
                e.Handled = true;
                OpenReplaceDialog(file);
            }
        }

        private bool _replaceDialogOpen;

        private void OpenReplaceDialog(TextureFile file)
        {
            if (_replaceDialogOpen) return;
            _replaceDialogOpen = true;
            try
            {
                bool ru = _settings.IsRussian;
                var dlg = new ReplaceEntryWindow(this, ru, file.EntryPath ?? file.FileName, file.FullPath, file.ReplacePath ?? "");
                if (dlg.ShowDialog() != true) return;

                file.ReplacePath = dlg.ReplacementPath.Length > 0 ? dlg.ReplacementPath : null;
                file.Status = file.HasReplace
                    ? (ru ? "Заменится" : "Will be replaced")
                    : Localize(file.State);
            }
            finally
            {
                _replaceDialogOpen = false;
            }
        }

        /// <summary>После смены темы/языка пересоздаём окно превью, чтобы оно подхватило новые цвета и тексты.</summary>
        private void RefreshPreviewWindow()
        {
            var old = _preview;
            if (old == null) return;
            _preview = null;
            old.Close();
            TogglePreview();
        }

        private void UpdateStatusCount()
        {
            TxtStatusCount.Text = _settings.IsRussian
                ? $"Файлов: {FilesList.Count} · выбрано: {GridFiles.SelectedItems.Count}"
                : $"Files: {FilesList.Count} · selected: {GridFiles.SelectedItems.Count}";
        }

        private void BtnBrowseSavePath_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderDialog dlg = new OpenFolderDialog();
            if (dlg.ShowDialog() == true)
            {
                TxtCustomPath.Text = dlg.FolderName;
                RbSaveCustom.IsChecked = true;
                SaveLocation_Changed(null, null);
            }
        }

        // --- ЛОГИКА КНОПКИ ПАУЗА / ПРОДОЛЖИТЬ ---
        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            if (!_isPaused)
            {
                _isPaused = true;
                _pauseEvent.Reset(); // Останавливает потоки
                _convStopwatch.Stop();
                BtnPause.Content = _settings.IsRussian ? "Продолжить" : "Resume";
                BtnPause.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Зелёный
                LogInfo(_settings.IsRussian ? "Пауза." : "Paused.");
            }
            else
            {
                _isPaused = false;
                _pauseEvent.Set(); // Возобновляет потоки
                _convStopwatch.Start();
                BtnPause.Content = _settings.IsRussian ? "Пауза" : "Pause";
                BtnPause.Background = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Оранжевый
                LogInfo(_settings.IsRussian ? "Продолжаю." : "Resumed.");
            }
        }

        private async void BtnConvertAll_Click(object sender, RoutedEventArgs e) => await StartConversion(FilesList.ToList(), CurrentDoneMode());
        // Выбранные файлы конвертируются всегда — в том числе заново, даже если они уже были готовы
        private async void BtnConvertSelected_Click(object sender, RoutedEventArgs e) => await StartConversion(GridFiles.SelectedItems.Cast<TextureFile>().ToList(), 2);

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            _pauseEvent.Set(); // Размораживаем потоки, чтобы они моментально завершились
            LogInfo(_settings.IsRussian ? "Останавливаю…" : "Stopping…");
            BtnStop.IsEnabled = false;
            BtnPause.IsEnabled = false;
        }

        private string CurrentSignature()
        {
            string auto = RbTileTga.IsChecked == true ? "tga" : RbTileDds.IsChecked == true ? "dds" : "png";
            string fixedExt = RbFixedPng.IsChecked == true ? "png" : RbFixedTga.IsChecked == true ? "tga" : RbFixedDds.IsChecked == true ? "dds" : "iwi";
            return string.Join("|",
                CmbCodVersion.SelectedIndex, RbModeAuto.IsChecked == true, auto, fixedExt,
                RbSaveOriginal.IsChecked == true, TxtCustomPath.Text,
                _settings.FastDxt, _settings.IwiMipmaps, _settings.IwiFormatMode, _settings.DdsMipmaps, _settings.DdsFormat, _settings.TgaRle, _settings.PngLevel,
                _settings.TgaDepth, _settings.PngDepth, _settings.NoMipsForSmall);
        }

        /// <summary>0 = Авто, 1 = Пропускать готовые, 2 = Делать заново</summary>
        private int CurrentDoneMode() => RbDoneSkip.IsChecked == true ? 1 : (RbDoneRedo.IsChecked == true ? 2 : 0);

        private async Task StartConversion(List<TextureFile> filesToConvert, int doneMode)
        {
            // «Подпись» текущих настроек: если вы поменяли формат, версию игры или сжатие,
            // готовые файлы считаются устаревшими и конвертируются заново — загружать их повторно не нужно
            string signature = CurrentSignature();
            bool ruMsg = _settings.IsRussian;

            List<TextureFile> pendingFiles = doneMode switch
            {
                2 => filesToConvert.ToList(),                                            // заново: все
                1 => filesToConvert.Where(f => f.State != ConvState.Done).ToList(),      // пропускать любые готовые
                _ => filesToConvert.Where(f => f.State != ConvState.Done || f.ConvertedSignature != signature).ToList() // авто
            };

            if (pendingFiles.Count == 0 && filesToConvert.Count > 0)
            {
                if (doneMode == 1)
                {
                    LogInfo(ruMsg
                        ? "Все файлы уже готовы. Переключите «Готовые файлы» на «Авто» или «Заново»."
                        : "Every file is already done. Switch \"Finished files\" to \"Auto\" or \"Redo\".");
                    return;
                }
                // Авто: всё готово и настройки те же — значит, нужно сделать заново (например, после правки исходников)
                pendingFiles = filesToConvert.ToList();
                LogInfo(ruMsg ? "Все файлы уже готовы — конвертирую заново." : "Every file is already done — converting again.");
            }

            if (pendingFiles.Count == 0)
            {
                LogInfo(ruMsg ? "Нечего конвертировать: выберите файлы или добавьте их в список." : "Nothing to convert: select files or add them to the list.");
                return;
            }

            // при повторной конвертации сбрасываем прежний статус
            foreach (var f in pendingFiles)
                if (f.State != ConvState.Pending) f.SetState(ConvState.Pending, Localize(ConvState.Pending));

            BtnConvertAll.IsEnabled = false;
            BtnConvertSelected.IsEnabled = false;
            BtnPackAll.IsEnabled = false;
            BtnPackSelected.IsEnabled = false;
            BtnStop.IsEnabled = true;

            // Показываем кнопку Пауза
            _isPaused = false;
            _pauseEvent.Set();
            BtnPause.Visibility = Visibility.Visible;
            BtnPause.IsEnabled = true;
            BtnPause.Content = _settings.IsRussian ? "Пауза" : "Pause";
            BtnPause.Background = new SolidColorBrush(Color.FromRgb(255, 152, 0));

            int total = pendingFiles.Count;
            UpdateProgress(0, total);

            if (AppTaskbarInfo != null)
            {
                AppTaskbarInfo.ProgressState = TaskbarItemProgressState.Normal;
                AppTaskbarInfo.ProgressValue = 0;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            int totalCores = Environment.ProcessorCount;
            int maxDegreeOfParallelism = _settings.PerformanceMode switch
            {
                0 => Math.Max(1, (int)Math.Round(totalCores * 0.80)),
                1 => Math.Max(1, (int)Math.Round(totalCores * 0.40)),
                _ => Math.Max(1, (int)Math.Round(totalCores * 0.15))
            };

            // ImageMagick сам распараллеливает пиксельные операции (OpenMP). Мы уже параллелим по файлам,
            // поэтому даём каждому файлу по одному потоку — иначе потоки мешают друг другу.
            try { ResourceLimits.Thread = 1; } catch { }

            int codVerIndex = CmbCodVersion.SelectedIndex;
            if (codVerIndex < 0) codVerIndex = 4; // WaW по умолчанию (раньше -1 приводил к вылету)
            bool isAutoMode = RbModeAuto.IsChecked == true;

            string autoExtractExt = ".png";
            if (RbTileTga.IsChecked == true) autoExtractExt = ".tga";
            else if (RbTileDds.IsChecked == true) autoExtractExt = ".dds";

            string fixedTargetExt = ".iwi";
            if (RbFixedPng.IsChecked == true) fixedTargetExt = ".png";
            else if (RbFixedTga.IsChecked == true) fixedTargetExt = ".tga";
            else if (RbFixedDds.IsChecked == true) fixedTargetExt = ".dds";

            bool saveOriginal = RbSaveOriginal.IsChecked == true;
            string customPath = TxtCustomPath.Text;

            // файлы из архивов распаковываются во временную папку (по одному разу на архив) и удаляются сразу после конвертации
            var archiveExtractor = new ArchiveExtractor(pendingFiles.Where(f => f.IsArchiveEntry), token);

            var job = new ConvertJob(
                codVerIndex, isAutoMode, autoExtractExt, fixedTargetExt, saveOriginal, customPath,
                _settings.FastDxt, _settings.FastNvme,
                _settings.IwiMipmaps, _settings.IwiFormatMode, _settings.DdsMipmaps, _settings.DdsFormat, _settings.TgaRle, _settings.PngLevel,
                _settings.TgaDepth, _settings.PngDepth, _settings.NoMipsForSmall,
                new HashSet<string>(pendingFiles.Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase),
                new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                archiveExtractor);

            bool isRu = _settings.IsRussian;
            string txtDone = Localize(ConvState.Done);
            string txtErr = Localize(ConvState.Error);
            string errTag = isRu ? "ОШИБКА" : "ERROR";

            if (job.IwiMipmaps && !IwiCodec.SupportsMipChain(codVerIndex) && (isAutoMode || fixedTargetExt == ".iwi"))
                LogInfo(isRu
                    ? "Для выбранной версии игры мипмапы в IWI пока не создаются: записывается один уровень (мипмапы поддержаны для CoD2, CoD4, World at War и Black Ops)."
                    : "Mipmaps in IWI are not created for the selected game yet: a single level is written (mipmaps are supported for CoD2, CoD4, World at War and Black Ops).");

            // Крупные файлы первыми — потоки заканчивают примерно одновременно
            var ordered = pendingFiles.OrderByDescending(f => f.FileSizeBytes).ToList();

            if (archiveExtractor.HasArchives)
                LogInfo(isRu
                    ? "Есть файлы из архивов: нужные текстуры сначала распаковываются во временную папку, результат ляжет в подпапку с именем архива."
                    : "Some files come from archives: the needed textures are unpacked to a temporary folder first, and the result goes to a subfolder named after the archive.");

            int completed = 0;
            int successCount = 0;
            int failCount = 0;

            _convStopwatch.Restart();
            TxtElapsed.Text = "00:00";
            bool debug = _settings.DebugMode;

            // Прогресс обновляем по таймеру, а не из каждого потока через Dispatcher.Invoke
            var uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            uiTimer.Tick += (_, _) =>
            {
                PushProgress(Volatile.Read(ref completed), total);
                TxtElapsed.Text = FormatElapsed(_convStopwatch.Elapsed);
            };
            uiTimer.Start();

            if (debug)
            {
                string modeText = isAutoMode ? (isRu ? "авто" : "auto") : (isRu ? "один формат" : "single format");
                LogDebug(isRu
                    ? $"[DEBUG] Старт: файлов {total}, потоков {maxDegreeOfParallelism}, режим: {modeText}, версия CoD: {codVerIndex} (IWI v{IwiCodec.VersionByIndex[Math.Clamp(codVerIndex, 0, IwiCodec.VersionByIndex.Length - 1)]}), мипмапы IWI: {job.IwiMipmaps}, мипмапы DDS: {job.DdsMipmaps}, DDS-сжатие: {job.DdsFormat}, TGA RLE: {job.TgaRle}, PNG: {job.PngLevel}, быстрое DXT: {job.FastDxt}"
                    : $"[DEBUG] Start: files {total}, threads {maxDegreeOfParallelism}, mode: {modeText}, CoD version: {codVerIndex} (IWI v{IwiCodec.VersionByIndex[Math.Clamp(codVerIndex, 0, IwiCodec.VersionByIndex.Length - 1)]}), IWI mipmaps: {job.IwiMipmaps}, DDS mipmaps: {job.DdsMipmaps}, DDS compression: {job.DdsFormat}, TGA RLE: {job.TgaRle}, PNG: {job.PngLevel}, fast DXT: {job.FastDxt}");
            }

            await Task.Run(() =>
            {
                try
                {
                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = maxDegreeOfParallelism,
                        CancellationToken = token
                    };

                    Parallel.ForEach(ordered, parallelOptions, file =>
                    {
                        token.ThrowIfCancellationRequested();
                        _pauseEvent.Wait(token); // ожидание снятия с паузы; при "Стоп" бросает OperationCanceledException

                        var fileClock = Stopwatch.StartNew();
                        if (debug)
                            LogDebug(file.IsArchiveEntry
                                ? $"[DEBUG] {file.FullPath} · {file.FileSizeBytes:N0} " + (isRu ? "байт (из архива)" : "bytes (from an archive)")
                                : DescribeInput(file.FullPath, isRu));

                        try
                        {
                            var (savedFile, skipped) = ConvertFile(file, job);
                            file.ConvertedSignature = signature;
                            file.SetState(ConvState.Done, txtDone);
                            Interlocked.Increment(ref successCount);
                            if (skipped)
                                LogInfo(isRu
                                    ? $"[ПРОПУСК] {file.FileName}: такой же файл уже сконвертирован → {Path.GetFileName(savedFile)}"
                                    : $"[SKIP] {file.FileName}: an identical file was already converted → {Path.GetFileName(savedFile)}");
                            else
                            {
                                LogSuccess($"[OK] {file.FileName} → {Path.GetFileName(savedFile)}");
                                if (debug)
                                {
                                    long outSize = 0;
                                    try { outSize = new FileInfo(savedFile).Length; } catch { }
                                    LogDebug(isRu
                                        ? $"    готово за {fileClock.ElapsedMilliseconds} мс · результат: {outSize:N0} байт → {savedFile}"
                                        : $"    done in {fileClock.ElapsedMilliseconds} ms · result: {outSize:N0} bytes → {savedFile}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            file.SetState(ConvState.Error, txtErr);
                            Interlocked.Increment(ref failCount);
                            LogError($"[{errTag}] {file.FileName}: {ex.Message}" + (debug ? "\n" + ErrorDetails(ex) : ""));
                        }

                        Interlocked.Increment(ref completed);
                    });
                }
                catch (OperationCanceledException)
                {
                    // Нажали "Стоп" — это штатная ситуация (раньше здесь падала вся программа)
                }
                catch (Exception fatal)
                {
                    LogError(isRu ? $"Сбой: {fatal.Message}" : $"Failure: {fatal.Message}");
                }
            });

            archiveExtractor.Dispose(); // удаляем временную папку с распакованными файлами

            uiTimer.Stop();
            _convStopwatch.Stop();
            TxtElapsed.Text = FormatElapsed(_convStopwatch.Elapsed);
            PushProgress(Volatile.Read(ref completed), total);
            FlushLog();

            if (token.IsCancellationRequested)
                LogInfo(isRu ? "Конвертация остановлена." : "Conversion stopped.");
            else
                LogInfo(isRu ? "Конвертация завершена." : "Conversion finished.");

            if (AppTaskbarInfo != null) AppTaskbarInfo.ProgressState = TaskbarItemProgressState.None;
            TrimMemory();
            FlashTaskbar();
            if (_settings.PlaySoundOnFinish) try { SystemSounds.Asterisk.Play(); } catch { }
            if (ChkClearLogsAfter.IsChecked == true) ClearLog();

            BtnConvertAll.IsEnabled = true;
            BtnConvertSelected.IsEnabled = true;
            BtnPackAll.IsEnabled = true;
            BtnPackSelected.IsEnabled = true;
            BtnStop.IsEnabled = false;
            BtnPause.Visibility = Visibility.Collapsed;

            // Итог теперь в журнале (вместо отдельного окна)
            string elapsedText = FormatElapsed(_convStopwatch.Elapsed);
            string summary = isRu
                ? $"Итого: готово {successCount} из {pendingFiles.Count}, ошибок: {failCount}, время: {elapsedText}."
                : $"Summary: {successCount} of {pendingFiles.Count} done, errors: {failCount}, time: {elapsedText}.";
            Color summaryColor = failCount > 0 ? WarnColor(_settings.IsDarkTheme) : Color.FromRgb(46, 125, 50);
            AppendLog(summary, summaryColor, failCount > 0); // при ошибках строка остаётся видимой в режиме «Смотреть ошибки»
            if (debug && IwiCodec.WaveletTablesSource != null)
                LogDebug(isRu ? $"[DEBUG] Таблицы wavelet: {IwiCodec.WaveletTablesSource}" : $"[DEBUG] Wavelet tables: {IwiCodec.WaveletTablesSource}");
        }

        private void PushProgress(int current, int max)
        {
            UpdateProgress(current, max);
            if (AppTaskbarInfo != null && max > 0)
                AppTaskbarInfo.ProgressValue = (double)current / max;
        }

        private void UpdateProgress(int current, int max)
        {
            PbProgress.Maximum = max;
            PbProgress.Value = current;
            int percent = max > 0 ? (int)((double)current / max * 100) : 0;
            TxtProgressPercent.Text = $"{percent}% ({current} / {max})";
        }

        private sealed record ConvertJob(
            int CodVerIndex,
            bool IsAuto,
            string AutoExt,
            string FixedExt,
            bool SaveOriginal,
            string CustomPath,
            bool FastDxt,
            bool FastIo,
            bool IwiMipmaps,
            int IwiFormatMode,
            bool DdsMipmaps,
            int DdsFormat,
            bool TgaRle,
            int PngLevel,
            int TgaDepth,
            int PngDepth,
            bool NoMipsForSmall,
            HashSet<string> BatchInputs,
            ConcurrentDictionary<string, string> ClaimedOutputs,
            ArchiveExtractor Archives);

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        /// <summary>Мигание кнопки в панели задач (как уведомление в Telegram/Steam) — до тех пор, пока окно не откроют.</summary>
        private void FlashTaskbar()
        {
            try
            {
                if (!_settings.FlashTaskbar || IsActive) return;
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                var info = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                    hwnd = hwnd,
                    dwFlags = 0x2 | 0xC, // FLASHW_TRAY | FLASHW_TIMERNOFG: мигать, пока окно не станет активным
                    uCount = uint.MaxValue,
                    dwTimeout = 0
                };
                FlashWindowEx(ref info);
            }
            catch { }
        }

        private (string Path, bool Skipped) ConvertFile(TextureFile file, ConvertJob job)
        {
            bool isRu = _settings.IsRussian;

            bool fromArchive = file.IsArchiveEntry;
            // «рядом» для файла из архива — это папка, где лежит сам архив
            string dir = System.IO.Path.GetDirectoryName(fromArchive ? file.ArchivePath! : file.FullPath) ?? "";
            string outFolder = job.SaveOriginal ? dir : job.CustomPath;
            if (string.IsNullOrWhiteSpace(outFolder)) outFolder = dir;

            string inExt = System.IO.Path.GetExtension(file.FullPath).ToLowerInvariant();
            string targetExt = job.IsAuto
                ? (inExt == ".iwi" || inExt == ".vtf" ? job.AutoExt : ".iwi")
                : job.FixedExt;

            string baseName = System.IO.Path.GetFileNameWithoutExtension(file.FileName);
            // Файл из архива: результат кладём в подпапку с именем архива и сохраняем структуру папок архива (images/…),
            // чтобы текстурпак получился в том же виде, в каком он лежал в архиве. Сам архив не меняется.
            string relDir = "";
            if (fromArchive)
            {
                outFolder = System.IO.Path.Combine(outFolder, ArchiveSupport.OutputFolderName(file.ArchivePath!));
                relDir = ArchiveSupport.SafeRelativeDir(file.EntryPath!);
            }
            string outPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(outFolder, relDir, baseName + targetExt));
            string outDir = System.IO.Path.GetDirectoryName(outPath)!;

            // Защита от перезаписи исходника (например, PNG ➔ PNG рядом с оригиналом)
            if (string.Equals(outPath, file.FullPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException(isRu
                    ? "Результат совпадает с исходным файлом. Выберите другой формат или папку."
                    : "The result would overwrite the source file. Pick another format or folder.");

            byte[] src;
            if (fromArchive)
            {
                string temp = job.Archives.GetFile(file.ArchivePath!, file.EntryPath!);
                try { src = File.ReadAllBytes(temp); }
                finally { try { File.Delete(temp); } catch { } } // место на диске освобождаем сразу
            }
            else
            {
                src = File.ReadAllBytes(file.FullPath);
            }

            // Одинаковые имена из разных папок при сохранении в одну папку:
            //  - содержимое идентично ➔ повторно не конвертируем (SKIP);
            //  - содержимое разное ➔ сохраняем с суффиксом _2, _3 ... (ничего не затирается).
            string candidate = outPath;
            for (int n = 2; ; n++)
            {
                if (job.BatchInputs.Contains(candidate))
                    throw new IOException(isRu
                        ? "Результат затёр бы другой файл из списка."
                        : "The result would overwrite another file in the list.");

                if (job.ClaimedOutputs.TryAdd(candidate, file.FullPath)) break;

                if (job.ClaimedOutputs.TryGetValue(candidate, out string? owner) && IsSameContent(src, owner))
                    return (candidate, true);

                candidate = System.IO.Path.Combine(outDir, $"{baseName}_{n}{targetExt}");
            }

            Directory.CreateDirectory(outDir); // папку сохранения создаём, если её нет

            byte[] result = Transcode(src, inExt, targetExt, job, file.FileName, file.FullPath);
            WriteAtomic(candidate, result, job.FastIo);
            return (candidate, false);
        }

        private static bool IsSameContent(byte[] data, string otherPath)
        {
            try
            {
                if (ArchiveSupport.TrySplit(otherPath, out _, out _)) return false; // источник внутри архива: не сравниваем, берём суффикс _2
                if (new FileInfo(otherPath).Length != data.Length) return false;
                return data.AsSpan().SequenceEqual(File.ReadAllBytes(otherPath));
            }
            catch { return false; }
        }

        private static byte[] Transcode(byte[] src, string inExt, string targetExt, ConvertJob job, string fileName, string fullPath)
        {
            // IWI и DDS — контейнеры с готовым DXT: работаем с байтами напрямую, без ImageMagick
            byte[]? dds = inExt == ".iwi" ? IwiCodec.IwiToDds(src)
                        : inExt == ".vtf" ? VtfCodec.VtfToDds(src)
                        : (inExt == ".dds" ? src : null);

            switch (targetExt)
            {
                case ".dds":
                    return dds ?? MagickToDds(src, inExt, job, fileName);
                case ".iwi":
                    return ToIwi(src, inExt, dds, job, fullPath);
                default: // .png / .tga
                    return ToRaster(dds ?? src, dds != null ? ".dds" : inExt, targetExt, job);
            }
        }

        private static byte[] MagickToDds(byte[] src, string inExt, ConvertJob job, string fileName)
        {
            using var image = LoadMagick(src, inExt);
            return EncodeDdsForTarget(image, job, fileName);
        }

        private static byte[] ToIwi(byte[] src, string inExt, byte[]? dds, ConvertJob job, string fullPath)
        {
            // 0 = авто, 1 = DXT5, 2 = DXT1 (как в настройках), 3 — служебное значение для Magick: dxt1
            int formatMode = job.IwiFormatMode;

            // «Авто»: если рядом лежит готовый .iwi (оригинал из игры) — берём формат из него.
            // Это надёжнее, чем угадывать по альфе: Black Ops требует тот же DXT1/DXT5, что и в оригинале.
            if (formatMode == 0 && TryGetReferenceIwiFormat(fullPath, out int referenceMode)) formatMode = referenceMode;

            // Быстрый путь: DXT уже есть и мипы на месте ➔ меняем только контейнер (без потери качества).
            // Но только если формат совпадает с выбранным в настройках (иначе перекодируем).
            bool formatMatches = formatMode == 0 || (dds != null && DdsMatchesFormat(dds, formatMode));
            if (formatMatches && dds != null &&
                IwiCodec.TryDdsToIwi(dds, job.CodVerIndex, job.IwiMipmaps, false, out byte[] direct) == IwiBuildResult.Ok)
                return direct;

            // Иначе (PNG/TGA, несжатый DDS, другой DXT или без мипов) сжимаем в DXT через Magick и при необходимости создаём мипы
            using var image = dds != null ? LoadMagick(dds, ".dds") : LoadMagick(src, inExt);
            bool mips = job.IwiMipmaps && IwiCodec.SupportsMipChain(job.CodVerIndex);
            int ddsFormat = formatMode switch { 1 => 1, 2 => 3, _ => 0 }; // 1 → dxt5, 3 → dxt1, 0 → авто
            byte[] compressed = EncodeDds(image, job.FastDxt, ddsFormat, mips);
            if (IwiCodec.TryDdsToIwi(compressed, job.CodVerIndex, job.IwiMipmaps, true, out byte[] iwi) != IwiBuildResult.Ok)
                throw new InvalidDataException("Не удалось сжать текстуру в DXT.");
            return iwi;
        }

        /// <summary>
        /// Ищет рядом с исходником готовый .iwi того же имени (или без цифр/подчёркиваний в конце,
        /// например «~-gray_gun1.png» → «~-gray_gun.iwi») и возвращает его формат:
        /// 2 = DXT1, 1 = DXT5. Так «Авто» повторяет формат оригинала, который использует игра.
        /// </summary>
        private static bool TryGetReferenceIwiFormat(string? sourcePath, out int mode)
        {
            mode = 0;
            try
            {
                if (string.IsNullOrEmpty(sourcePath)) return false;

                string dir = System.IO.Path.GetDirectoryName(sourcePath) ?? "";
                if (dir.Length == 0) return false;

                string baseName = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
                var candidates = new List<string> { System.IO.Path.Combine(dir, baseName + ".iwi") };

                string trimmed = baseName.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '_', '-', ' ');
                if (trimmed.Length > 0 && !string.Equals(trimmed, baseName, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(System.IO.Path.Combine(dir, trimmed + ".iwi"));

                foreach (string candidate in candidates)
                {
                    if (!File.Exists(candidate)) continue;

                    var head = new byte[8];
                    using (var fs = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (fs.Read(head, 0, head.Length) < head.Length) continue;
                    }

                    if (head[0] != (byte)'I' || head[1] != (byte)'W' || head[2] != (byte)'i') continue;

                    switch (head[4])
                    {
                        case 0x0D: mode = 1; return true; // DXT5
                        case 0x0B: mode = 2; return true; // DXT1
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>Совпадает ли сжатие готового DDS с выбранным режимом IWI (1 = DXT5, 2 = DXT1).</summary>
        private static bool DdsMatchesFormat(byte[] dds, int iwiFormatMode)
        {
            if (dds.Length < 128) return false;
            if (BitConverter.ToUInt32(dds, 0) != 0x20534444u) return false; // "DDS "
            string fourCc = System.Text.Encoding.ASCII.GetString(dds, 84, 4);
            return iwiFormatMode switch
            {
                1 => fourCc == "DXT5",
                2 => fourCc == "DXT1",
                _ => true
            };
        }

        private static byte[] ToRaster(byte[] bytes, string readExt, string targetExt, ConvertJob job)
        {
            using var image = LoadMagick(bytes, readExt);
            if (targetExt == ".png")
            {
                image.Format = MagickFormat.Png;
                // PNG без потерь. Десятки = уровень zlib, единицы = фильтр (5 = адаптивный).
                image.Quality = job.PngLevel switch { 0 => 15u, 2 => 95u, _ => 35u };

                switch (job.PngDepth)
                {
                    case 1: // 32 бита (RGBA)
                        if (!image.HasAlpha) image.Alpha(AlphaOption.Opaque);
                        image.Settings.SetDefine(MagickFormat.Png, "bit-depth", "8");
                        image.Settings.SetDefine(MagickFormat.Png, "color-type", "6");
                        break;
                    case 2: // 24 бита (RGB), без альфы
                        image.Alpha(AlphaOption.Off);
                        image.Settings.SetDefine(MagickFormat.Png, "bit-depth", "8");
                        image.Settings.SetDefine(MagickFormat.Png, "color-type", "2");
                        break;
                    case 3: // 8 бит (оттенки серого)
                        image.Grayscale();
                        image.Alpha(AlphaOption.Off);
                        image.Settings.SetDefine(MagickFormat.Png, "bit-depth", "8");
                        image.Settings.SetDefine(MagickFormat.Png, "color-type", "0");
                        break;
                }
            }
            else
            {
                image.Format = MagickFormat.Tga;
                image.Settings.Compression = job.TgaRle ? CompressionMethod.RLE : CompressionMethod.NoCompression;

                switch (job.TgaDepth)
                {
                    case 1: // 32 бита (RGBA)
                        if (!image.HasAlpha) image.Alpha(AlphaOption.Opaque);
                        break;
                    case 2: // 24 бита (RGB)
                        image.Alpha(AlphaOption.Off);
                        break;
                }
            }

            using var ms = new MemoryStream();
            image.Write(ms);
            return ms.ToArray();
        }

        private static MagickImage LoadMagick(byte[] bytes, string ext)
        {
            var readSettings = new MagickReadSettings();
            if (ext == ".tga") readSettings.Format = MagickFormat.Tga;
            else if (ext == ".png") readSettings.Format = MagickFormat.Png;
            else if (ext == ".dds" || ext == ".iwi") readSettings.Format = MagickFormat.Dds;
            return new MagickImage(bytes, readSettings);
        }

        /// <summary>
        /// DDS для PNG/TGA. Форматы: 0 = авто (DXT1/DXT5), 1 = DXT5, 2 = без сжатия, 3 = BC5, 4 = BC7,
        /// 5 = автоопределение под игру (Black Ops 2: BC7/BC5, остальные игры: DXT1/DXT5, ATI2, ARGB32).
        /// </summary>
        private static byte[] EncodeDdsForTarget(MagickImage image, ConvertJob job, string fileName)
        {
            int w = (int)image.Width, h = (int)image.Height;
            TexKind tex = DdsEncoders.Classify(fileName);
            bool small = Math.Max(w, h) <= 128;
            bool mips = job.DdsMipmaps && !(job.NoMipsForSmall && (small || tex == TexKind.Ui));

            DdsKind kind;
            bool dx10 = false;
            switch (job.DdsFormat)
            {
                case 1: kind = DdsKind.Dxt5; break;
                case 2: kind = DdsKind.Uncompressed; break;
                case 3: kind = DdsKind.Bc5; break;
                case 4: kind = DdsKind.Bc7; dx10 = true; break;
                case 5: (kind, dx10) = DdsEncoders.ChooseProfile(job.CodVerIndex, tex, image.HasAlpha); break;
                default: kind = image.HasAlpha ? DdsKind.Dxt5 : DdsKind.Dxt1; break;
            }

            if (kind == DdsKind.Bc5 || kind == DdsKind.Bc7)
                return DdsEncoders.BuildBcDds(kind, dx10, GetRgbaLevels(image, mips));

            int magickMode = kind == DdsKind.Uncompressed ? 2 : (kind == DdsKind.Dxt5 ? 1 : 0);
            return EncodeDds(image, job.FastDxt, magickMode, mips);
        }

        /// <summary>Уровни мипов в виде сырых байт RGBA (для собственных кодировщиков BC5/BC7).</summary>
        private static List<RgbaLevel> GetRgbaLevels(MagickImage image, bool mips)
        {
            int w = (int)image.Width, h = (int)image.Height;
            int count = mips ? IwiCodec.FullMipCount(w, h) : 1;
            var levels = new List<RgbaLevel>(count) { new RgbaLevel(w, h, ToRgbaBytes(image, w, h)) };

            for (int i = 1; i < count; i++)
            {
                int lw = Math.Max(1, w >> i), lh = Math.Max(1, h >> i);
                using var copy = new MagickImage(image);
                copy.Resize(new MagickGeometry((uint)lw, (uint)lh) { IgnoreAspectRatio = true });
                levels.Add(new RgbaLevel(lw, lh, ToRgbaBytes(copy, lw, lh)));
            }
            return levels;
        }

        private static byte[] ToRgbaBytes(MagickImage image, int w, int h)
        {
            image.Depth = 8;
            byte[]? raw = image.ToByteArray(MagickFormat.Rgba);
            if (raw == null || raw.Length != w * h * 4)
                throw new InvalidDataException("Не удалось получить пиксели изображения для кодирования BC5/BC7.");
            return raw;
        }

        /// <param name="ddsFormat">0 = DXT1/DXT5 по наличию альфы, 1 = всегда DXT5, 2 = без сжатия</param>
        private static byte[] EncodeDds(MagickImage image, bool fastDxt, int ddsFormat, bool mipmaps)
        {
            byte[] first = EncodeDdsSingle(image, fastDxt, ddsFormat);
            int levels = mipmaps ? IwiCodec.FullMipCount((int)image.Width, (int)image.Height) : 1;
            if (levels <= 1 || first.Length <= 128) return first;

            // Мипмапы собираем сами: каждый уровень уменьшаем и кодируем отдельно, затем склеиваем в один DDS.
            // Так цепочка получается всегда (встроенная генерация ImageMagick зависит от размеров и версии).
            var payloads = new List<byte[]>(levels);
            var firstPayload = new byte[first.Length - 128];
            Buffer.BlockCopy(first, 128, firstPayload, 0, firstPayload.Length);
            payloads.Add(firstPayload);

            int w = (int)image.Width, h = (int)image.Height;
            for (int i = 1; i < levels; i++)
            {
                int lw = Math.Max(1, w >> i), lh = Math.Max(1, h >> i);
                using var copy = new MagickImage(image);
                copy.Resize(new MagickGeometry((uint)lw, (uint)lh) { IgnoreAspectRatio = true });
                byte[] level = EncodeDdsSingle(copy, fastDxt, ddsFormat);
                var payload = new byte[level.Length - 128];
                Buffer.BlockCopy(level, 128, payload, 0, payload.Length);
                payloads.Add(payload);
            }

            long total = 128;
            foreach (byte[] p in payloads) total += p.Length;
            var dds = new byte[total];
            Buffer.BlockCopy(first, 0, dds, 0, 128);

            // в заголовке: флаг и число мипов, признаки сложной текстуры
            uint flags = BitConverter.ToUInt32(dds, 8) | 0x20000u;
            Buffer.BlockCopy(BitConverter.GetBytes(flags), 0, dds, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)levels), 0, dds, 28, 4);
            uint caps = BitConverter.ToUInt32(dds, 108) | 0x400008u;
            Buffer.BlockCopy(BitConverter.GetBytes(caps), 0, dds, 108, 4);

            int pos = 128;
            foreach (byte[] p in payloads)
            {
                Buffer.BlockCopy(p, 0, dds, pos, p.Length);
                pos += p.Length;
            }
            return dds;
        }

        /// <summary>
        /// Есть ли в картинке РЕАЛЬНАЯ прозрачность. Просто наличия альфа-канала мало: PNG из
        /// графических редакторов почти всегда идёт с полностью непрозрачной альфой, и из-за этого
        /// «Авто» выбирал DXT5 там, где игра ждёт DXT1 (Black Ops).
        /// </summary>
        private static bool ImageUsesAlpha(MagickImage image)
        {
            if (!image.HasAlpha) return false;

            try
            {
                using var small = new MagickImage(image);
                if (small.Width > 64 || small.Height > 64)
                    small.Resize(new MagickGeometry(64, 64) { IgnoreAspectRatio = true });

                byte[]? rgba = small.GetPixels().ToByteArray(PixelMapping.RGBA);
                if (rgba == null) return true;

                for (int i = 3; i < rgba.Length; i += 4)
                {
                    if (rgba[i] < 250) return true; // есть заметно прозрачные пиксели
                }
                return false;
            }
            catch
            {
                return image.HasAlpha; // если проверить не удалось — ведём себя как раньше
            }
        }

        private static byte[] EncodeDdsSingle(MagickImage image, bool fastDxt, int ddsFormat)
        {
            string comp = ddsFormat switch
            {
                2 => "none",
                1 => "dxt5",
                3 => "dxt1",
                _ => ImageUsesAlpha(image) ? "dxt5" : "dxt1"
            };
            image.Settings.SetDefine(MagickFormat.Dds, "compression", comp);
            // cluster-fit — лучшее качество, но заметно медленнее; range-fit — быстрый режим
            image.Settings.SetDefine(MagickFormat.Dds, "cluster-fit", fastDxt ? "false" : "true");
            image.Format = MagickFormat.Dds;

            using var ms = new MemoryStream();
            image.Write(ms);
            return ms.ToArray();
        }

        /// <summary>Запись через временный файл: при сбое или остановке не остаётся «обрезанных» результатов.</summary>
        private static void WriteAtomic(string outPath, byte[] data, bool bigBuffer)
        {
            string tmp = outPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                // Без FileOptions.WriteThrough: он принудительно сбрасывал каждый файл на диск и сильно тормозил запись
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bigBuffer ? 65536 : 4096, FileOptions.SequentialScan))
                {
                    fs.Write(data, 0, data.Length);
                }
                File.Move(tmp, outPath, true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }
    }

    // ================= БАЗОВОЕ МОДАЛЬНОЕ ОКНО =================
    public class FluentModalWindow : Window
    {
        protected Border? RootBorder;
        protected ScaleTransform RootScale = new(0.92, 0.92);

        public FluentModalWindow(Window owner)
        {
            Owner = owner;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
        }

        protected void AttachAnimation(Border root)
        {
            RootBorder = root;
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            root.RenderTransform = RootScale;
            root.Opacity = 0;

            Loaded += (s, e) => PlayOpenAnimation();
        }

        private void PlayOpenAnimation()
        {
            if (RootBorder == null) return;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            RootBorder.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        }

        public void CloseWithAnimation(bool? dialogResult = null)
        {
            if (RootBorder == null)
            {
                try { DialogResult = dialogResult; } catch { }
                Close();
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var animOpacity = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease };
            animOpacity.Completed += (s, e) =>
            {
                try { DialogResult = dialogResult; } catch { }
                Close();
            };

            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 0.94, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 0.94, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
            RootBorder.BeginAnimation(UIElement.OpacityProperty, animOpacity);
        }

        protected void SetupDragHeader(UIElement header)
        {
            header.MouseLeftButtonDown += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed) DragMove();
            };
        }

        protected static Button CreateCloseButton(Action onClick)
        {
            var btn = new Button
            {
                Content = "✕",
                Width = 28,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Right,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Cursor = Cursors.Hand,
                Foreground = Brushes.Gray
            };

            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "b";
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            template.VisualTree = border;

            var hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(232, 17, 35)), "b"));
            hover.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.White));
            template.Triggers.Add(hover);

            btn.Template = template;
            btn.Click += (s, e) => onClick();
            return btn;
        }

        protected static Button CreateModernButton(string text, Brush bg, Brush fg, Brush? borderBrush, double height, CornerRadius cornerRadius, Action? onClick = null)
        {
            var btn = new Button
            {
                Content = text,
                Height = height,
                FontWeight = FontWeights.Bold,
                Foreground = fg,
                Cursor = Cursors.Hand
            };

            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "b";
            border.SetValue(Border.BackgroundProperty, bg);
            border.SetValue(Border.CornerRadiusProperty, cornerRadius);
            if (borderBrush != null)
            {
                border.SetValue(Border.BorderBrushProperty, borderBrush);
                border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            }

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            template.VisualTree = border;

            var hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85, "b"));
            template.Triggers.Add(hover);

            var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.65, "b"));
            template.Triggers.Add(pressed);

            btn.Template = template;
            if (onClick != null) btn.Click += (s, e) => onClick();
            return btn;
        }
    }

    // ================= ОКНО НАСТРОЕК (С ВЫБОРОМ ЯЗЫКА) =================
    public class SettingsDialog : FluentModalWindow
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public SettingsDialog(Window owner, AppSettings settings, Action<bool> onThemeChanged, Action<bool> onLanguageChanged) : base(owner)
        {
            // общий вид галочек и цвета — как в главном окне
            foreach (var key in new[] { "AccentBrush", "TextBrush", "TextMutedBrush", "BorderBrush", "InputBgBrush", "HoverOverlayBrush" })
            {
                if (owner.Resources[key] != null) Resources[key] = owner.Resources[key];
            }
            if (owner.Resources[typeof(CheckBox)] != null) Resources[typeof(CheckBox)] = owner.Resources[typeof(CheckBox)];

            bool isDark = settings.IsDarkTheme;
            bool isRu = settings.IsRussian;

            var panelBrush = new SolidColorBrush(isDark ? Color.FromRgb(37, 37, 38) : Colors.White);
            var cardBrush = new SolidColorBrush(isDark ? Color.FromRgb(42, 42, 45) : Color.FromRgb(245, 246, 248));
            var textBrush = new SolidColorBrush(isDark ? Colors.White : Color.FromRgb(28, 30, 33));
            var borderBrush = new SolidColorBrush(isDark ? Color.FromRgb(62, 62, 66) : Color.FromRgb(215, 218, 224));

            // Обычная рамка окна Windows: кнопки «свернуть», «развернуть», «закрыть»; в тёмной теме — тёмный заголовок
            AllowsTransparency = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            SizeToContent = SizeToContent.Manual;
            Title = isRu ? "Настройки" : "Settings";
            Background = panelBrush;
            Width = 640;
            Height = Math.Min(780, SystemParameters.WorkArea.Height - 60);
            MinWidth = 570;
            MinHeight = 440;
            SourceInitialized += (_, _) =>
            {
                try
                {
                    int dark = isDark ? 1 : 0;
                    DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
                }
                catch { }
            };

            var mainBorder = new Border { Background = panelBrush };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var bodyStack = new StackPanel { Margin = new Thickness(20, 34, 20, 34) };
            var bodyScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = bodyStack
            };
            // ---- вкладки: Основное / Производительность / Формат и сжатие
            var tabMain = new StackPanel();
            var tabPerf = new StackPanel { Visibility = Visibility.Collapsed };
            var tabFormat = new StackPanel { Visibility = Visibility.Collapsed };
            bodyStack.Children.Add(tabMain);
            bodyStack.Children.Add(tabPerf);
            bodyStack.Children.Add(tabFormat);

            var accentBrush = new SolidColorBrush(Color.FromRgb(0, 122, 204));
            var tabPanels = new[] { tabMain, tabPerf, tabFormat };
            var tabTitles = new[]
            {
                isRu ? "Основное" : "General",
                isRu ? "Производительность" : "Performance",
                isRu ? "Формат и сжатие" : "Format & compression"
            };
            var tabButtons = new Border[3];

            void SelectTab(int index)
            {
                for (int i = 0; i < 3; i++)
                {
                    bool active = i == index;
                    tabPanels[i].Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                    tabButtons[i].Background = active ? accentBrush : Brushes.Transparent;
                    var label = (TextBlock)tabButtons[i].Child;
                    label.Foreground = active ? Brushes.White : textBrush;
                    label.Opacity = active ? 1.0 : 0.75;
                }
            }

            var tabRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 14, 20, 6) };
            for (int i = 0; i < 3; i++)
            {
                int index = i;
                var tab = new Border
                {
                    Child = new TextBlock { Text = tabTitles[i], FontWeight = FontWeights.SemiBold, FontSize = 13 },
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 6, 14, 6),
                    Margin = new Thickness(0, 0, 6, 0),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Transparent
                };
                tab.MouseLeftButtonUp += (_, _) => SelectTab(index);
                tabButtons[i] = tab;
                tabRow.Children.Add(tab);
            }
            SelectTab(0);
            Grid.SetRow(tabRow, 1);
            mainGrid.Children.Add(tabRow);

            Grid.SetRow(bodyScroll, 2);

            // КАРТОЧКА 1: ЯЗЫК
            var cardLang = CreateCard(cardBrush, borderBrush, textBrush, "Язык / Language");
            var segTrack = new SolidColorBrush(isDark ? Color.FromRgb(30, 30, 33) : Color.FromRgb(232, 235, 240));
            var langSeg = new SegmentedControl(new[] { "Русский", "English" }, isRu ? 0 : 1, textBrush, segTrack);
            langSeg.SelectionChanged += pick => onLanguageChanged(pick == 0);
            ((StackPanel)cardLang.Child).Children.Add(langSeg);
            tabMain.Children.Add(cardLang);

            // КАРТОЧКА 2: ТЕМА
            var cardTheme = CreateCard(cardBrush, borderBrush, textBrush, isRu ? "Оформление" : "Appearance");
            var themeSeg = new SegmentedControl(new[] { isRu ? "Тёмная" : "Dark", isRu ? "Светлая" : "Light" }, isDark ? 0 : 1, textBrush, segTrack);
            themeSeg.SelectionChanged += pick => onThemeChanged(pick == 0);
            ((StackPanel)cardTheme.Child).Children.Add(themeSeg);
            tabMain.Children.Add(cardTheme);

            // КАРТОЧКА 3: CPU
            var cardCpu = CreateCard(cardBrush, borderBrush, textBrush, isRu ? "Нагрузка на процессор" : "CPU load");
            var cpuMode = new SegmentedControl(
                new[]
                {
                    isRu ? "Максимум (80%)" : "Max (80%)",
                    isRu ? "Баланс (40%)" : "Balanced (40%)",
                    isRu ? "Эко (15%)" : "Eco (15%)"
                },
                Math.Clamp(settings.PerformanceMode, 0, 2),
                textBrush,
                new SolidColorBrush(isDark ? Color.FromRgb(30, 30, 33) : Color.FromRgb(232, 235, 240)),
                new[]
                {
                    isRu ? "Использовать до 80% ядер процессора (высокая скорость)" : "Use up to 80% of CPU cores (high speed)",
                    isRu ? "Использовать около 40% ядер процессора (комфортная работа ПК)" : "Use about 40% of CPU cores (comfortable PC use)",
                    isRu ? "Использовать около 15% ядер процессора (фоновый щадящий режим)" : "Use about 15% of CPU cores (gentle background mode)"
                });
            ((StackPanel)cardCpu.Child).Children.Add(cpuMode);
            tabPerf.Children.Add(cardCpu);

            // КАРТОЧКА 4: ЖЕЛЕЗО
            var cardHw = CreateCard(cardBrush, borderBrush, textBrush, isRu ? "Ускорение" : "Speed");
            var chkGpu = new CheckBox { Content = isRu ? "Использовать OpenCL (видеокарта). Помогает не всегда" : "Use OpenCL (GPU). Doesn't always help", Foreground = textBrush, IsChecked = settings.EnableGpu, Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand };
            var chkNvme = new CheckBox { Content = isRu ? "Быстрая запись на NVMe (буфер 64 КБ)" : "Fast NVMe writes (64 KB buffer)", Foreground = textBrush, IsChecked = settings.FastNvme, Cursor = Cursors.Hand };
            var chkFastDxt = new CheckBox { Content = isRu ? "Быстрое DXT-сжатие (качество чуть ниже)" : "Fast DXT compression (slightly lower quality)", Foreground = textBrush, IsChecked = settings.FastDxt, Margin = new Thickness(0, 6, 0, 0), Cursor = Cursors.Hand };
            ((StackPanel)cardHw.Child).Children.Add(chkGpu);
            ((StackPanel)cardHw.Child).Children.Add(chkNvme);
            ((StackPanel)cardHw.Child).Children.Add(chkFastDxt);
            tabPerf.Children.Add(cardHw);

            // КАРТОЧКИ ФОРМАТОВ: у каждого формата — своя карточка
            CheckBox MakeCheck(string text, bool value, double top = 0) => new CheckBox { Content = text, Foreground = textBrush, IsChecked = value, Margin = new Thickness(0, top, 0, 0), Cursor = Cursors.Hand };
            TextBlock MakeSub(string text) => new TextBlock { Text = text, Foreground = textBrush, Opacity = 0.75, Margin = new Thickness(0, 12, 0, 6) };
            TextBlock MakeNote(string text) => new TextBlock { Text = text, Foreground = textBrush, Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            var trackBrush = new SolidColorBrush(isDark ? Color.FromRgb(30, 30, 33) : Color.FromRgb(232, 235, 240));

            // ---- IWI
            var cardIwi = CreateCard(cardBrush, borderBrush, textBrush, isRu ? "IWI — игры Call of Duty" : "IWI — Call of Duty games");
            var iwiStack = (StackPanel)cardIwi.Child;
            var chkIwiMips = MakeCheck(isRu ? "Создавать мипмапы (игре они нужны)" : "Generate mipmaps (the game needs them)", settings.IwiMipmaps);
            iwiStack.Children.Add(chkIwiMips);
            iwiStack.Children.Add(MakeSub(isRu ? "Сжатие IWI" : "IWI compression"));
            var iwiFormatSegments = new SegmentedControl(
                new[] { isRu ? "Авто" : "Auto", "DXT5", "DXT1" },
                Math.Clamp(settings.IwiFormatMode, 0, 2),
                textBrush, trackBrush,
                new[]
                {
                    isRu ? "Авто: формат из лежащего рядом .iwi (оригинал), иначе DXT5 только при реальной прозрачности" : "Auto: format from a nearby .iwi (original), otherwise DXT5 only when the image really has transparency",
                    isRu ? "Всегда DXT5 (с альфой, 16 байт на блок)" : "Always DXT5 (with alpha, 16 bytes per block)",
                    isRu ? "Всегда DXT1 (без альфы, 8 байт на блок) — как в оригинале Black Ops" : "Always DXT1 (no alpha, 8 bytes per block) — like the original Black Ops assets"
                });
            iwiStack.Children.Add(iwiFormatSegments);
            iwiStack.Children.Add(MakeNote(isRu
                ? "Мипмапы в IWI создаются для всех поддерживаемых версий, включая Black Ops 2 (v27). «Авто» сначала смотрит формат у лежащего рядом оригинального .iwi (например «~-gray_gun.iwi» для «~-gray_gun1.png»), а если его нет — берёт DXT1 для непрозрачных картинок и DXT5 только при реальной прозрачности."
                : "Mipmaps in IWI are created for all supported versions, including Black Ops 2 (v27). Auto first takes the format from a nearby original .iwi (e.g. ~-gray_gun.iwi for ~-gray_gun1.png), otherwise it uses DXT1 for opaque images and DXT5 only when there is real transparency."));
            tabFormat.Children.Add(cardIwi);

            // ---- DDS
            var cardDds = CreateCard(cardBrush, borderBrush, textBrush, "DDS");
            var ddsStack = (StackPanel)cardDds.Child;
            ddsStack.Children.Add(MakeSub(isRu ? "Сжатие" : "Compression"));
            int[] ddsMap = { 0, 1, 3, 4, 2, 5 }; // порядок кнопок → значение настройки
            var ddsSegments = new SegmentedControl(
                new[]
                {
                    isRu ? "Авто" : "Auto", "DXT5", "BC5", "BC7",
                    isRu ? "Без сжатия" : "None", isRu ? "Под игру" : "By game"
                },
                Math.Max(0, Array.IndexOf(ddsMap, settings.DdsFormat)),
                textBrush, trackBrush,
                new[]
                {
                    isRu ? "DXT1 без альфы или DXT5 с альфой" : "DXT1 without alpha or DXT5 with alpha",
                    isRu ? "Всегда DXT5" : "Always DXT5",
                    isRu ? "BC5 — карта нормалей" : "BC5 — normal map",
                    isRu ? "BC7 — DX11, высокое качество" : "BC7 — DX11, high quality",
                    isRu ? "ARGB32 без сжатия" : "ARGB32, uncompressed",
                    isRu
                        ? "Автоопределение под игру.\nBlack Ops 2 (DirectX 11): BC7 для цвета и BC5 для нормалей.\nCoD2, MW, WaW, MW2, BO1, MW3 (DirectX 9): DXT1 без альфы, DXT5 с альфой, ATI2/BC5 для нормалей, ARGB32 для интерфейса."
                        : "Auto by game.\nBlack Ops 2 (DirectX 11): BC7 for colour and BC5 for normals.\nCoD2, MW, WaW, MW2, BO1, MW3 (DirectX 9): DXT1 without alpha, DXT5 with alpha, ATI2/BC5 for normals, ARGB32 for UI."
                });
            ddsStack.Children.Add(ddsSegments);

            var chkDdsMips = MakeCheck(isRu ? "Создавать мипмапы" : "Generate mipmaps", settings.DdsMipmaps, 12);
            var chkNoMipsSmall = MakeCheck(
                isRu ? "Не создавать мипмапы для мелких текстур и UI (≤ 128×128)" : "Skip mipmaps for small textures and UI (≤ 128×128)",
                settings.NoMipsForSmall, 6);
            ddsStack.Children.Add(chkDdsMips);
            ddsStack.Children.Add(chkNoMipsSmall);
            ddsStack.Children.Add(MakeNote(isRu
                ? "Внимание: сжатие DXT1/DXT5 является сжатием с потерями (Lossy). На плавных градиентах и небе могут возникать ступенчатые артефакты. Для интерфейса, шрифтов и прицелов используйте режим «Без сжатия» (ARGB32)."
                : "Note: DXT1/DXT5 is lossy compression. Smooth gradients and sky can show banding artifacts. For UI, fonts and crosshairs use the \u201cNone\u201d mode (ARGB32)."));
            tabFormat.Children.Add(cardDds);

            // ---- TGA
            var cardTga = CreateCard(cardBrush, borderBrush, textBrush, "TGA");
            var tgaStack = (StackPanel)cardTga.Child;
            var chkTgaRle = MakeCheck(isRu ? "Сжатие RLE" : "RLE compression", settings.TgaRle);
            tgaStack.Children.Add(chkTgaRle);
            tgaStack.Children.Add(MakeSub(isRu ? "Глубина цвета" : "Color depth"));
            var tgaDepth = new SegmentedControl(
                new[] { isRu ? "Авто (по альфе)" : "Auto (by alpha)", isRu ? "32 бита (RGBA)" : "32-bit (RGBA)", isRu ? "24 бита (RGB)" : "24-bit (RGB)" },
                settings.TgaDepth, textBrush, trackBrush);
            tgaStack.Children.Add(tgaDepth);
            tabFormat.Children.Add(cardTga);

            // ---- PNG
            var cardPng = CreateCard(cardBrush, borderBrush, textBrush, "PNG");
            var pngStack = (StackPanel)cardPng.Child;
            pngStack.Children.Add(MakeSub(isRu ? "Сжатие" : "Compression"));
            var pngLevel = new SegmentedControl(
                new[] { isRu ? "Быстро" : "Fast", isRu ? "Обычно" : "Normal", isRu ? "Минимальный размер" : "Smallest size" },
                settings.PngLevel, textBrush, trackBrush);
            pngStack.Children.Add(pngLevel);
            pngStack.Children.Add(MakeSub(isRu ? "Глубина цвета" : "Color depth"));
            var pngDepth = new SegmentedControl(
                new[] { isRu ? "Авто" : "Auto", isRu ? "32 бита (RGBA)" : "32-bit (RGBA)", isRu ? "24 бита (RGB)" : "24-bit (RGB)", isRu ? "8 бит (Grayscale)" : "8-bit (Grayscale)" },
                settings.PngDepth, textBrush, trackBrush);
            pngStack.Children.Add(pngDepth);
            tabFormat.Children.Add(cardPng);

            var cardDiag = CreateCard(cardBrush, borderBrush, textBrush, isRu ? "Диагностика" : "Diagnostics");
            var chkDebugSetting = new CheckBox
            {
                Content = isRu ? "Подробный журнал (Debug): размер файла, кодек, детали ошибок" : "Verbose log (Debug): file size, codec, error details",
                Foreground = textBrush,
                IsChecked = settings.DebugMode,
                Cursor = Cursors.Hand
            };
            ((StackPanel)cardDiag.Child).Children.Add(chkDebugSetting);
            tabFormat.Children.Add(cardDiag);

            // КАРТОЧКА 5: ЗВУК
            var cardSound = CreateCard(cardBrush, borderBrush, textBrush, isRu ? "Уведомления" : "Notifications");
            var chkSound = new CheckBox { Content = isRu ? "Звук по окончании" : "Play a sound when done", Foreground = textBrush, IsChecked = settings.PlaySoundOnFinish, Cursor = Cursors.Hand };
            ((StackPanel)cardSound.Child).Children.Add(chkSound);
            var chkFlash = new CheckBox { Content = isRu ? "Мигать в панели задач, если окно в фоне" : "Flash the taskbar if the window is in the background", Foreground = textBrush, IsChecked = settings.FlashTaskbar, Margin = new Thickness(0, 6, 0, 0), Cursor = Cursors.Hand };
            ((StackPanel)cardSound.Child).Children.Add(chkFlash);
            tabMain.Children.Add(cardSound);

            var cardDrop = CreateCard(cardBrush, borderBrush, textBrush, isRu ? "Добавление файлов" : "Adding files");
            var chkAutoDrop = new CheckBox { Content = isRu ? "Сразу конвертировать то, что перетащили в окно" : "Convert right away whatever is dropped into the window", Foreground = textBrush, IsChecked = settings.AutoConvertOnDrop, Cursor = Cursors.Hand };
            ((StackPanel)cardDrop.Child).Children.Add(chkAutoDrop);
            tabMain.Children.Add(cardDrop);

            // ---- файл открыт двойным кликом или через «Открыть с помощью»
            var cardOpen = CreateCard(cardBrush, borderBrush, textBrush, isRu ? "Открытие файла двойным кликом" : "Opening a file with a double-click");
            var openStack = (StackPanel)cardOpen.Child;
            var chkOpenConvert = MakeCheck(isRu ? "Сразу конвертировать открытый файл" : "Convert the opened file right away", settings.AutoConvertOnOpen);
            var chkOpenPreview = MakeCheck(isRu ? "Показывать предпросмотр открытого файла" : "Show a preview of the opened file", settings.PreviewOnOpen, 6);
            openStack.Children.Add(chkOpenConvert);
            openStack.Children.Add(chkOpenPreview);
            openStack.Children.Add(MakeNote(isRu
                ? "Если обе галочки выключены, файл просто попадёт в список. Работает для IWI, VTF, DDS, TGA, PNG и архивов."
                : "If both are off, the file just lands in the list. Works for IWI, VTF, DDS, TGA, PNG and archives."));
            var assocNote = MakeNote("");
            var assocBg = new SolidColorBrush(isDark ? Color.FromRgb(58, 58, 62) : Color.FromRgb(230, 233, 238));
            var btnAssocOn = CreateModernButton(
                isRu ? "Открывать .iwi этой программой и добавить в «Открыть с помощью»" : "Open .iwi with this program and add to \"Open with\"",
                assocBg, textBrush, borderBrush, 32, new CornerRadius(6),
                () =>
                {
                    bool ok = FileAssociation.Register(out string err);
                    assocNote.Text = ok ? (isRu ? "Готово. Если Windows уже закрепила за .iwi другую программу, выберите эту через «Открыть с помощью → Всегда»." : "Done. If Windows already pinned another program to .iwi, pick this one via \"Open with → Always\".")
                                        : (isRu ? "Не получилось: " : "Failed: ") + err;
                });
            btnAssocOn.Margin = new Thickness(0, 10, 0, 0);
            var btnAssocOff = CreateModernButton(
                isRu ? "Убрать регистрацию" : "Remove registration",
                assocBg, textBrush, borderBrush, 32, new CornerRadius(6),
                () =>
                {
                    bool ok = FileAssociation.Unregister(out string err);
                    assocNote.Text = ok ? (isRu ? "Регистрация убрана." : "Registration removed.") : (isRu ? "Не получилось: " : "Failed: ") + err;
                });
            btnAssocOff.Margin = new Thickness(0, 6, 0, 0);
            openStack.Children.Add(btnAssocOn);
            openStack.Children.Add(btnAssocOff);
            openStack.Children.Add(assocNote);
            tabMain.Children.Add(cardOpen);

            // ---- миниатюры текстур в проводнике Windows (компонент IwiThumbs)
            var cardThumbs = CreateCard(cardBrush, borderBrush, textBrush, isRu ? "Миниатюры в проводнике" : "Thumbnails in Explorer");
            var thumbsStack = (StackPanel)cardThumbs.Child;
            thumbsStack.Children.Add(MakeNote(isRu
                ? "Файлы .iwi и .vtf показывают саму текстуру (с прозрачностью) в режимах «Обычные», «Крупные» и «Огромные значки» — без запуска программы. Компонент один раз собирается файлом BuildThumbnails.cmd (лежит рядом с проектом), затем здесь нажмите «Включить» и перезапустите проводник."
                : "The .iwi and .vtf files show the texture itself (with transparency) in the Medium, Large and Extra large icon views — without starting the program. Build the component once with BuildThumbnails.cmd (next to the project), then press Enable here and restart Explorer."));
            var thumbsNote = MakeNote(FileAssociation.AreThumbnailsRegistered()
                ? (isRu ? "Сейчас: включено." : "Now: enabled.")
                : (isRu ? "Сейчас: выключено." : "Now: disabled."));
            var btnThumbOn = CreateModernButton(isRu ? "Включить миниатюры" : "Enable thumbnails", assocBg, textBrush, borderBrush, 32, new CornerRadius(6), async () =>
            {
                if (FileAssociation.RegisterThumbnails(out string thumbError))
                {
                    thumbsNote.Text = isRu ? "Включено. Перезапустите проводник, чтобы миниатюры появились." : "Enabled. Restart Explorer for the thumbnails to appear.";
                    return;
                }
                if (thumbError == "NOT_BUILT")
                {
                    thumbsNote.Text = isRu ? "Компонент ещё не собран: запустите BuildThumbnails.cmd (лежит рядом с проектом), затем нажмите «Включить» снова." : "The component is not built yet: run BuildThumbnails.cmd (next to the project), then press Enable again.";
                    return;
                }
                if (thumbError == "NEED_ADMIN")
                {
                    // проводник читает обработчик миниатюр только из HKLM, поэтому нужен один запрос UAC
                    thumbsNote.Text = isRu ? "Нужны права администратора — подтвердите запрос Windows…" : "Administrator rights are required — confirm the Windows prompt…";
                    var elevated = await Task.Run(() => FileAssociation.RunThumbnailSetupElevated(true));
                    thumbsNote.Text = elevated.ok
                        ? (isRu ? "Включено. Перезапустите проводник, чтобы миниатюры появились." : "Enabled. Restart Explorer for the thumbnails to appear.")
                        : (isRu ? "Не получилось: " : "Failed: ") + (elevated.error == "UAC_CANCELLED" ? (isRu ? "запрос прав отменён." : "the elevation prompt was cancelled.") : elevated.error);
                    return;
                }
                thumbsNote.Text = (isRu ? "Не получилось: " : "Failed: ") + thumbError;
            });
            btnThumbOn.Margin = new Thickness(0, 10, 0, 0);
            var btnThumbOff = CreateModernButton(isRu ? "Выключить миниатюры" : "Disable thumbnails", assocBg, textBrush, borderBrush, 32, new CornerRadius(6), async () =>
            {
                if (FileAssociation.UnregisterThumbnails(out string thumbError))
                {
                    thumbsNote.Text = isRu ? "Выключено. Перезапустите проводник." : "Disabled. Restart Explorer.";
                    return;
                }
                if (thumbError == "NEED_ADMIN")
                {
                    thumbsNote.Text = isRu ? "Нужны права администратора — подтвердите запрос Windows…" : "Administrator rights are required — confirm the Windows prompt…";
                    var elevated = await Task.Run(() => FileAssociation.RunThumbnailSetupElevated(false));
                    thumbsNote.Text = elevated.ok
                        ? (isRu ? "Выключено. Перезапустите проводник." : "Disabled. Restart Explorer.")
                        : (isRu ? "Не получилось: " : "Failed: ") + (elevated.error == "UAC_CANCELLED" ? (isRu ? "запрос прав отменён." : "the elevation prompt was cancelled.") : elevated.error);
                    return;
                }
                thumbsNote.Text = (isRu ? "Не получилось: " : "Failed: ") + thumbError;
            });
            btnThumbOff.Margin = new Thickness(0, 6, 0, 0);
            var btnRestartExplorer = CreateModernButton(isRu ? "Сбросить кэш миниатюр и перезапустить проводник" : "Clear the thumbnail cache and restart Explorer", assocBg, textBrush, borderBrush, 32, new CornerRadius(6), () =>
            {
                try
                {
                    // кэш миниатюр удаляется при закрытом проводнике: иначе старые «пустые» значки остаются.
                    // Пауза перед запуском обязательна: если стартовать explorer сразу, он может не успеть
                    // закрыться и новый проводник не запустится — останется пустой рабочий стол.
                    Process.Start(new ProcessStartInfo("cmd.exe",
                        "/c taskkill /f /im explorer.exe >nul 2>&1 & ping -n 3 127.0.0.1 >nul & " +
                        "del /f /q \"%localappdata%\\Microsoft\\Windows\\Explorer\\thumbcache_*.db\" >nul 2>&1 & " +
                        "start \"\" explorer.exe")
                    { CreateNoWindow = true, UseShellExecute = false });
                    thumbsNote.Text = isRu ? "Проводник перезапускается. Откройте папку с .iwi ещё раз." : "Explorer is restarting. Open the folder with the .iwi files again.";
                }
                catch (Exception restartEx)
                {
                    thumbsNote.Text = (isRu ? "Не получилось: " : "Failed: ") + restartEx.Message;
                }
            });
            btnRestartExplorer.Margin = new Thickness(0, 6, 0, 0);
            var btnThumbTest = CreateModernButton(isRu ? "Проверить миниатюры" : "Test thumbnails", assocBg, textBrush, borderBrush, 32, new CornerRadius(6), async () =>
            {
                thumbsNote.Text = isRu ? "Проверяю…" : "Testing…";
                string report = await Task.Run(() => FileAssociation.RunThumbnailSelfTest(isRu));
                thumbsNote.Text = report;

                // Компоненту нужна среда .NET 10 Desktop Runtime: если её нет, Windows
                // не может загрузить обработчик — предлагаем сразу скачать установщик.
                bool needsRuntime = report.Contains("0x80008083") || report.Contains("0x80008096");
                if (!needsRuntime) return;

                var answer = MessageBox.Show(this,
                    isRu
                        ? "Компонент миниатюр не запустился: не установлена среда .NET 10 Desktop Runtime.\n\nОткрыть страницу загрузки с сайта Microsoft?"
                        : "The thumbnail component did not start: the .NET 10 Desktop Runtime is not installed.\n\nOpen the Microsoft download page?",
                    isRu ? "Нужен .NET 10" : ".NET 10 is required",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://dotnet.microsoft.com/en-us/download/dotnet/10.0",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    thumbsNote.Text = (isRu ? "Не удалось открыть ссылку: " : "Could not open the link: ") + ex.Message;
                }
            });
            btnThumbTest.Margin = new Thickness(0, 6, 0, 0);
            var btnThumbLog = CreateModernButton(isRu ? "Открыть журнал миниатюр" : "Open the thumbnail log", assocBg, textBrush, borderBrush, 32, new CornerRadius(6), () =>
            {
                try
                {
                    if (File.Exists(FileAssociation.ThumbnailLogPath))
                        Process.Start(new ProcessStartInfo { FileName = FileAssociation.ThumbnailLogPath, UseShellExecute = true });
                    else
                        thumbsNote.Text = isRu ? "Журнала ещё нет: к компоненту пока никто не обращался." : "There is no log yet: nothing has used the component.";
                }
                catch (Exception logEx)
                {
                    thumbsNote.Text = (isRu ? "Не получилось: " : "Failed: ") + logEx.Message;
                }
            });
            btnThumbLog.Margin = new Thickness(0, 6, 0, 0);
            thumbsStack.Children.Add(btnThumbOn);
            thumbsStack.Children.Add(btnThumbOff);
            thumbsStack.Children.Add(btnThumbTest);
            thumbsStack.Children.Add(btnThumbLog);
            thumbsStack.Children.Add(btnRestartExplorer);
            thumbsStack.Children.Add(thumbsNote);
            tabMain.Children.Add(cardThumbs);

            // ---- папки игр: из них берётся список архивов в режиме «Архивы»
            var cardGames = CreateCard(cardBrush, borderBrush, textBrush, isRu ? "Директории Call of Duty" : "Call of Duty folders");
            var gamesStack = (StackPanel)cardGames.Child;
            gamesStack.Children.Add(MakeNote(isRu
                ? "Корневая папка каждой игры. В режиме «Архивы» стрелка в поле имени покажет архивы из папки выбранной версии. Если поле пустое, папка ищется сама."
                : "The root folder of each game. In the \"Archives\" mode the arrow in the name field lists the archives from the folder of the selected version. If a field is empty, the folder is searched for automatically."));

            // индекс = индекс версии в списке: 0 Black Ops 2, 1 MW3, 2 Black Ops, 3 MW2, 4 World at War, 5 MW1 (CoD4), 6 Call of Duty 2
            var gameNames = new[] { "Black Ops 2", "Modern Warfare 3", "Black Ops", "Modern Warfare 2", "World at War", "Modern Warfare 1 (CoD4)", "Call of Duty 2" };
            var gameBoxes = new TextBox[GameLocator.GameCount];
            string[] currentDirs = settings.GameDirs ?? new string[GameLocator.GameCount];

            var gameCardBg = new SolidColorBrush(isDark ? Color.FromRgb(37, 37, 41) : Color.FromRgb(244, 246, 249));
            var gameInputBg = new SolidColorBrush(isDark ? Color.FromRgb(22, 22, 25) : Colors.White);
            var gameBtnBg = new SolidColorBrush(isDark ? Color.FromRgb(58, 58, 62) : Color.FromRgb(230, 233, 238));
            var gameMuted = new SolidColorBrush(isDark ? Color.FromRgb(140, 140, 150) : Color.FromRgb(110, 115, 125));
            var gameOk = new SolidColorBrush(isDark ? Color.FromRgb(61, 184, 106) : Color.FromRgb(30, 140, 70));
            var gameErr = new SolidColorBrush(isDark ? Color.FromRgb(229, 83, 75) : Color.FromRgb(200, 50, 45));
            var gameAccent = new SolidColorBrush(Color.FromRgb(0, 122, 204));

            foreach (int gameIndex in new[] { 6, 5, 4, 3, 2, 1, 0 })
            {
                int idx = gameIndex;

                // карточка игры: название и статус сверху, под ними поле пути с кнопкой выбора папки
                var gameCard = new Border
                {
                    Background = gameCardBg,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 10, 12, 12),
                    Margin = new Thickness(0, 8, 0, 0)
                };
                var cardStack = new StackPanel();

                var head = new Grid();
                var gameTitle = new TextBlock { Text = gameNames[idx], Foreground = textBrush, FontWeight = FontWeights.SemiBold, FontSize = 13 };
                var gameStatus = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, FontSize = 11.5, Foreground = gameMuted };
                head.Children.Add(gameTitle);
                head.Children.Add(gameStatus);
                cardStack.Children.Add(head);

                var inputRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var inputWrap = new Border
                {
                    Background = gameInputBg,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8)
                };
                var inputGrid = new Grid();
                var box = new TextBox
                {
                    Text = idx < currentDirs.Length ? (currentDirs[idx] ?? "") : "",
                    Background = Brushes.Transparent,
                    Foreground = textBrush,
                    CaretBrush = textBrush,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(10, 8, 10, 8),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                var placeholder = new TextBlock
                {
                    Text = isRu ? "Авто — папка найдётся сама" : "Auto — the folder is found automatically",
                    Foreground = gameMuted,
                    IsHitTestVisible = false,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(11, 0, 10, 0)
                };
                inputGrid.Children.Add(placeholder);
                inputGrid.Children.Add(box);
                inputWrap.Child = inputGrid;
                gameBoxes[idx] = box;

                box.GotKeyboardFocus += (s, e) => inputWrap.BorderBrush = gameAccent;
                box.LostKeyboardFocus += (s, e) => inputWrap.BorderBrush = borderBrush;

                Action refreshStatus = () =>
                {
                    string typed = box.Text.Trim();
                    placeholder.Visibility = typed.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (typed.Length == 0)
                    {
                        gameStatus.Text = isRu ? "Авто" : "Auto";
                        gameStatus.Foreground = gameMuted;
                    }
                    else if (Directory.Exists(typed))
                    {
                        gameStatus.Text = isRu ? "✓ Папка найдена" : "✓ Folder found";
                        gameStatus.Foreground = gameOk;
                    }
                    else
                    {
                        gameStatus.Text = isRu ? "✕ Такой папки нет" : "✕ No such folder";
                        gameStatus.Foreground = gameErr;
                    }
                };
                box.TextChanged += (s, e) => refreshStatus();
                refreshStatus();

                var browse = CreateModernButton("…", gameBtnBg, textBrush, borderBrush, 34, new CornerRadius(8), () =>
                {
                    var dlg = new OpenFolderDialog();
                    if (dlg.ShowDialog(this) == true) box.Text = dlg.FolderName;
                });
                browse.Width = 42;
                browse.Margin = new Thickness(8, 0, 0, 0);

                Grid.SetColumn(inputWrap, 0);
                Grid.SetColumn(browse, 1);
                inputRow.Children.Add(inputWrap);
                inputRow.Children.Add(browse);
                cardStack.Children.Add(inputRow);

                gameCard.Child = cardStack;
                gamesStack.Children.Add(gameCard);
            }

            var gamesNote = MakeNote("");
            var btnFindGames = CreateModernButton(
                isRu ? "Найти игры автоматически" : "Find the games automatically",
                gameBtnBg, textBrush, borderBrush, 32, new CornerRadius(6),
                () =>
                {
                    string?[] found = GameLocator.DetectAll();
                    int count = 0;
                    for (int gi = 0; gi < found.Length; gi++)
                    {
                        if (string.IsNullOrEmpty(found[gi])) continue;
                        gameBoxes[gi].Text = found[gi]!;
                        count++;
                    }
                    gamesNote.Text = isRu
                        ? $"Найдено игр: {count} из {found.Length}. Остальные укажите вручную. Не забудьте нажать «Сохранить»."
                        : $"Games found: {count} of {found.Length}. Set the others by hand. Don't forget to press Save.";
                });
            btnFindGames.Margin = new Thickness(0, 12, 0, 0);
            gamesStack.Children.Add(btnFindGames);
            gamesStack.Children.Add(gamesNote);
            tabMain.Children.Add(cardGames);

            bool resetColumns = false;
            var cardTable = CreateCard(cardBrush, borderBrush, textBrush, isRu ? "Таблица" : "Table");
            Button? resetBtn = null;
            resetBtn = CreateModernButton(
                isRu ? "Сбросить расположение колонок" : "Reset column layout",
                new SolidColorBrush(isDark ? Color.FromRgb(58, 58, 62) : Color.FromRgb(230, 233, 238)),
                textBrush, borderBrush, 32, new CornerRadius(6),
                () =>
                {
                    resetColumns = true;
                    if (resetBtn != null) resetBtn.Content = isRu ? "Колонки сбросятся после «Сохранить»" : "Columns will reset after Save";
                });
            ((StackPanel)cardTable.Child).Children.Add(resetBtn);
            tabMain.Children.Add(cardTable);

            // ---- горячие клавиши: каждая настройка — одна строка «текст + кнопка»
            Key hotkey = settings.PreviewHotkey;
            string previewBinding = string.IsNullOrEmpty(settings.PreviewHotkeyBinding)
                ? HotkeyBinding.KeyToken(settings.PreviewHotkey)
                : settings.PreviewHotkeyBinding;
            bool previewEnabled = settings.PreviewHotkeyEnabled;
            string replaceHotkey = settings.ReplaceHotkey;
            bool replaceEnabled = settings.ReplaceHotkeyEnabled;
            if (previewEnabled && replaceEnabled) replaceEnabled = false; // одновременно только одна горячая кнопка
            bool capturingKey = false;
            bool capturingReplace = false;
            var cardKeys = CreateCard(cardBrush, borderBrush, textBrush, isRu ? "Горячие клавиши" : "Hotkeys");
            var keysStack = (StackPanel)cardKeys.Child;
            var keyButtonBrush = new SolidColorBrush(isDark ? Color.FromRgb(58, 58, 62) : Color.FromRgb(230, 233, 238));

            StackPanel KeyRow(string text)
            {
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                row.Children.Add(new TextBlock
                {
                    Text = text,
                    Foreground = textBrush,
                    Opacity = 0.8,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                });
                return row;
            }

            var previewRow = KeyRow(isRu ? "Открыть и закрыть предпросмотр" : "Open and close the preview");
            Button? keyButton = null;
            keyButton = CreateModernButton(
                HotkeyBinding.Display(previewBinding, isRu), keyButtonBrush, textBrush, borderBrush, 32, new CornerRadius(6),
                () =>
                {
                    capturingKey = true;
                    capturingReplace = false;
                    if (keyButton != null) keyButton.Content = isRu ? "Нажмите клавишу или кнопку мыши…  (Esc — отмена)" : "Press a key or mouse button…  (Esc to cancel)";
                });
            keyButton.MinWidth = 64;
            previewRow.Children.Add(keyButton);

            string hotkeyCheckText = isRu ? "открывать двойным кликом для ЛКМ" : "open on double-click for LMB";
            bool syncing = false;
            CheckBox? previewCheck = null;
            CheckBox? replaceCheck = null;

            void SyncHotkeyChecks()
            {
                if (syncing) return;
                syncing = true;
                try
                {
                    if (previewCheck != null)
                    {
                        previewCheck.IsChecked = previewEnabled;
                        previewCheck.IsEnabled = !replaceEnabled;
                        previewCheck.Opacity = replaceEnabled ? 0.45 : 1.0;
                    }
                    if (replaceCheck != null)
                    {
                        replaceCheck.IsChecked = replaceEnabled;
                        replaceCheck.IsEnabled = !previewEnabled;
                        replaceCheck.Opacity = previewEnabled ? 0.45 : 1.0;
                    }
                }
                finally { syncing = false; }
            }

            previewCheck = MakeCheck(hotkeyCheckText, previewEnabled);
            previewCheck.VerticalAlignment = VerticalAlignment.Center;
            previewCheck.Margin = new Thickness(10, 0, 0, 0);
            previewCheck.Checked += (_, _) => { if (syncing) return; previewEnabled = true; replaceEnabled = false; SyncHotkeyChecks(); };
            previewCheck.Unchecked += (_, _) => { if (syncing) return; previewEnabled = false; SyncHotkeyChecks(); };
            previewRow.Children.Add(previewCheck);
            keysStack.Children.Add(previewRow);

            var replaceRow = KeyRow(isRu ? "Заменить файл в архиве" : "Replace a file in the archive");

            Button? replaceButton = null;
            replaceButton = CreateModernButton(
                HotkeyBinding.Display(replaceHotkey, isRu), keyButtonBrush, textBrush, borderBrush, 32, new CornerRadius(6),
                () =>
                {
                    capturingReplace = true;
                    capturingKey = false;
                    if (replaceButton != null)
                        replaceButton.Content = isRu ? "Нажмите клавишу или кнопку мыши…  (Esc — отмена)"
                                                    : "Press a key or mouse button…  (Esc to cancel)";
                });
            replaceButton.MinWidth = 64;
            replaceRow.Children.Add(replaceButton);

            replaceCheck = MakeCheck(hotkeyCheckText, replaceEnabled);
            replaceCheck.VerticalAlignment = VerticalAlignment.Center;
            replaceCheck.Margin = new Thickness(10, 0, 0, 0);
            replaceCheck.Checked += (_, _) => { if (syncing) return; replaceEnabled = true; previewEnabled = false; SyncHotkeyChecks(); };
            replaceCheck.Unchecked += (_, _) => { if (syncing) return; replaceEnabled = false; SyncHotkeyChecks(); };
            replaceRow.Children.Add(replaceCheck);
            keysStack.Children.Add(replaceRow);
            SyncHotkeyChecks();

            keysStack.Children.Add(new TextBlock
            {
                Text = isRu ? "ЛКМ срабатывает двойным нажатием (чтобы не мешать выделению), остальные кнопки — одним."
                            : "LMB works on double-click (so selection still works); other buttons work on a single press.",
                Foreground = textBrush,
                Opacity = 0.55,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5,
                Margin = new Thickness(0, 0, 0, 0)
            });

            PreviewKeyDown += (_, e) =>
            {
                if (capturingKey)
                {
                    Key pressed = e.Key == Key.System ? e.SystemKey : e.Key;
                    e.Handled = true;
                    if (pressed is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
                        return; // одни только модификаторы не подходят — ждём обычную клавишу

                    capturingKey = false;
                    if (pressed != Key.Escape)
                    {
                        hotkey = pressed;
                        previewBinding = HotkeyBinding.KeyToken(pressed);
                    }
                    if (keyButton != null) keyButton.Content = HotkeyBinding.Display(previewBinding, isRu);
                    return;
                }

                if (!capturingReplace) return;

                Key pressedReplace = e.Key == Key.System ? e.SystemKey : e.Key;
                e.Handled = true;
                if (pressedReplace == Key.Escape)
                {
                    capturingReplace = false;
                    if (replaceButton != null) replaceButton.Content = HotkeyBinding.Display(replaceHotkey, isRu);
                    return;
                }
                if (pressedReplace is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
                    return;

                replaceHotkey = HotkeyBinding.KeyToken(pressedReplace);
                capturingReplace = false;
                if (replaceButton != null) replaceButton.Content = HotkeyBinding.Display(replaceHotkey, isRu);
            };

            PreviewMouseDown += (_, e) =>
            {
                if (capturingKey)
                {
                    e.Handled = true;
                    previewBinding = HotkeyBinding.MouseToken(e.ChangedButton);
                    capturingKey = false;
                    if (keyButton != null) keyButton.Content = HotkeyBinding.Display(previewBinding, isRu);
                    return;
                }

                if (!capturingReplace) return;
                e.Handled = true;
                replaceHotkey = HotkeyBinding.MouseToken(e.ChangedButton);
                capturingReplace = false;
                if (replaceButton != null) replaceButton.Content = HotkeyBinding.Display(replaceHotkey, isRu);
            };

            PreviewMouseWheel += (_, e) =>
            {
                if (capturingKey)
                {
                    e.Handled = true;
                    previewBinding = e.Delta > 0 ? "Mouse:WheelUp" : "Mouse:WheelDown";
                    capturingKey = false;
                    if (keyButton != null) keyButton.Content = HotkeyBinding.Display(previewBinding, isRu);
                    return;
                }

                if (!capturingReplace) return;
                e.Handled = true;
                replaceHotkey = e.Delta > 0 ? "Mouse:WheelUp" : "Mouse:WheelDown";
                capturingReplace = false;
                if (replaceButton != null) replaceButton.Content = HotkeyBinding.Display(replaceHotkey, isRu);
            };

            tabMain.Children.Add(cardKeys);

            // Кнопка сохранения
            var saveBtn = CreateModernButton(isRu ? "Сохранить" : "Save", new SolidColorBrush(Color.FromRgb(0, 122, 204)), Brushes.White, null, 36, new CornerRadius(6), () =>
            {
                settings.IsRussian = langSeg.SelectedIndex == 0;
                settings.IsDarkTheme = themeSeg.SelectedIndex == 0;
                var gameDirsToSave = new string[GameLocator.GameCount];
                for (int gi = 0; gi < gameDirsToSave.Length; gi++)
                {
                    // путь приводим к настоящему регистру (C:\Program Files (x86)\Steam\...), если папка существует
                    string typedDir = gameBoxes[gi].Text.Trim();
                    gameDirsToSave[gi] = typedDir.Length > 0 && Directory.Exists(typedDir) ? GameLocator.NormalizePath(typedDir) : typedDir;
                }
                settings.GameDirs = gameDirsToSave;
                settings.PerformanceMode = cpuMode.SelectedIndex;
                settings.EnableGpu = chkGpu.IsChecked == true;
                settings.FastNvme = chkNvme.IsChecked == true;
                settings.FastDxt = chkFastDxt.IsChecked == true;
                settings.IwiMipmaps = chkIwiMips.IsChecked == true;
                settings.IwiFormatMode = iwiFormatSegments.SelectedIndex;
                settings.DdsMipmaps = chkDdsMips.IsChecked == true;
                settings.DdsFormat = ddsMap[Math.Clamp(ddsSegments.SelectedIndex, 0, ddsMap.Length - 1)];
                settings.NoMipsForSmall = chkNoMipsSmall.IsChecked == true;
                settings.TgaRle = chkTgaRle.IsChecked == true;
                settings.PngLevel = pngLevel.SelectedIndex;
                settings.TgaDepth = tgaDepth.SelectedIndex;
                settings.PngDepth = pngDepth.SelectedIndex;
                settings.PreviewHotkey = hotkey;
                settings.PreviewHotkeyBinding = previewBinding;
                settings.PreviewHotkeyEnabled = previewEnabled;
                settings.ReplaceHotkey = replaceHotkey;
                settings.ReplaceHotkeyEnabled = replaceEnabled;
                settings.PlaySoundOnFinish = chkSound.IsChecked == true;
                settings.FlashTaskbar = chkFlash.IsChecked == true;
                settings.AutoConvertOnDrop = chkAutoDrop.IsChecked == true;
                settings.AutoConvertOnOpen = chkOpenConvert.IsChecked == true;
                settings.PreviewOnOpen = chkOpenPreview.IsChecked == true;
                settings.DebugMode = chkDebugSetting.IsChecked == true;
                if (resetColumns) settings.ColumnLayout = new List<ColumnState>();
                CloseWithAnimation(true);
            });
            saveBtn.Margin = new Thickness(20, 10, 20, 20);
            Grid.SetRow(saveBtn, 3);
            mainGrid.Children.Add(saveBtn);

            mainGrid.Children.Add(bodyScroll);

            // мягкий переход фона под вкладками и над кнопкой «Сохранить»
            var fadeTop = EdgeFade.Top(panelBrush.Color, 32);
            var fadeBottom = EdgeFade.Bottom(panelBrush.Color, 32);
            Grid.SetRow(fadeTop, 2);
            Grid.SetRow(fadeBottom, 2);
            mainGrid.Children.Add(fadeTop);
            mainGrid.Children.Add(fadeBottom);
            mainBorder.Child = mainGrid;

            Content = mainBorder;
        }

        private Border CreateCard(Brush bg, Brush border, Brush textBrush, string title)
        {
            var b = new Border { Background = bg, CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10), BorderBrush = border, BorderThickness = new Thickness(1) };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, Foreground = textBrush, Margin = new Thickness(0, 0, 0, 8) });
            b.Child = sp;
            return b;
        }
    }

    // ================= ОКНО ИТОГОВ (С ЛОКАЛИЗАЦИЕЙ) =================
    public class ColumnState
    {
        public string Key { get; set; } = "";
        public double Width { get; set; }
        public string Unit { get; set; } = "Pixel"; // "Pixel" или "Star"
        public int Index { get; set; }
    }

    public class AppSettings
    {
        public bool IsRussian { get; set; } = true;
        public bool IsDarkTheme { get; set; } = true;
        public int PerformanceMode { get; set; } = 0;
        public bool EnableGpu { get; set; } = false; // OpenCL на мелких текстурах чаще тормозит, чем ускоряет
        public bool FastNvme { get; set; } = true;
        public bool FastDxt { get; set; } = false;
        public bool IwiMipmaps { get; set; } = true;   // мипмапы в IWI (игре нужны)
        public int IwiFormatMode { get; set; } = 0;    // сжатие IWI: 0 = авто, 1 = DXT5, 2 = DXT1
        public bool DdsMipmaps { get; set; } = false;  // мипмапы в DDS
        public int DdsFormat { get; set; } = 0;        // 0 = авто DXT1/DXT5, 1 = DXT5, 2 = без сжатия, 3 = BC5, 4 = BC7, 5 = автопрофиль под игру
        public bool TgaRle { get; set; } = true;       // TGA со сжатием RLE
        public int PngLevel { get; set; } = 1;         // 0 = быстро, 1 = обычно, 2 = минимальный размер
        public bool PlaySoundOnFinish { get; set; } = true;
        public bool FlashTaskbar { get; set; } = true;
        public bool AutoConvertOnDrop { get; set; } = false; // сразу конвертировать после перетаскивания
        public int CodVersionIndex { get; set; } = 4;
        public bool IsAutoMode { get; set; } = true;
        public int AutoExtractFormat { get; set; } = 0;
        public int FixedFormatIndex { get; set; } = 0;
        public bool SaveToOriginal { get; set; } = true;
        public string CustomSavePath { get; set; } = "";
        public bool ClearLogsAfter { get; set; } = false;
        public int DoneMode { get; set; } = 0; // 0 = авто, 1 = пропускать готовые, 2 = делать заново
        public bool DebugMode { get; set; } = false; // подробный журнал для диагностики
        public List<ColumnState> ColumnLayout { get; set; } = new(); // ширина и порядок колонок таблицы (пусто = по умолчанию)
        public int TgaDepth { get; set; } = 0;           // 0 = авто (по альфе), 1 = 32 бита, 2 = 24 бита
        public int PngDepth { get; set; } = 0;           // 0 = авто, 1 = 32 бита, 2 = 24 бита, 3 = 8 бит (серый)
        public bool NoMipsForSmall { get; set; } = false; // не создавать мипмапы DDS для мелких текстур и интерфейса
        public bool AutoConvertOnOpen { get; set; } = false; // файл открыт двойным кликом / «Открыть с помощью»: сразу конвертировать
        public bool PreviewOnOpen { get; set; } = true;      // ... и показать предпросмотр

        // окно «Упаковать»
        public string[] GameDirs { get; set; } = new string[7]; // папки игр по индексу версии: 0 BO2, 1 MW3, 2 BO1, 3 MW2, 4 WaW, 5 MW1, 6 CoD2
        public bool ArchiveMode { get; set; } = false;       // последний режим: false = конвертер, true = архивы
        public int PackVersionIndex { get; set; } = 4;       // версия игры для IWD
        public int PackFormat { get; set; } = 1;             // 0 = IPAK, 1 = IWD
        public int PackCompression { get; set; } = 1;        // 0 = максимально, 1 = обычно, 2 = без сжатия
        public string PackArchiveName { get; set; } = "";    // пусто = iw_collect / ipak_collect
        public bool PackKeepOriginals { get; set; } = false; // при замене сохранять прежний файл из архива с припиской Original
        public bool PackReplaceExisting { get; set; } = true; // заменять файлы с тем же именем (иначе пропускать)
        public bool PackSaveNearby { get; set; } = true;
        public string PackCustomPath { get; set; } = "";
        public Key PreviewHotkey { get; set; } = Key.P;   // клавиша открытия и закрытия предпросмотра
        public string PreviewHotkeyBinding { get; set; } = ""; // клавиша/кнопка мыши; пусто = PreviewHotkey
        public bool PreviewHotkeyEnabled { get; set; } = true; // включена ли горячая кнопка предпросмотра
        public string ReplaceHotkey { get; set; } = HotkeyBinding.DefaultReplace; // клавиша/кнопка мыши для окна замены
        public bool ReplaceHotkeyEnabled { get; set; } = true;                   // включена ли горячая кнопка замены
    }

    public class TextureFile : INotifyPropertyChanged
    {
        public string FileName { get; set; } = "";
        public string FileType { get; set; } = "";
        public long FileSizeBytes { get; set; } = 0;
        public string FileSize { get; set; } = "";
        public string FullPath { get; set; } = "";   // для файла из архива — «виртуальный путь»: архив>путь внутри

        public string? ArchivePath { get; set; }     // архив, в котором лежит файл (null = обычный файл на диске)
        public string? EntryPath { get; set; }       // путь файла внутри архива
        public bool IsArchiveEntry => ArchivePath != null;

        /// <summary>Внешний файл, которым надо заменить содержимое записи архива (null = без замены).</summary>
        public string? ReplacePath { get; set; }
        public bool HasReplace => !string.IsNullOrWhiteSpace(ReplacePath) && File.Exists(ReplacePath);
        public bool PackStatus { get; set; }         // статус в списке выставила упаковка (а не конвертер)

        public string? ConvertedSignature { get; set; } // с какими настройками файл был сконвертирован
        private ConvState _state = ConvState.Pending;
        public ConvState State
        {
            get => _state;
            set
            {
                if (_state == value) return;
                _state = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
            }
        }

        public void SetState(ConvState state, string text)
        {
            State = state;
            Status = text;
        }

        private string _status = "В очереди";
        public string Status
        {
            get => _status;
            set
            {
                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public enum ConvState { Pending, Done, Error }

    /// <summary>ObservableCollection с массовым добавлением/удалением (одно уведомление вместо тысяч).</summary>
    public class BulkObservableCollection<T> : ObservableCollection<T>
    {
        public void AddRange(IEnumerable<T> items)
        {
            CheckReentrancy();
            int before = Items.Count;
            foreach (var item in items) Items.Add(item);
            if (Items.Count != before) RaiseReset();
        }

        public void RemoveRange(IEnumerable<T> items)
        {
            var set = new HashSet<T>(items);
            if (set.Count == 0) return;
            CheckReentrancy();
            var keep = Items.Where(x => !set.Contains(x)).ToList();
            Items.Clear();
            foreach (var x in keep) Items.Add(x);
            RaiseReset();
        }

        private void RaiseReset()
        {
            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}