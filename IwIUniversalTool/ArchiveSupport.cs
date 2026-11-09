using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace IwIUniversalTool
{
    /// <summary>
    /// Работа с архивами: ZIP, 7Z, RAR и IWD (IWD у Call of Duty — это обычный ZIP с другим расширением).
    /// Файл внутри архива в программе имеет «виртуальный путь»:  C:\mods\iw_00.iwd&gt;images/foo.iwi
    /// (символ '&gt;' нельзя использовать в именах файлов Windows, поэтому путаницы с обычными путями не будет).
    /// </summary>
    internal static class ArchiveSupport
    {
        public const char Sep = '>';

        public static readonly HashSet<string> ArchiveExts = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar", ".iwd", ".ipak" };

        // ---- IPAK (Black Ops 2): внутри лежат IWI, но имён нет — только хеши имён. Такие текстуры называем по хешу: 0xA1B2C3D4.iwi
        public static bool IsIpak(string path) => string.Equals(Path.GetExtension(path), ".ipak", StringComparison.OrdinalIgnoreCase);

        private static string IpakKey(uint nameHash) => "0x" + nameHash.ToString("X8") + ".iwi";

        private static bool TryParseIpakKey(string key, out uint nameHash)
        {
            string name = Path.GetFileNameWithoutExtension(key);
            if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) name = name.Substring(2);
            return uint.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out nameHash);
        }

        public static bool IsArchive(string path) => ArchiveExts.Contains(Path.GetExtension(path));

        public static string MakeVirtualPath(string archivePath, string entryKey) => archivePath + Sep + entryKey;

        /// <summary>Разбирает виртуальный путь «архив&gt;файл». Для обычного пути возвращает false.</summary>
        public static bool TrySplit(string path, out string archivePath, out string entryKey)
        {
            int i = path.IndexOf(Sep);
            if (i > 0 && i < path.Length - 1)
            {
                archivePath = path.Substring(0, i);
                entryKey = path.Substring(i + 1);
                return true;
            }
            archivePath = path;
            entryKey = "";
            return false;
        }

        /// <summary>Настоящий путь на диске: для файла из архива — сам архив.</summary>
        public static string RealPath(string path) => TrySplit(path, out string archive, out _) ? archive : path;

        /// <summary>Путь внутри архива в едином виде: прямые слэши, без начального и конечного слэша.</summary>
        public static string NormalizeKey(string key) => key.Replace('\\', '/').Trim('/');

        /// <summary>Имя папки для результата: имя архива без расширения (iw_00.iwd → iw_00).</summary>
        public static string OutputFolderName(string archivePath)
        {
            string name = Path.GetFileNameWithoutExtension(archivePath);
            var invalid = Path.GetInvalidFileNameChars();
            name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            return name.Length == 0 ? "archive" : name;
        }

        /// <summary>
        /// Подпапки файла внутри архива (images/foo.iwi → «images»). Служебные «..» и недопустимые символы убираются,
        /// чтобы хитрый архив не смог записать файл за пределы папки результата.
        /// </summary>
        public static string SafeRelativeDir(string entryKey)
        {
            string[] parts = NormalizeKey(entryKey).Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1) return "";

            var invalid = Path.GetInvalidFileNameChars();
            var clean = new List<string>();
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string part = parts[i].Trim();
                if (part == "." || part == "..") continue;
                part = new string(part.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
                if (part.Length > 0) clean.Add(part);
            }
            return clean.Count == 0 ? "" : Path.Combine(clean.ToArray());
        }

        /// <summary>Список подходящих файлов в архиве (без распаковки — читается только оглавление).</summary>
        public static List<(string Key, long Size)> ListFiles(string archivePath, HashSet<string> wantedExts, out string format)
        {
            var result = new List<(string Key, long Size)>();

            if (IsIpak(archivePath))
            {
                format = "IPAK";
                if (!wantedExts.Contains(".iwi")) return result;
                using var ipak = IpakReader.Open(archivePath);
                var seen = new HashSet<uint>();
                foreach (IpakIndexEntry entry in ipak.Index)
                {
                    if (seen.Add(entry.NameHash)) result.Add((IpakKey(entry.NameHash), entry.Size));
                }
                return result;
            }

            using var archive = ArchiveFactory.OpenArchive(archivePath);
            format = archive.Type.ToString() + (archive.IsSolid ? ", solid" : "");   // для журнала: как библиотека распознала архив
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory) continue;
                string key = NormalizeKey(entry.Key ?? "");
                if (key.Length == 0 || !wantedExts.Contains(Path.GetExtension(key))) continue;
                result.Add((key, entry.Size));
            }
            return result;
        }

        /// <summary>Читает один файл из архива (для предпросмотра).</summary>
        public static byte[] ReadEntry(string archivePath, string entryKey)
        {
            if (IsIpak(archivePath))
            {
                if (TryParseIpakKey(entryKey, out uint hash))
                {
                    using var ipak = IpakReader.Open(archivePath);
                    foreach (IpakIndexEntry entry in ipak.Index)
                    {
                        if (entry.NameHash == hash) return ipak.ReadImage(entry.Offset, entry.Size);
                    }
                }
                throw new FileNotFoundException($"'{entryKey}' not found in '{Path.GetFileName(archivePath)}'");
            }

            using var archive = ArchiveFactory.OpenArchive(archivePath);
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory) continue;
                if (!string.Equals(NormalizeKey(entry.Key ?? ""), entryKey, StringComparison.OrdinalIgnoreCase)) continue;

                using var stream = entry.OpenEntryStream();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
            throw new FileNotFoundException($"'{entryKey}' not found in '{Path.GetFileName(archivePath)}'");
        }

        /// <summary>Читает файл по обычному или по виртуальному пути.</summary>
        public static byte[] ReadAllBytes(string path)
            => TrySplit(path, out string archive, out string key) ? ReadEntry(archive, key) : File.ReadAllBytes(path);

        /// <summary>
        /// Распаковывает в папку только нужные файлы за один проход по архиву.
        /// Для «солидных» архивов (7Z, часть RAR) только такой проход быстрый — отдельное чтение каждого файла было бы очень медленным.
        /// </summary>
        internal static Dictionary<string, string> ExtractTo(string archivePath, HashSet<string> wantedKeys, string destDir, CancellationToken token)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(destDir);
            int counter = 0;

            void Save(string key, Stream source)
            {
                // имя на диске придумываем сами — имя из архива в путь не попадает
                string file = Path.Combine(destDir, (counter++).ToString() + Path.GetExtension(key));
                using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                    source.CopyTo(fs);
                map[key] = file;
            }

            if (IsIpak(archivePath))
            {
                using var ipak = IpakReader.Open(archivePath);
                var byHash = new Dictionary<uint, IpakIndexEntry>();
                foreach (IpakIndexEntry entry in ipak.Index) byHash.TryAdd(entry.NameHash, entry);

                foreach (string key in wantedKeys)
                {
                    token.ThrowIfCancellationRequested();
                    if (map.ContainsKey(key) || !TryParseIpakKey(key, out uint hash) || !byHash.TryGetValue(hash, out IpakIndexEntry found)) continue;
                    using var data = new MemoryStream(ipak.ReadImage(found.Offset, found.Size));
                    Save(key, data);
                }
                return map;
            }

            using var archive = ArchiveFactory.OpenArchive(archivePath);

            if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
            {
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.Entry.IsDirectory) continue;
                    string key = NormalizeKey(reader.Entry.Key ?? "");
                    if (!wantedKeys.Contains(key) || map.ContainsKey(key)) continue;

                    using var stream = reader.OpenEntryStream();
                    Save(key, stream);
                }
            }
            else
            {
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.IsDirectory) continue;
                    string key = NormalizeKey(entry.Key ?? "");
                    if (!wantedKeys.Contains(key) || map.ContainsKey(key)) continue;

                    using var stream = entry.OpenEntryStream();
                    Save(key, stream);
                }
            }
            return map;
        }
    }

    /// <summary>
    /// Распаковывает файлы из архивов во временную папку по мере надобности.
    /// Каждый архив открывается один раз: первый поток, которому понадобился файл из архива, распаковывает сразу все нужные
    /// файлы этого архива, остальные потоки ждут. Если архив повреждён или запаролен, ошибка запоминается и повторно не мучает диск.
    /// </summary>
    internal sealed class ArchiveExtractor : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "IwIUniversalTool", Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, HashSet<string>> _wanted = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Lazy<Dictionary<string, string>>> _extracted = new(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationToken _token;
        private int _folderId;

        public bool HasArchives => _wanted.Count > 0;

        public ArchiveExtractor(IEnumerable<TextureFile> files, CancellationToken token)
        {
            _token = token;
            foreach (var f in files)
            {
                if (f.ArchivePath == null || f.EntryPath == null) continue;
                if (!_wanted.TryGetValue(f.ArchivePath, out var set))
                    _wanted[f.ArchivePath] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(f.EntryPath);
            }
        }

        /// <summary>Путь к временному файлу с содержимым. Вызывающий сам удаляет файл, когда прочитал его.</summary>
        public string GetFile(string archivePath, string entryKey)
        {
            var lazy = _extracted.GetOrAdd(archivePath, a => new Lazy<Dictionary<string, string>>(
                () => Extract(a), LazyThreadSafetyMode.ExecutionAndPublication));

            if (lazy.Value.TryGetValue(entryKey, out string? file)) return file;
            throw new FileNotFoundException($"'{entryKey}' not found in '{Path.GetFileName(archivePath)}'");
        }

        private Dictionary<string, string> Extract(string archivePath)
        {
            var wanted = _wanted.TryGetValue(archivePath, out var w) ? w : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string dir = Path.Combine(_root, Interlocked.Increment(ref _folderId).ToString());
            return ArchiveSupport.ExtractTo(archivePath, wanted, dir, _token);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }
    }
}
