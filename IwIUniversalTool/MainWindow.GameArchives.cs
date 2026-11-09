using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IwIUniversalTool
{
    /// <summary>Архив из папки игры для выпадающего списка в поле «Имя архива».</summary>
    internal sealed class ArchiveListItem
    {
        public string Display { get; init; } = "";      // путь без расширения, например main\iw_00
        public string FullDisplay { get; init; } = "";  // путь с расширением — для подсказки при наведении
        public string FullPath { get; init; } = "";
        public string SizeText { get; init; } = "";
    }

    // Выпадающий список архивов выбранной версии Call of Duty (папки игр задаются в настройках или находятся автоматически).
    public partial class MainWindow
    {
        private List<ArchiveListItem> _gameArchives = new();
        private string _gameDir = "";
        private string _archiveFilter = "";
        private string _pickedArchivePath = "";
        private string _pickedArchiveName = "";
        private bool _nameFromCode;
        private int _archiveScanId;

        private static string FormatArchiveSize(long bytes)
        {
            string[] suf = { "B", "KB", "MB", "GB" };
            if (bytes <= 0) return "0 B";
            int place = Math.Min((int)Math.Floor(Math.Log(bytes, 1024)), suf.Length - 1);
            double num = Math.Round(bytes / Math.Pow(1024, place), 1);
            return $"{num} {suf[place]}";
        }

        /// <summary>Папка игры: из настроек, а если там пусто или её нет — найденная автоматически.</summary>
        private static string ResolveGameDir(string[] configured, int versionIndex)
        {
            if (configured != null && versionIndex >= 0 && versionIndex < configured.Length)
            {
                string d = (configured[versionIndex] ?? "").Trim();
                if (d.Length > 0 && Directory.Exists(d)) return d;
            }
            return GameLocator.Detect(versionIndex) ?? "";
        }

        /// <summary>Ищет архивы нужного формата в папке игры (до 4 уровней вглубь).</summary>
        private static List<ArchiveListItem> ScanArchives(string dir, string ext)
        {
            var list = new List<ArchiveListItem>();
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
                foreach (string file in Directory.EnumerateFiles(dir, "*" + ext, options))
                {
                    long size = 0;
                    try { size = new FileInfo(file).Length; } catch { }
                    string rel = Path.GetRelativePath(dir, file);
                    list.Add(new ArchiveListItem
                    {
                        FullDisplay = rel,
                        Display = StripExtension(rel),
                        FullPath = file,
                        SizeText = FormatArchiveSize(size)
                    });
                }
            }
            catch { }
            list.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// <summary>Убирает расширение из относительного пути: main\iw_00.iwd → main\iw_00.</summary>
        private static string StripExtension(string relativePath)
        {
            string? dirPart = Path.GetDirectoryName(relativePath);
            string name = Path.GetFileNameWithoutExtension(relativePath);
            return string.IsNullOrEmpty(dirPart) ? name : Path.Combine(dirPart, name);
        }

        private int CurrentPackVersionIndex() => Math.Max(0, CmbPackVersion.SelectedIndex);

        private void OpenArchivePopup(bool showAll)
        {
            if (_packBusy) return;
            if (showAll) _archiveFilter = "";
            PopArchives.IsOpen = true;
            _ = RefreshArchivePopupAsync();
        }

        private async Task RefreshArchivePopupAsync()
        {
            int id = ++_archiveScanId;
            bool ipak = RbFmtIpak.IsChecked == true;
            int version = CurrentPackVersionIndex();
            string ext = ipak ? ".ipak" : ".iwd";
            string[] configured = _settings.GameDirs != null ? (string[])_settings.GameDirs.Clone() : new string[GameLocator.GameCount];

            LstArchives.Visibility = Visibility.Collapsed;
            TxtArchivesEmpty.Text = PackT("Поиск архивов…", "Searching for archives…");
            TxtArchivesEmpty.Visibility = Visibility.Visible;
            var found = await Task.Run(() =>
            {
                string dir = ResolveGameDir(configured, version);
                List<ArchiveListItem> items = dir.Length > 0 ? ScanArchives(dir, ext) : new List<ArchiveListItem>();
                return (Dir: dir, Items: items);
            });

            if (id != _archiveScanId) return; // пока искали, запустили новый поиск
            _gameDir = found.Dir;
            _gameArchives = found.Items;
            ApplyArchiveFilter();
        }

        private void ApplyArchiveFilter()
        {
            if (LstArchives == null) return;
            bool ipak = RbFmtIpak.IsChecked == true;
            string ext = ipak ? ".ipak" : ".iwd";

            IEnumerable<ArchiveListItem> view = _gameArchives;
            if (_archiveFilter.Length > 0)
                view = view.Where(a =>
                    a.Display.IndexOf(_archiveFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    a.FullDisplay.IndexOf(_archiveFilter, StringComparison.OrdinalIgnoreCase) >= 0);
            var shown = view.ToList();

            LstArchives.ItemsSource = shown;
            LstArchives.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtArchivesEmpty.Visibility = shown.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

            if (_gameDir.Length == 0)
            {
                TxtArchivesEmpty.Text = PackT(
                    "Папка этой версии игры не указана и не нашлась автоматически. Укажите её в настройках, в блоке «Директории Call of Duty».",
                    "The folder of this game version is not set and could not be found automatically. Set it in the settings, in the \"Call of Duty folders\" block.");
            }
            else
            {
                TxtArchivesEmpty.Text = _gameArchives.Count == 0
                    ? PackT($"В папке игры нет архивов {ext}.", $"No {ext} archives in the game folder.")
                    : PackT("Ничего не найдено — такой архив будет создан заново.", "Nothing found — such an archive will be created from scratch.");
            }
        }

        private void PickArchive(ArchiveListItem item)
        {
            _nameFromCode = true;
            TxtArchiveName.Text = Path.GetFileNameWithoutExtension(item.FullPath);
            _nameFromCode = false;
            _pickedArchivePath = item.FullPath;
            _pickedArchiveName = TxtArchiveName.Text;
            PopArchives.IsOpen = false;
            UpdatePackNameHint();
        }

        private void ClearPickedArchive()
        {
            _pickedArchivePath = "";
            _pickedArchiveName = "";
        }

        /// <summary>Если архив выбран из списка и имя не менялось — пакуем прямо в папку этого архива.</summary>
        private bool TryGetPickedArchiveDir(string sanitizedName, bool ipak, out string dir)
        {
            dir = "";
            if (_pickedArchivePath.Length == 0) return false;
            string ext = ipak ? ".ipak" : ".iwd";
            if (!string.Equals(Path.GetExtension(_pickedArchivePath), ext, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(Path.GetFileNameWithoutExtension(_pickedArchivePath), sanitizedName, StringComparison.OrdinalIgnoreCase)) return false;
            string? d = Path.GetDirectoryName(_pickedArchivePath);
            if (string.IsNullOrEmpty(d)) return false;
            dir = d;
            return true;
        }

        /// <summary>
        /// Папка архива для упаковки: выбранный из списка архив, иначе — поиск архива
        /// с таким именем в папке текущей версии игры (иначе после смены версии имя
        /// оставалось, но паковалось не туда).
        /// </summary>
        private string ResolveArchiveDir(string archiveName, bool ipak)
        {
            if (TryGetPickedArchiveDir(archiveName, ipak, out string picked)) return picked;

            string ext = ipak ? ".ipak" : ".iwd";
            if (archiveName.Length == 0) return "";

            string root = ResolveGameDir(_settings.GameDirs ?? Array.Empty<string>(), CurrentPackVersionIndex());
            if (root.Length == 0) return "";

            try
            {
                string direct = Path.Combine(root, archiveName + ext);
                if (File.Exists(direct)) return root;

                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 4,
                    IgnoreInaccessible = true
                };
                foreach (string file in Directory.EnumerateFiles(root, "*" + ext, options))
                {
                    if (string.Equals(Path.GetFileNameWithoutExtension(file), archiveName, StringComparison.OrdinalIgnoreCase))
                        return Path.GetDirectoryName(file) ?? root;
                }
            }
            catch { }

            return "";
        }

        // ---------------------------------------------------------------- события

        private void BtnArchivePick_Click(object sender, RoutedEventArgs e)
        {
            if (PopArchives.IsOpen) PopArchives.IsOpen = false;
            else OpenArchivePopup(true);
            BtnArchivePick.IsChecked = PopArchives.IsOpen; // стрелка повёрнута, пока список открыт
        }

        /// <summary>Добавляет текстуры выбранного архива в основной список файлов (с него можно смотреть превью, конвертировать и т. д.).</summary>
        private async void BtnArchiveContents_Click(object sender, RoutedEventArgs e)
        {
            PopArchives.IsOpen = false;

            bool ipak = RbFmtIpak.IsChecked == true;
            string archiveName = SanitizeArchiveName(TxtArchiveName.Text, ipak);

            // тот же выбор места, что и при упаковке: архив из списка игры, иначе «Рядом» / «Другая папка»
            string saveDir = RbPackSaveCustom.IsChecked == true ? TxtPackCustomPath.Text.Trim() : CommonFolder(FilesList.ToList());
            string archiveDir = ResolveArchiveDir(archiveName, ipak);
            if (archiveDir.Length == 0) archiveDir = saveDir;
            if (string.IsNullOrWhiteSpace(archiveDir))
            {
                PackLog(PackT("Папка для архива пока не определена: выберите архив стрелкой в поле «Имя архива» или укажите папку.",
                              "The archive folder is not known yet: pick an archive with the arrow in the name field or choose a folder."), 2);
                return;
            }

            string archivePath = Path.Combine(archiveDir, archiveName + (ipak ? ".ipak" : ".iwd"));
            if (!File.Exists(archivePath))
            {
                PackLog(PackT($"Архива «{Path.GetFileName(archivePath)}» ещё нет: {archivePath}", $"The archive \"{Path.GetFileName(archivePath)}\" does not exist yet: {archivePath}"), 2);
                return;
            }

            int before = FilesList.Count;
            try
            {
                await AddPathsAsync(new[] { archivePath });
            }
            catch (Exception ex)
            {
                PackLog(PackT("Не удалось прочитать архив: ", "Could not read the archive: ") + ex.Message, 3);
                return;
            }
            UpdateStatusCount();

            int added = FilesList.Count - before;
            string text = added > 0
                ? PackT($"Из архива «{Path.GetFileName(archivePath)}» добавлено текстур: {added}.", $"Textures added from \"{Path.GetFileName(archivePath)}\": {added}.")
                : PackT($"Все текстуры архива «{Path.GetFileName(archivePath)}» уже есть в списке.", $"All the textures of \"{Path.GetFileName(archivePath)}\" are already in the list.");
            if (ipak && added > 0)
                text += PackT(" В IPAK имён нет, поэтому текстуры названы по хешу (например 0xA1B2C3D4.iwi).", " IPAK stores no names, so the textures are named by hash (for example 0xA1B2C3D4.iwi).");
            PackLog(text, added > 0 ? 1 : 0);
        }

        private void PopArchives_Opened(object? sender, EventArgs e) => BtnArchivePick.IsChecked = true;

        private void PopArchives_Closed(object? sender, EventArgs e) => BtnArchivePick.IsChecked = false;

        /// <summary>Клик вне списка (кроме самой стрелки и поля имени) закрывает список архивов.</summary>
        private void ClosePopupOnOutsideClick(object sender, MouseButtonEventArgs e)
        {
            if (!PopArchives.IsOpen) return;
            if (e.OriginalSource is DependencyObject source)
            {
                // клики по самому списку (в том числе по его полосе прокрутки), стрелке и полю имени список не закрывают
                if (IsInside(source, BtnArchivePick) || IsInside(source, TxtArchiveName)) return;
                if (PopArchives.Child != null && IsInside(source, PopArchives.Child)) return;
            }
            PopArchives.IsOpen = false;
        }

        private static bool IsInside(DependencyObject? element, DependencyObject container)
        {
            while (element != null)
            {
                if (ReferenceEquals(element, container)) return true;
                element = element is System.Windows.Media.Visual || element is System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                    : LogicalTreeHelper.GetParent(element);
            }
            return false;
        }

        private void LstArchives_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source
                && ItemsControl.ContainerFromElement(LstArchives, source) is ListBoxItem container
                && container.DataContext is ArchiveListItem item)
            {
                PickArchive(item);
                e.Handled = true;
            }
        }

        private void TxtArchiveName_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && PopArchives.IsOpen)
            {
                PopArchives.IsOpen = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Down || e.Key == Key.Up)
            {
                if (!PopArchives.IsOpen)
                {
                    OpenArchivePopup(true);
                }
                else if (LstArchives.Items.Count > 0)
                {
                    int next = LstArchives.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                    LstArchives.SelectedIndex = Math.Clamp(next, 0, LstArchives.Items.Count - 1);
                    LstArchives.ScrollIntoView(LstArchives.SelectedItem);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && PopArchives.IsOpen && LstArchives.SelectedItem is ArchiveListItem chosen)
            {
                PickArchive(chosen);
                e.Handled = true;
            }
        }
    }
}
