using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace IwIUniversalTool
{
    /// <summary>
    /// Автоматический поиск папок игр Call of Duty: библиотеки Steam (через реестр и libraryfolders.vdf)
    /// и несколько типовых мест. Индекс игры такой же, как в списке версий: 0 Black Ops 2, 1 Modern Warfare 3, 2 Black Ops,
    /// 3 Modern Warfare 2, 4 World at War, 5 Modern Warfare 1 (CoD4), 6 Call of Duty 2.
    /// </summary>
    internal static class GameLocator
    {
        public const int GameCount = 7;

        /// <summary>Ищет папку одной игры. null — не найдена.</summary>
        public static string? Detect(int versionIndex) => DetectAll(versionIndex)[versionIndex];

        /// <summary>Ищет папки всех игр сразу (за один проход по библиотекам).</summary>
        public static string?[] DetectAll() => DetectAll(-1);

        private static string?[] DetectAll(int onlyIndex)
        {
            var result = new string?[GameCount];
            foreach (string root in CandidateRoots())
            {
                string[] folders;
                try { folders = Directory.GetDirectories(root); }
                catch { continue; }

                foreach (string folder in folders)
                {
                    string name = Path.GetFileName(folder).ToLowerInvariant();
                    for (int i = 0; i < GameCount; i++)
                    {
                        if (result[i] != null) continue;
                        if (onlyIndex >= 0 && i != onlyIndex) continue;
                        if (Matches(i, name)) result[i] = NormalizePath(folder);
                    }
                }
            }
            return result;
        }

        // Имя папки должно совпасть с игрой целиком: «Black Ops II» не подходит под «Black Ops III», а «Black Ops» — под «Black Ops 4».
        private static readonly Regex[] GamePatterns =
        {
            new Regex(@"black ops\s*(?:ii|2)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),            // 0 Black Ops 2
            new Regex(@"modern warfare\s*(?:3|iii)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),      // 1 Modern Warfare 3
            new Regex(@"black ops(?!\s*(?:i{2,}|iv|\d))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),            // 2 Black Ops (первая)
            new Regex(@"modern warfare\s*2(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),                 // 3 Modern Warfare 2 (2009)
            new Regex(@"world at war", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),                                // 4 World at War
            new Regex(@"call of duty\s*4(?![0-9])|cod4", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),              // 5 Modern Warfare 1 (CoD4)
            new Regex(@"^call of duty 2(?![0-9a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)                   // 6 Call of Duty 2
        };

        /// <summary>Подходит ли имя папки игре с этим индексом.</summary>
        private static bool Matches(int index, string name)
        {
            if (index < 0 || index >= GamePatterns.Length) return false;
            if (name.Contains("remaster", StringComparison.OrdinalIgnoreCase) || name.Contains("cold war", StringComparison.OrdinalIgnoreCase)) return false;
            return GamePatterns[index].IsMatch(name);
        }

        /// <summary>
        /// Приводит путь к настоящему написанию папок и букве диска в верхнем регистре:
        /// реестр Steam отдаёт «c:/program files (x86)/steam», а нужно «C:\Program Files (x86)\Steam».
        /// Несуществующие части пути остаются как есть.
        /// </summary>
        public static string NormalizePath(string path)
        {
            try
            {
                string full = Path.GetFullPath(path.Trim().Trim('"'));
                string root = Path.GetPathRoot(full) ?? "";
                if (root.Length == 0) return full;

                string current = root;
                if (root.Length >= 2 && root[1] == ':') current = char.ToUpperInvariant(root[0]) + root.Substring(1);

                string rest = full.Substring(root.Length);
                foreach (string part in rest.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string real = part;
                    try
                    {
                        foreach (FileSystemInfo entry in new DirectoryInfo(current).EnumerateFileSystemInfos(part))
                        {
                            real = entry.Name;
                            break;
                        }
                    }
                    catch { }
                    current = Path.Combine(current, real);
                }
                return current;
            }
            catch
            {
                return path;
            }
        }

        /// <summary>Папки, внутри которых лежат папки игр: steamapps\common всех библиотек Steam и несколько типовых мест.</summary>
        private static IEnumerable<string> CandidateRoots()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();

            void Add(string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                string full;
                try { full = Path.GetFullPath(path); }
                catch { return; }
                if (Directory.Exists(full) && seen.Add(full)) list.Add(full);
            }

            foreach (string steam in SteamRoots())
            {
                Add(Path.Combine(steam, "steamapps", "common"));

                // дополнительные библиотеки Steam
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                try
                {
                    if (File.Exists(vdf))
                    {
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            Add(Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps", "common"));
                    }
                }
                catch { }
            }

            // типовые места: обычная установка и библиотеки Steam на других дисках
            Add(@"C:\Program Files (x86)\Activision");
            Add(@"C:\Program Files\Activision");
            Add(@"C:\Program Files (x86)\Call of Duty");
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    string r = drive.RootDirectory.FullName;
                    Add(Path.Combine(r, "SteamLibrary", "steamapps", "common"));
                    Add(Path.Combine(r, "Steam", "steamapps", "common"));
                    Add(Path.Combine(r, "Games"));
                    Add(Path.Combine(r, "Program Files (x86)", "Steam", "steamapps", "common"));
                }
            }
            catch { }

            return list;
        }

        private static IEnumerable<string> SteamRoots()
        {
            var roots = new List<string>();
            try
            {
                using (RegistryKey? k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    if (k?.GetValue("SteamPath") is string p && p.Length > 0) roots.Add(p.Replace('/', '\\'));
            }
            catch { }
            try
            {
                using (RegistryKey? k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                    if (k?.GetValue("InstallPath") is string p && p.Length > 0) roots.Add(p);
            }
            catch { }
            roots.Add(@"C:\Program Files (x86)\Steam");
            roots.Add(@"C:\Program Files\Steam");
            return roots;
        }
    }
}