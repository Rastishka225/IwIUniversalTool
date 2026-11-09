using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace IwIUniversalTool
{
    // Режим «Архивы» главного окна: упаковка текстур в IWD (старые Call of Duty) или IPAK (Black Ops 2).
    // Плюс открытие файлов двойным кликом / «Открыть с помощью».
    public partial class MainWindow
    {
        private bool _archiveMode;
        private bool _packLoading = true;   // пока окно собирается, переключатели ничего не делают
        private bool _packBusy;
        private int _packVersionIndex = 4;
        private CancellationTokenSource? _packCts;
        private string _lastArchivePath = "";
        private int _packDone;

        private sealed class PackItem
        {
            public TextureFile File = null!;
            public string Name = "";
        }

        private sealed class PackPrepared
        {
            public byte[]? Iwi;
            public string? Error;
            public string? Warning;
        }

        private sealed class PackSummary
        {
            public int Added;
            public int Replaced;
            public int Skipped;
            public int Failed;
            public int Originals;
            public bool Completed;
        }

        /// <summary>Цвет предупреждений в журнале: спокойный янтарный вместо ярко-жёлтого.</summary>
        internal static Color WarnColor(bool isDark) => isDark ? Color.FromRgb(224, 168, 96) : Color.FromRgb(168, 104, 16);

        private void LogWarn(string message) => AppendLog(message, WarnColor(_settings.IsDarkTheme), false);

        private string PackT(string ru, string en) => _settings.IsRussian ? ru : en;

        private void PackLog(string message, int level)
        {
            string line = PackT("[УПАКОВКА] ", "[PACK] ") + message;
            switch (level)
            {
                case 1: LogSuccess(line); break;
                case 2: LogWarn(line); break;
                case 3: LogError(line); break;
                default: LogInfo(line); break;
            }
        }

        /// <summary>Сколько потоков использовать (по режиму нагрузки из настроек).</summary>
        private int GetMaxParallelism()
        {
            int cores = Environment.ProcessorCount;
            return _settings.PerformanceMode switch
            {
                0 => Math.Max(1, (int)Math.Round(cores * 0.80)),
                1 => Math.Max(1, (int)Math.Round(cores * 0.40)),
                _ => Math.Max(1, (int)Math.Round(cores * 0.15))
            };
        }

        /// <summary>
        /// Функция «байты исходной картинки → готовый IWI» под выбранную версию Call of Duty.
        /// Используется то же преобразование, что и в обычной конвертации (с теми же настройками формата и мипмапов).
        /// Параметры: байты файла, расширение с точкой (.png), имя файла, полный путь.
        /// </summary>
        private Func<byte[], string, string, string, byte[]> CreateIwiConverter(int codVerIndex)
        {
            try { ImageMagick.ResourceLimits.Thread = 1; } catch { }

            var job = new ConvertJob(
                codVerIndex, true, ".png", ".iwi", true, "",
                _settings.FastDxt, _settings.FastNvme,
                _settings.IwiMipmaps, _settings.IwiFormatMode, _settings.DdsMipmaps, _settings.DdsFormat, _settings.TgaRle, _settings.PngLevel,
                _settings.TgaDepth, _settings.PngDepth, _settings.NoMipsForSmall,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new ArchiveExtractor(Array.Empty<TextureFile>(), CancellationToken.None));

            return (src, inExt, fileName, fullPath) => Transcode(src, inExt, ".iwi", job, fileName, fullPath);
        }

        // ============================================================ настройка интерфейса

        private void InitPackUi()
        {
            // версия игры определяет формат: Black Ops 2 = IPAK, остальные = IWD
            _packVersionIndex = _settings.PackFormat == 0 ? 0 : _settings.PackVersionIndex;
            if (_packVersionIndex < 0 || _packVersionIndex > 6) _packVersionIndex = 4;
            CmbPackVersion.SelectedIndex = _packVersionIndex;
            (_packVersionIndex == 0 ? RbFmtIpak : RbFmtIwd).IsChecked = true;
            TxtArchiveName.Text = _settings.PackArchiveName ?? "";

            if (_settings.PackCompression == 0) RbCompMax.IsChecked = true;
            else if (_settings.PackCompression == 2) RbCompNone.IsChecked = true;
            else RbCompNormal.IsChecked = true;

            ChkPackKeepOriginal.IsChecked = _settings.PackKeepOriginals;
            ChkPackReplace.IsChecked = _settings.PackReplaceExisting;
            (_settings.PackSaveNearby ? RbPackSaveNearby : RbPackSaveCustom).IsChecked = true;
            TxtPackCustomPath.Text = _settings.PackCustomPath ?? "";
            PackSaveLocation_Changed(null, null);

            _packLoading = false;
            ApplyFormatLock();
            UpdatePackFormatUi();
            UpdatePackNameHint();
            ApplyPackLanguage(_settings.IsRussian);

            // список архивов закрывается при клике в любом другом месте, при смене окна и при перемещении окна
            PreviewMouseDown += ClosePopupOnOutsideClick;
            Deactivated += (_, _) => PopArchives.IsOpen = false;
            LocationChanged += (_, _) => PopArchives.IsOpen = false;

            (_settings.ArchiveMode ? RbAppArchive : RbAppConvert).IsChecked = true;
            ApplyAppMode(_settings.ArchiveMode);
        }

        private void SavePackSettings()
        {
            if (_packLoading) return;
            _settings.ArchiveMode = _archiveMode;
            _settings.PackFormat = RbFmtIpak.IsChecked == true ? 0 : 1;
            _settings.PackVersionIndex = CmbPackVersion.SelectedIndex >= 0 ? CmbPackVersion.SelectedIndex : _packVersionIndex;
            _settings.PackArchiveName = TxtArchiveName.Text.Trim();
            _settings.PackCompression = SelectedCompression();
            _settings.PackKeepOriginals = ChkPackKeepOriginal.IsChecked == true;
            _settings.PackReplaceExisting = ChkPackReplace.IsChecked == true;
            _settings.PackSaveNearby = RbPackSaveNearby.IsChecked == true;
            _settings.PackCustomPath = TxtPackCustomPath.Text;
        }

        private void ApplyPackLanguage(bool ru)
        {
            RbAppConvert.Content = ru ? "Конвертер" : "Converter";
            RbAppArchive.Content = ru ? "Архивы" : "Archives";
            RbAppConvert.ToolTip = ru ? "Конвертация текстур между форматами" : "Convert textures between formats";
            RbAppArchive.ToolTip = ru ? "Упаковка текстур в архив IWD или IPAK" : "Pack textures into an IWD or IPAK archive";

            LblPackFormat.Text = ru ? "ФОРМАТ АРХИВА" : "ARCHIVE FORMAT";
            LblPackName.Text = ru ? "ИМЯ АРХИВА" : "ARCHIVE NAME";
            LblPackVersion.Text = ru ? "ВЕРСИЯ CALL OF DUTY" : "CALL OF DUTY VERSION";
            LblPackCompression.Text = ru ? "СЖАТИЕ АРХИВА" : "ARCHIVE COMPRESSION";
            RbCompMax.Content = ru ? "Максимум" : "Maximum";
            RbCompNormal.Content = ru ? "Обычно" : "Normal";
            RbCompNone.Content = ru ? "Без сжатия" : "None";
            LblPackOptions.Text = ru ? "ПАРАМЕТРЫ" : "OPTIONS";
            ChkPackKeepOriginal.Content = ru ? "Сохранять оригинал при замене" : "Keep the original when replacing";
            ChkPackKeepOriginal.ToolTip = ru
                ? "Когда файл в архиве заменяется, прежний файл из архива сохраняется в папку «имя_архива_originals» рядом с архивом с припиской _Original (например, name_Original.iwi)."
                : "When a file in the archive is replaced, the previous file from the archive is saved into an \"archive_name_originals\" folder next to the archive with an _Original suffix (for example name_Original.iwi).";
            ChkPackReplace.Content = ru ? "Заменять файлы с тем же именем" : "Replace files with the same name";
            ChkPackReplace.ToolTip = ru
                ? "Если архив уже есть и в нём лежит файл с таким же именем — он заменяется. Если галочка снята — такой файл пропускается."
                : "If the archive already exists and contains a file with the same name, it is replaced. If unchecked, that file is skipped.";
            LblPackSaveTo.Text = ru ? "КУДА СОХРАНЯТЬ" : "SAVE TO";
            RbPackSaveNearby.Content = ru ? "Рядом" : "Nearby";
            RbPackSaveCustom.Content = ru ? "Другая папка" : "Another folder";

            BtnPackAll.Content = ru ? "УПАКОВАТЬ" : "PACK";
            BtnPackSelected.Content = ru ? "Упаковать выбранные" : "Pack selected";
            BtnPackShowFolder.Content = ru ? "Открыть папку с результатом" : "Open the output folder";
            BtnArchiveContents.ToolTip = ru
                ? "Добавить текстуры этого архива в список файлов (дальше с ними можно смотреть превью, конвертировать и менять)."
                : "Add the textures of this archive to the file list (then you can preview, convert and replace them).";
            BtnPackRecompress.Content = ru ? "Сжать архив" : "Compress archive";
            BtnPackRecompress.ToolTip = ru
                ? "Пересжимает выбранный архив (выберите его стрелкой в поле «Имя архива») выбранным уровнем: Максимум, Обычно или Без сжатия. Файлы внутри не меняются."
                : "Recompresses the selected archive (pick it with the arrow in the name field) with the chosen level: Maximum, Normal or None. The files inside are not changed.";
            BtnPackStop.Content = ru ? "Остановить" : "Stop";

            TxtSettingsHeader.Text = _archiveMode
                ? (ru ? "Упаковка в IWD / IPAK" : "Pack to IWD / IPAK")
                : (ru ? "Конвертер текстур CoD" : "CoD texture converter");

            if (!_packLoading)
            {
                UpdatePackFormatUi();
                UpdatePackNameHint();
            }
        }

        private void AppMode_Changed(object? sender, RoutedEventArgs? e)
        {
            if (_packLoading || PnlArchiveSettings == null) return;
            ApplyAppMode(RbAppArchive.IsChecked == true);
        }

        private void ApplyAppMode(bool archive)
        {
            _archiveMode = archive;
            PopArchives.IsOpen = false;
            PnlConvertSettings.Visibility = archive ? Visibility.Collapsed : Visibility.Visible;
            PnlConvertBottom.Visibility = archive ? Visibility.Collapsed : Visibility.Visible;
            PnlArchiveSettings.Visibility = archive ? Visibility.Visible : Visibility.Collapsed;
            PnlArchiveBottom.Visibility = archive ? Visibility.Visible : Visibility.Collapsed;

            bool ru = _settings.IsRussian;
            TxtSettingsHeader.Text = archive
                ? (ru ? "Упаковка в IWD / IPAK" : "Pack to IWD / IPAK")
                : (ru ? "Конвертер текстур CoD" : "CoD texture converter");

            if (!archive)
            {
                // статусы упаковки в режиме конвертера не нужны: иначе файлы выглядели бы уже сконвертированными
                foreach (var f in FilesList)
                {
                    if (!f.PackStatus) continue;
                    f.PackStatus = false;
                    f.SetState(ConvState.Pending, Localize(ConvState.Pending));
                }
            }
        }

        private void PackFormat_Changed(object sender, RoutedEventArgs e)
        {
            if (_packLoading) return;
            UpdatePackFormatUi();
            UpdatePackNameHint();
            ClearPickedArchive();
            if (PopArchives.IsOpen) _ = RefreshArchivePopupAsync();
        }

        private void PackCompression_Changed(object sender, RoutedEventArgs e)
        {
            if (_packLoading) return;
            UpdatePackCompressionHint();
        }

        private int SelectedCompression() => RbCompMax.IsChecked == true ? 0 : (RbCompNone.IsChecked == true ? 2 : 1);

        private void UpdatePackFormatUi()
        {
            if (BrdPackCompression == null) return;
            bool ipak = RbFmtIpak.IsChecked == true;
            TxtPackExt.Text = ipak ? ".ipak" : ".iwd";
            TxtNamePlaceholder.Text = ipak ? "ipak_collect" : "iw_collect";
            UpdatePackCompressionHint();
        }

        /// <summary>IPAK бывает только в Black Ops 2: для неё включён IPAK, а IWD заблокирован; для остальных игр наоборот.</summary>
        private void ApplyFormatLock()
        {
            if (CmbPackVersion == null) return;
            bool bo2 = CmbPackVersion.SelectedIndex == 0;
            bool wasLoading = _packLoading;
            _packLoading = true;
            (bo2 ? RbFmtIpak : RbFmtIwd).IsChecked = true;
            _packLoading = wasLoading;
            RbFmtIpak.IsEnabled = bo2 && !_packBusy;
            RbFmtIwd.IsEnabled = !bo2 && !_packBusy;
        }

        /// <summary>Описание каждого уровня сжатия — всплывающая подсказка у переключателя (появляется после 5 секунд наведения).</summary>
        private void UpdatePackCompressionHint()
        {
            if (RbCompMax == null || RbCompNormal == null || RbCompNone == null) return;
            bool ipak = RbFmtIpak.IsChecked == true;
            if (ipak)
            {
                RbCompMax.ToolTip = PackT("LZO с перебором вариантов: файл получается меньше, но упаковка идёт дольше.", "LZO with a deeper search: a smaller file, but packing takes longer.");
                RbCompNormal.ToolTip = PackT("LZO, быстро. Сжимаются только те куски, которые от этого уменьшаются.", "LZO, fast. Only the pieces that actually shrink are compressed.");
                RbCompNone.ToolTip = PackT("Без сжатия: файл больше, зато игра читает его быстрее всего.", "No compression: a bigger file, but the game reads it fastest.");
            }
            else
            {
                RbCompMax.ToolTip = PackT("ZIP (Deflate) с максимальным сжатием: самый маленький IWD; если выигрыша нет — архив остаётся как был.", "ZIP (Deflate), maximum compression: the smallest IWD; if it does not help, the archive stays as it was.");
                RbCompNormal.ToolTip = PackT("ZIP (Deflate), обычное сжатие: баланс между размером и скоростью.", "ZIP (Deflate), normal compression: a balance between size and speed.");
                RbCompNone.ToolTip = PackT("Файлы кладутся в ZIP без сжатия: быстрее всего, но архив больше.", "Files are stored in the ZIP without compression: fastest, but a bigger archive.");
            }
        }

        private void CmbPackVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_packLoading) return;
            if (CmbPackVersion.SelectedIndex >= 0) _packVersionIndex = CmbPackVersion.SelectedIndex;
            _gameDir = ResolveGameDir(_settings.GameDirs ?? Array.Empty<string>(), CurrentPackVersionIndex());
            ApplyFormatLock();
            UpdatePackFormatUi();
            UpdatePackNameHint();
            ClearPickedArchive();
            if (PopArchives.IsOpen) _ = RefreshArchivePopupAsync();
        }

        private void PackSaveLocation_Changed(object? sender, RoutedEventArgs? e)
        {
            if (PnlPackCustomPath == null) return;
            bool custom = RbPackSaveCustom.IsChecked == true;
            PnlPackCustomPath.IsEnabled = custom;
            PnlPackCustomPath.Opacity = custom ? 1.0 : 0.55;
        }

        private void BtnPackBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog();
            if (dlg.ShowDialog() == true)
            {
                TxtPackCustomPath.Text = dlg.FolderName;
                RbPackSaveCustom.IsChecked = true;
                PackSaveLocation_Changed(null, null);
            }
        }

        private void UpdatePackNameHint()
        {
            if (TxtNamePlaceholder == null || TxtArchiveName == null) return;
            TxtNamePlaceholder.Visibility = string.IsNullOrEmpty(TxtArchiveName.Text) && !TxtArchiveName.IsKeyboardFocused
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void TxtArchiveName_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdatePackNameHint();
            if (_packLoading || _nameFromCode || TxtArchiveName == null) return;

            // имя изменили руками: прежний выбор архива из списка больше не действует
            if (_pickedArchivePath.Length > 0
                && !string.Equals(TxtArchiveName.Text.Trim(), _pickedArchiveName, StringComparison.OrdinalIgnoreCase))
                ClearPickedArchive();

            // список сам не открывается; если он открыт стрелкой — показываем только подходящие архивы
            _archiveFilter = TxtArchiveName.Text.Trim();
            if (PopArchives.IsOpen) ApplyArchiveFilter();
        }
        private void TxtArchiveName_FocusChanged(object sender, KeyboardFocusChangedEventArgs e) => UpdatePackNameHint();

        // ============================================================ упаковка

        private static string SanitizeArchiveName(string text, bool ipak)
        {
            string name = (text ?? "").Trim();
            foreach (string ext in new[] { ".iwd", ".ipak" })
            {
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - ext.Length);
            }
            var invalid = Path.GetInvalidFileNameChars();
            name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (name.Length == 0) name = ipak ? "ipak_collect" : "iw_collect";
            return name;
        }

        /// <summary>«Рядом»: общая папка исходных файлов (для файла из архива — папка архива).</summary>
        private static string CommonFolder(List<TextureFile> files)
        {
            var dirs = new List<string[]>();
            string first = "";
            foreach (var f in files)
            {
                string? dir = Path.GetDirectoryName(f.IsArchiveEntry ? f.ArchivePath! : f.FullPath);
                if (string.IsNullOrEmpty(dir)) continue;
                if (first.Length == 0) first = dir;
                dirs.Add(dir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries));
            }
            if (dirs.Count == 0) return first;

            int common = dirs.Min(d => d.Length);
            for (int i = 0; i < common; i++)
            {
                string part = dirs[0][i];
                if (dirs.Any(d => !string.Equals(d[i], part, StringComparison.OrdinalIgnoreCase)))
                {
                    common = i;
                    break;
                }
            }
            if (common == 0) return first; // разные диски

            string path = string.Join(Path.DirectorySeparatorChar.ToString(), dirs[0].Take(common));
            if (path.Length == 2 && path[1] == ':') path += Path.DirectorySeparatorChar; // «C:» без слэша — это текущая папка диска
            if (first.StartsWith(@"\\")) path = @"\\" + path;                            // сетевой путь
            return path;
        }

        private void SetPackBusy(bool busy)
        {
            _packBusy = busy;
            BtnPackAll.IsEnabled = !busy;
            BtnPackSelected.IsEnabled = !busy;
            BtnPackRecompress.IsEnabled = !busy;
            BtnArchiveContents.IsEnabled = !busy;
            BtnPackStop.IsEnabled = busy;
            BtnConvertAll.IsEnabled = !busy;       // конвертация во время упаковки не запускается
            BtnConvertSelected.IsEnabled = !busy;
            RbAppConvert.IsEnabled = !busy;
            RbAppArchive.IsEnabled = !busy;
            CmbPackVersion.IsEnabled = !busy;
            ApplyFormatLock();
            TxtArchiveName.IsEnabled = !busy;
            BtnArchivePick.IsEnabled = !busy;
        }

        private async void BtnPackAll_Click(object sender, RoutedEventArgs e) => await StartPack(FilesList.ToList());

        private async void BtnPackSelected_Click(object sender, RoutedEventArgs e)
            => await StartPack(GridFiles.SelectedItems.Cast<TextureFile>().ToList());

        private async Task StartPack(List<TextureFile> files)
        {
            if (_packBusy) return;
            if (!BtnConvertAll.IsEnabled)
            {
                LogInfo(PackT("Сейчас идёт конвертация — упаковка станет доступна после её окончания.", "A conversion is running — packing will be available when it finishes."));
                return;
            }
            if (files.Count == 0)
            {
                PackLog(PackT("Нечего упаковывать: выберите файлы или добавьте их в список.", "Nothing to pack: select files or add them to the list."), 2);
                return;
            }

            bool ipak = RbFmtIpak.IsChecked == true;
            int codVer = ipak ? 0 : Math.Max(0, CmbPackVersion.SelectedIndex);
            int level = SelectedCompression();
            bool keepOriginal = ChkPackKeepOriginal.IsChecked == true;
            bool replace = ChkPackReplace.IsChecked == true;
            SavePackSettings();

            // куда сохранять
            string archiveName = SanitizeArchiveName(TxtArchiveName.Text, ipak);
            string archiveFile = archiveName + (ipak ? ".ipak" : ".iwd");
            // «Рядом» / «Другая папка» — одно правило для всего: туда идёт и архив, и папка с оригиналами «имя_originals»
            string saveDir;
            if (RbPackSaveCustom.IsChecked == true)
            {
                saveDir = TxtPackCustomPath.Text.Trim();
                if (saveDir.Length == 0)
                {
                    PackLog(PackT("Выберите папку для сохранения.", "Choose a folder to save to."), 3);
                    return;
                }
            }
            else
            {
                saveDir = CommonFolder(files);
            }
            if (string.IsNullOrWhiteSpace(saveDir))
            {
                PackLog(PackT("Не удалось определить папку для сохранения.", "Could not work out the folder to save to."), 3);
                return;
            }

            // архив, выбранный из списка папки игры, обновляется на месте; оригиналы всё равно уходят в выбранное место сохранения
            string archiveDir = ResolveArchiveDir(archiveName, ipak);
            if (archiveDir.Length == 0) archiveDir = saveDir;
            string archivePath = Path.Combine(archiveDir, archiveFile);
            string originalsDir = Path.Combine(saveDir, archiveName + "_originals");

            if (!ipak && codVer == 0)
                PackLog(PackT("Black Ops 2 не использует IWD (там IPAK). Текстуры будут сделаны под его версию IWI — проверьте выбор версии.", "Black Ops 2 does not use IWD (it uses IPAK). The textures will be built for its IWI version — check the selected version."), 2);

            // имена внутри архива; повторы: при «заменять» побеждает последний, иначе первый
            var byName = new Dictionary<string, PackItem>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f.FileName);
                if (name.Length == 0) continue;
                if (byName.TryGetValue(name, out var existing))
                {
                    PackLog(PackT($"Одинаковое имя «{name}»: ", $"Same name \"{name}\": ") + (replace ? PackT("берётся последний файл.", "the last file is used.") : PackT("берётся первый файл.", "the first file is used.")), 2);
                    if (replace) existing.File = f;
                    continue;
                }
                byName[name] = new PackItem { File = f, Name = name };
                order.Add(name);
            }
            var items = order.Select(n => byName[n]).ToList();

            string pendingText = Localize(ConvState.Pending);
            foreach (var f in files)
            {
                f.PackStatus = true;
                f.SetState(ConvState.Pending, pendingText);
            }

            _packCts = new CancellationTokenSource();
            CancellationToken token = _packCts.Token;
            SetPackBusy(true);

            _packDone = 0;
            int total = items.Count;
            PushProgress(0, total);
            if (AppTaskbarInfo != null)
            {
                AppTaskbarInfo.ProgressState = TaskbarItemProgressState.Normal;
                AppTaskbarInfo.ProgressValue = 0;
            }
            _convStopwatch.Restart();
            TxtElapsed.Text = "00:00";
            var uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            uiTimer.Tick += (_, _) =>
            {
                PushProgress(Volatile.Read(ref _packDone), total);
                TxtElapsed.Text = FormatElapsed(_convStopwatch.Elapsed);
            };
            uiTimer.Start();

            byte iwiVersion = IwiCodec.VersionByIndex[Math.Clamp(codVer, 0, IwiCodec.VersionByIndex.Length - 1)];
            PackLog(PackT($"Начинаю упаковку: {items.Count} файл(ов) → {archivePath} ({(ipak ? "IPAK" : "IWD")}, IWI v{iwiVersion}).",
                          $"Packing {items.Count} file(s) → {archivePath} ({(ipak ? "IPAK" : "IWD")}, IWI v{iwiVersion})."), 0);

            Func<byte[], string, string, string, byte[]> convert = CreateIwiConverter(codVer);
            int dop = GetMaxParallelism();
            var extractor = new ArchiveExtractor(items.Where(i => i.File.IsArchiveEntry).Select(i => i.File), token);

            var summary = new PackSummary();
            try
            {
                Directory.CreateDirectory(archiveDir);
                summary = await Task.Run(() => RunPack(items, archivePath, originalsDir, ipak, iwiVersion, level, replace, keepOriginal, convert, extractor, dop, token));
            }
            catch (OperationCanceledException)
            {
                PackLog(PackT("Остановлено. Архив не изменён.", "Stopped. The archive was not changed."), 2);
            }
            catch (Exception ex)
            {
                PackLog(PackT("Сбой упаковки: ", "Packing failed: ") + ex.Message, 3);
            }
            finally
            {
                extractor.Dispose();
                uiTimer.Stop();
                _convStopwatch.Stop();
                TxtElapsed.Text = FormatElapsed(_convStopwatch.Elapsed);
                PushProgress(Volatile.Read(ref _packDone), total);
                if (AppTaskbarInfo != null) AppTaskbarInfo.ProgressState = TaskbarItemProgressState.None;
                SetPackBusy(false);
                _packCts?.Dispose();
                _packCts = null;
            }

            FlushLog();
            FlashTaskbar();

            if (summary.Completed)
            {
                _lastArchivePath = archivePath;
                string text = PackT(
                    $"Готово: {archiveFile} — добавлено {summary.Added}, заменено {summary.Replaced}, пропущено {summary.Skipped}, ошибок {summary.Failed}, время {FormatElapsed(_convStopwatch.Elapsed)}.",
                    $"Done: {archiveFile} — added {summary.Added}, replaced {summary.Replaced}, skipped {summary.Skipped}, errors {summary.Failed}, time {FormatElapsed(_convStopwatch.Elapsed)}.");
                if (summary.Originals > 0)
                    text += PackT($" Сохранено оригиналов: {summary.Originals} → {originalsDir}.", $" Originals saved: {summary.Originals} → {originalsDir}.");
                PackLog(text, summary.Failed > 0 ? 2 : 1);
                if (_settings.PlaySoundOnFinish) try { SystemSounds.Asterisk.Play(); } catch { }
            }
        }

        private PackSummary RunPack(List<PackItem> items, string archivePath, string originalsDir, bool ipak, byte expectedVersion, int level, bool replace, bool keepOriginal,
            Func<byte[], string, string, string, byte[]> convert, ArchiveExtractor extractor, int dop, CancellationToken token)
        {
            System.IO.Compression.CompressionLevel zipLevel = level == 0 ? System.IO.Compression.CompressionLevel.SmallestSize
                                      : level == 2 ? System.IO.Compression.CompressionLevel.NoCompression
                                      : System.IO.Compression.CompressionLevel.Optimal;

            var summary = new PackSummary();
            IArchiveSink sink = ipak ? new IpakSink(archivePath, level, replace) : new IwdSink(archivePath, zipLevel, replace);
            sink.CapturePrevious = keepOriginal;
            string txtAdded = PackT("Добавлено", "Added");
            string txtReplaced = PackT("Заменено", "Replaced");
            string txtSkipped = PackT("Пропущено", "Skipped");
            string txtError = PackT("Ошибка", "Error");

            try
            {
                int batchSize = Math.Max(8, dop * 4);
                for (int start = 0; start < items.Count; start += batchSize)
                {
                    token.ThrowIfCancellationRequested();
                    int count = Math.Min(batchSize, items.Count - start);
                    var prepared = new PackPrepared[count];
                    int batchStart = start;

                    Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = token }, k =>
                    {
                        PackItem item = items[batchStart + k];
                        try
                        {
                            prepared[k] = PreparePackFile(item.File, expectedVersion, convert, extractor);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            prepared[k] = new PackPrepared { Error = ex.Message };
                        }
                    });

                    // запись в архив — строго по одному (потоки сюда не заходят)
                    for (int k = 0; k < count; k++)
                    {
                        token.ThrowIfCancellationRequested();
                        PackItem item = items[start + k];
                        PackPrepared r = prepared[k];
                        prepared[k] = null!;

                        if (r.Warning != null) PackLog($"[{item.File.FileName}] {r.Warning}", 2);

                        if (r.Error != null || r.Iwi == null)
                        {
                            summary.Failed++;
                            item.File.SetState(ConvState.Error, txtError);
                            PackLog($"[{item.File.FileName}] " + (r.Error ?? "?"), 3);
                        }
                        else
                        {
                            try
                            {
                                PackResult result = sink.Add(item.Name, r.Iwi, out byte[]? previous);
                                switch (result)
                                {
                                    case PackResult.Added:
                                        summary.Added++;
                                        item.File.SetState(ConvState.Done, txtAdded);
                                        break;
                                    case PackResult.Replaced:
                                        summary.Replaced++;
                                        item.File.SetState(ConvState.Done, txtReplaced);
                                        if (previous != null && SavePreviousFile(originalsDir, item.Name, previous)) summary.Originals++;
                                        break;
                                    default:
                                        summary.Skipped++;
                                        item.File.SetState(ConvState.Pending, txtSkipped);
                                        PackLog($"[{item.File.FileName}] " + PackT("такой файл уже есть в архиве — пропущен (галочка «Заменять» выключена).", "this file is already in the archive — skipped (\"Replace\" is off)."), 2);
                                        break;
                                }
                            }
                            catch (Exception ex)
                            {
                                summary.Failed++;
                                item.File.SetState(ConvState.Error, txtError);
                                PackLog($"[{item.File.FileName}] " + ex.Message, 3);
                            }
                        }
                        Interlocked.Increment(ref _packDone);
                    }
                }

                token.ThrowIfCancellationRequested();
                if (summary.Added + summary.Replaced == 0 && !File.Exists(archivePath))
                {
                    PackLog(PackT("Нечего упаковывать: ни один файл не подошёл. Архив не создан.", "Nothing to pack: no file was usable. The archive was not created."), 3);
                    return summary; // Completed = false
                }

                PackLog(PackT("Записываю архив…", "Writing the archive…"), 0);
                sink.Finish();
                summary.Completed = true;
            }
            finally
            {
                sink.Dispose(); // после успешной записи ничего не делает, при ошибке или остановке убирает временный файл

                // Если оригиналов не сохранилось, пустая папка «имя_originals» не нужна.
                try
                {
                    if (Directory.Exists(originalsDir) && !Directory.EnumerateFileSystemEntries(originalsDir).Any())
                        Directory.Delete(originalsDir);
                }
                catch { }
            }
            return summary;
        }

        /// <summary>Прежний файл из архива (до замены) кладём в папку «имя_архива_originals» как «имя_Original.iwi». Уже существующий оригинал не затираем.</summary>
        private bool SavePreviousFile(string originalsDir, string name, byte[] data)
        {
            try
            {
                Directory.CreateDirectory(originalsDir);
                string target = Path.Combine(originalsDir, name + "_Original.iwi");
                if (File.Exists(target)) return false; // там уже лежит самый первый оригинал
                File.WriteAllBytes(target, data);
                return true;
            }
            catch (Exception ex)
            {
                PackLog($"[{name}] " + PackT("оригинал сохранить не удалось: ", "could not save the original: ") + ex.Message, 2);
                return false;
            }
        }

        /// <summary>Читает исходный файл и при необходимости конвертирует его в IWI.</summary>
        private PackPrepared PreparePackFile(TextureFile file, byte expectedVersion, Func<byte[], string, string, string, byte[]> convert, ArchiveExtractor extractor)
        {
            // Файл замены мог быть удалён/перемещён после назначения — лучше ошибка, чем молча упаковать старую текстуру.
            if (!string.IsNullOrWhiteSpace(file.ReplacePath) && !File.Exists(file.ReplacePath))
                return new PackPrepared
                {
                    Error = PackT("файл замены не найден: ", "replacement file not found: ") + file.ReplacePath
                };

            byte[] src;
            string sourcePath = file.FullPath;
            string nameForExtension = file.FileName;

            if (file.HasReplace)
            {
                // Пользователь назначил внешний файл: берём его, но имя записи в архиве
                // остаётся прежним — так и делается замена текстуры в текстур-паке.
                sourcePath = file.ReplacePath!;
                src = File.ReadAllBytes(sourcePath);
                nameForExtension = Path.GetFileName(sourcePath);
            }
            else if (file.IsArchiveEntry)
            {
                string temp = extractor.GetFile(file.ArchivePath!, file.EntryPath!);
                try { src = File.ReadAllBytes(temp); }
                finally { try { File.Delete(temp); } catch { } }
            }
            else
            {
                src = File.ReadAllBytes(file.FullPath);
            }

            string inExt = Path.GetExtension(nameForExtension).ToLowerInvariant();
            var result = new PackPrepared();

            if (inExt == ".iwi")
            {
                // IWI не конвертируем — только проверяем, что версия подходит игре
                if (src.Length < 8 || src[0] != (byte)'I' || src[1] != (byte)'W' || src[2] != (byte)'i')
                {
                    result.Error = PackT("это не IWI (нет сигнатуры IWi).", "this is not an IWI (no IWi signature).");
                    return result;
                }
                if (src[3] != expectedVersion)
                {
                    result.Warning = PackT($"IWI версии {src[3]}, а для выбранной игры нужна {expectedVersion}. Файл упакован как есть — игра может его не принять.",
                                           $"IWI version {src[3]}, but the selected game needs {expectedVersion}. The file was packed as is — the game may reject it.");
                }
                result.Iwi = src;
                return result;
            }

            result.Iwi = convert(src, inExt, file.FileName, sourcePath);
            return result;
        }

        private void BtnPackStop_Click(object sender, RoutedEventArgs e)
        {
            _packCts?.Cancel();
            BtnPackStop.IsEnabled = false;
            PackLog(PackT("Останавливаю…", "Stopping…"), 0);
        }

        private void BtnPackShowFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_lastArchivePath.Length > 0 && File.Exists(_lastArchivePath))
                {
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + _lastArchivePath + "\"", UseShellExecute = true });
                    return;
                }

                // архива ещё не было: открываем папку, куда он будет сохранён
                string dir = RbPackSaveCustom.IsChecked == true ? TxtPackCustomPath.Text.Trim() : CommonFolder(FilesList.ToList());
                if (dir.Length > 0 && Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                else
                    PackLog(PackT("Папка пока не определена: добавьте файлы в список или выберите папку.", "The folder is not known yet: add files to the list or choose a folder."), 2);
            }
            catch (Exception ex)
            {
                PackLog(ex.Message, 3);
            }
        }

        // ============================================================ сжатие готового архива

        private int _recompressDone;
        private int _recompressTotal;

        private async void BtnPackRecompress_Click(object sender, RoutedEventArgs e) => await StartRecompress();

        /// <summary>Пересжимает выбранный архив выбранным уровнем (Максимум / Обычно / Без сжатия). Содержимое не меняется.</summary>
        private async Task StartRecompress()
        {
            if (_packBusy) return;
            if (!BtnConvertAll.IsEnabled)
            {
                LogInfo(PackT("Сейчас идёт конвертация — сжатие станет доступно после её окончания.", "A conversion is running — compression will be available when it finishes."));
                return;
            }

            // какой архив: выбранный стрелкой из папки игры, а если не выбран — архив с этим именем в папке сохранения
            bool ipakMode = RbFmtIpak.IsChecked == true;
            var listFiles = FilesList.ToList();
            string saveDir = RbPackSaveCustom.IsChecked == true ? TxtPackCustomPath.Text.Trim() : CommonFolder(listFiles);
            string arcName = SanitizeArchiveName(TxtArchiveName.Text, ipakMode);
            string arcExt = ipakMode ? ".ipak" : ".iwd";
            string target = "";

            // сначала ищем архив в папке игры для текущей версии, потом — в папке сохранения
            string resolvedDir = ResolveArchiveDir(arcName, ipakMode);
            if (resolvedDir.Length > 0)
            {
                string candidate = Path.Combine(resolvedDir, arcName + arcExt);
                if (File.Exists(candidate)) target = candidate;
            }
            if (target.Length == 0 && saveDir.Length > 0)
            {
                string candidate = Path.Combine(saveDir, arcName + arcExt);
                if (File.Exists(candidate)) target = candidate;
            }
            if (target.Length == 0)
            {
                PackLog(PackT("Выберите архив стрелкой в поле «Имя архива» — его я и сожму.", "Pick an archive with the arrow in the name field — that is the one I will compress."), 2);
                return;
            }

            bool targetIpak = string.Equals(Path.GetExtension(target), ".ipak", StringComparison.OrdinalIgnoreCase);
            int level = SelectedCompression();
            bool keepOriginal = ChkPackKeepOriginal.IsChecked == true;
            string levelName = level switch { 0 => PackT("Максимум", "Maximum"), 2 => PackT("Без сжатия", "None"), _ => PackT("Обычно", "Normal") };
            string archiveFile = Path.GetFileName(target);
            SavePackSettings();

            long before;
            try { before = new FileInfo(target).Length; }
            catch (Exception ex)
            {
                PackLog(ex.Message, 3);
                return;
            }

            _packCts = new CancellationTokenSource();
            CancellationToken token = _packCts.Token;
            SetPackBusy(true);

            _recompressDone = 0;
            _recompressTotal = 0;
            PushProgress(0, 1);
            if (AppTaskbarInfo != null)
            {
                AppTaskbarInfo.ProgressState = TaskbarItemProgressState.Normal;
                AppTaskbarInfo.ProgressValue = 0;
            }
            _convStopwatch.Restart();
            TxtElapsed.Text = "00:00";
            var uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            uiTimer.Tick += (_, _) =>
            {
                PushProgress(Volatile.Read(ref _recompressDone), Math.Max(1, Volatile.Read(ref _recompressTotal)));
                TxtElapsed.Text = FormatElapsed(_convStopwatch.Elapsed);
            };
            uiTimer.Start();

            PackLog(PackT($"Сжимаю архив {target}: уровень «{levelName}», сейчас {FormatArchiveSize(before)}.",
                          $"Compressing {target}: level \"{levelName}\", now {FormatArchiveSize(before)}."), 0);

            bool success = false;
            bool unchanged = false;
            long after = before;
            try
            {
                // оригинал архива — в папку «имя_архива_originals» (по тому же правилу «Рядом / Другая папка»)
                if (keepOriginal)
                {
                    string originalsRoot = saveDir.Length > 0 ? saveDir : (Path.GetDirectoryName(target) ?? "");
                    string originalsDir = Path.Combine(originalsRoot, Path.GetFileNameWithoutExtension(target) + "_backup");
                    string backup = Path.Combine(originalsDir, archiveFile);
                    await Task.Run(() =>
                    {
                        Directory.CreateDirectory(originalsDir);
                        if (!File.Exists(backup)) File.Copy(target, backup);
                    });
                    PackLog(PackT($"Оригинал архива сохранён: {backup}", $"The original archive is saved: {backup}"), 0);
                }

                bool changed = await Task.Run(() => ArchiveRecompressor.Recompress(target, targetIpak, level,
                    (done, total) =>
                    {
                        Volatile.Write(ref _recompressTotal, total);
                        Volatile.Write(ref _recompressDone, done);
                    }, token));

                after = new FileInfo(target).Length;
                success = true;
                unchanged = !changed;

                if (!changed)
                {
                    PackLog(PackT("Архив уже сжат оптимально — оставлен без изменений.",
                                  "The archive is already optimal — left unchanged."), 1);
                }
            }
            catch (OperationCanceledException)
            {
                PackLog(PackT("Остановлено. Архив не изменён.", "Stopped. The archive was not changed."), 2);
            }
            catch (Exception ex)
            {
                PackLog(PackT("Не удалось сжать архив: ", "Could not compress the archive: ") + ex.Message, 3);
            }
            finally
            {
                uiTimer.Stop();
                _convStopwatch.Stop();
                TxtElapsed.Text = FormatElapsed(_convStopwatch.Elapsed);
                PushProgress(success ? 1 : Volatile.Read(ref _recompressDone), success ? 1 : Math.Max(1, Volatile.Read(ref _recompressTotal)));
                if (AppTaskbarInfo != null) AppTaskbarInfo.ProgressState = TaskbarItemProgressState.None;
                SetPackBusy(false);
                _packCts?.Dispose();
                _packCts = null;
            }

            FlushLog();
            FlashTaskbar();

            if (success && !unchanged)
            {
                _lastArchivePath = target;
                double percent = before > 0 ? (after - before) * 100.0 / before : 0;
                string sign = percent > 0 ? "+" : "";
                string text = PackT(
                    $"Готово: {archiveFile} — {FormatArchiveSize(before)} → {FormatArchiveSize(after)} ({sign}{percent:0.#}%), уровень «{levelName}», время {FormatElapsed(_convStopwatch.Elapsed)}.",
                    $"Done: {archiveFile} — {FormatArchiveSize(before)} → {FormatArchiveSize(after)} ({sign}{percent:0.#}%), level \"{levelName}\", time {FormatElapsed(_convStopwatch.Elapsed)}.");
                PackLog(text, after <= before ? 1 : 2);
                if (_settings.PlaySoundOnFinish) try { SystemSounds.Asterisk.Play(); } catch { }
            }
        }

        // ============================================================ файлы, открытые снаружи

        /// <summary>
        /// Файлы пришли снаружи: двойной клик в проводнике, «Открыть с помощью» или вторая копия программы.
        /// Файл попадает в список; дальше по настройкам — предпросмотр и/или сразу конвертация (только в режиме конвертера).
        /// </summary>
        internal async void OpenExternalFiles(IEnumerable<string> paths)
        {
            try
            {
                bool ru = _settings.IsRussian;
                var files = new List<string>();
                foreach (string p in paths)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(p)) continue;
                        if (Directory.Exists(p)) files.AddRange(ScanFolder(p));
                        else if (File.Exists(p) && InputExts.Contains(Path.GetExtension(p))) files.Add(Path.GetFullPath(p));
                    }
                    catch { }
                }
                if (files.Count == 0) return;

                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();

                await AddPathsAsync(files);
                UpdateStatusCount();

                // те, что открыли: и новые, и уже лежавшие в списке; для архивов — всё, что нашлось внутри
                var plain = new HashSet<string>(files.Where(f => !ArchiveSupport.IsArchive(f)), StringComparer.OrdinalIgnoreCase);
                var archives = new HashSet<string>(files.Where(ArchiveSupport.IsArchive), StringComparer.OrdinalIgnoreCase);
                var items = FilesList.Where(f => plain.Contains(f.FullPath) || (f.ArchivePath != null && archives.Contains(f.ArchivePath))).ToList();
                if (items.Count == 0) return;

                GridFiles.SelectedItems.Clear();
                GridFiles.SelectedItem = items[0];
                GridFiles.ScrollIntoView(items[0]);

                if (_settings.PreviewOnOpen)
                {
                    if (_preview == null) TogglePreview();
                    else UpdatePreview();
                }

                if (_settings.AutoConvertOnOpen && !_archiveMode)
                {
                    if (BtnConvertAll.IsEnabled)
                        await StartConversion(items, 2);
                    else
                        LogInfo(ru ? "Конвертация уже идёт — файл добавлен в список." : "A conversion is already running — the file was added to the list.");
                }
            }
            catch (Exception ex)
            {
                LogError(_settings.IsRussian ? $"Не удалось открыть файл: {ex.Message}" : $"Could not open the file: {ex.Message}");
            }
        }
    }
}
