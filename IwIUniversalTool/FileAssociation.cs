using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace IwIUniversalTool
{
    /// <summary>
    /// Регистрация в Windows (только для текущего пользователя, права администратора не нужны):
    /// .iwi открывается этой программой по умолчанию, а IWI, VTF, DDS, TGA и PNG появляются в списке «Открыть с помощью».
    /// PNG, DDS и TGA по умолчанию не захватываются — у них остаётся ваша программа просмотра.
    /// </summary>
    internal static class FileAssociation
    {
        private const string ProgId = "IwIUniversalTool.Texture";
        private static readonly string[] Extensions = { ".iwi", ".vtf", ".dds", ".tga", ".png" };

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        // ---- миниатюры в проводнике (компонент IwiThumbs встроен в exe как ресурсы)
        private const string ThumbnailClsid = "{6C0E1B8A-3D6F-4B7B-9C58-0A4F2D1E7B91}";   // тот же GUID, что у IwiThumbnailProvider
        private const string ThumbnailHandlerKey = "{E357FCCD-A995-4576-B01F-234630154E96}"; // IThumbnailProvider
        private static readonly string[] ThumbnailExtensions = { ".iwi", ".vtf" };

        /// <summary>Файлы встроенного компонента миниатюр (ресурсы вида IwIUniversalTool.Thumbnails.*).</summary>
        private static readonly string[] ThumbnailComponentFiles =
        {
            "IwiThumbs.comhost.dll",
            "IwiThumbs.dll",
            "IwiThumbs.deps.json",
            "IwiThumbs.runtimeconfig.json"
        };

        private static string ThumbnailCacheDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IwIUniversalTool", "Thumbnails");

        /// <summary>
        /// Распаковывает встроенный компонент миниатюр в %LOCALAPPDATA%\IwIUniversalTool\Thumbnails
        /// и возвращает путь к comhost.dll. Благодаря этому рядом с exe не нужны никакие файлы.
        /// </summary>
        private static string? ExtractThumbnails()
        {
            try
            {
                var assembly = typeof(FileAssociation).Assembly;
                string dir = ThumbnailCacheDirectory;
                Directory.CreateDirectory(dir);

                bool any = false;
                foreach (string name in ThumbnailComponentFiles)
                {
                    using Stream? stream = assembly.GetManifestResourceStream("IwIUniversalTool.Thumbnails." + name);
                    if (stream == null) continue;
                    any = true;

                    string target = Path.Combine(dir, name);
                    if (File.Exists(target) && new FileInfo(target).Length == stream.Length) continue;

                    using FileStream file = File.Create(target);
                    stream.CopyTo(file);
                }

                string host = Path.Combine(dir, "IwiThumbs.comhost.dll");
                return any && File.Exists(host) ? host : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Ищет IwiThumbs.comhost.dll: сначала распакованный встроенный, затем рядом с программой.</summary>
        public static string? FindThumbnailHost()
        {
            string? extracted = ExtractThumbnails();
            if (extracted != null) return extracted;

            string? dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                foreach (string relative in new[]
                {
                    @"Thumbnails\IwiThumbs.comhost.dll",
                    @"IwiThumbs.comhost.dll",
                    @"IwiThumbs\bin\Release\net10.0-windows\IwiThumbs.comhost.dll"
                })
                {
                    string candidate = Path.Combine(dir, relative);
                    if (File.Exists(candidate)) return candidate;
                }
                dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
            }
            return null;
        }

        public static bool AreThumbnailsRegistered()
        {
            try
            {
                // Проводник загружает обработчики миниатюр только из общесистемной регистрации (HKLM):
                // запись только в HKEY_CURRENT_USER он игнорирует и показывает обычный значок.
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"Software\Classes\CLSID\" + ThumbnailClsid + @"\InprocServer32");
                return key != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Программа запущена с правами администратора?</summary>
        public static bool IsElevated()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(identity)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Проводник видит обработчик миниатюр только из HKLM, поэтому регистрация требует прав администратора.
        /// Если их нет, перезапускаем эту же программу с запросом UAC и специальным ключом, ждём результата.
        /// </summary>
        public static (bool ok, string error) RunThumbnailSetupElevated(bool register)
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return (false, "не удалось определить путь к программе");
            }

            try
            {
                var psi = new ProcessStartInfo(exe, register ? "--register-thumbnails" : "--unregister-thumbnails")
                {
                    UseShellExecute = true,
                    Verb = "runas" // запрос прав администратора
                };
                using Process? process = Process.Start(psi);
                if (process == null)
                {
                    return (false, "не удалось запустить программу с правами администратора");
                }
                if (!process.WaitForExit(120000))
                {
                    try { process.Kill(); } catch { }
                    return (false, "запрос прав администратора не завершился");
                }
                if (process.ExitCode != 0)
                {
                    return (false, "регистрация отменена или не удалась (код " + process.ExitCode + ")");
                }
                bool ok = register ? AreThumbnailsRegistered() : !AreThumbnailsRegistered();
                return (ok, ok ? "" : "регистрация не сохранилась");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
            {
                return (false, "UAC_CANCELLED");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>Включает миниатюры .iwi и .vtf в проводнике (нужны права администратора: запись в HKLM).</summary>
        public static bool RegisterThumbnails(out string error)
        {
            error = "";
            try
            {
                string? host = FindThumbnailHost();
                if (host == null)
                {
                    error = "NOT_BUILT";
                    return false;
                }

                if (!IsElevated())
                {
                    error = "NEED_ADMIN";
                    return false;
                }

                // обработчик миниатюр должен быть зарегистрирован для всех пользователей (HKLM),
                // иначе проводник не загружает его и рисует обычный значок
                using (RegistryKey clsid = Registry.LocalMachine.CreateSubKey(@"Software\Classes\CLSID\" + ThumbnailClsid))
                {
                    clsid.SetValue("", "IwI Universal Tool thumbnails");
                    using RegistryKey server = clsid.CreateSubKey("InprocServer32");
                    server.SetValue("", host);
                    server.SetValue("ThreadingModel", "Both");
                }

                // список разрешённых расширений оболочки
                using (RegistryKey approved = Registry.LocalMachine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"))
                    approved.SetValue(ThumbnailClsid, "IwI Universal Tool thumbnails");

                // прежняя регистрация только для текущего пользователя больше не нужна и может мешать:
                // при переезде папки Thumbnails она указывала бы на старый путь
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\" + ThumbnailClsid, false);
                    using RegistryKey? approvedUser = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", true);
                    approvedUser?.DeleteValue(ThumbnailClsid, false);
                }
                catch { }

                foreach (string ext in ThumbnailExtensions)
                {
                    using RegistryKey handler = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext + @"\ShellEx\" + ThumbnailHandlerKey);
                    handler.SetValue("", ThumbnailClsid);
                }
                // у .iwi есть свой ProgID (см. Register): обработчик нужен и там, иначе его может перебить связь с программой
                using (RegistryKey handler = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId + @"\ShellEx\" + ThumbnailHandlerKey))
                    handler.SetValue("", ThumbnailClsid);

                // проводник проверяет и общий раздел SystemFileAssociations
                foreach (string ext in ThumbnailExtensions)
                {
                    using RegistryKey handler = Registry.CurrentUser.CreateSubKey(@"Software\Classes\SystemFileAssociations\" + ext + @"\ShellEx\" + ThumbnailHandlerKey);
                    handler.SetValue("", ThumbnailClsid);
                }

                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                error = "NEED_ADMIN";
                return false;
            }
            catch (System.Security.SecurityException)
            {
                error = "NEED_ADMIN";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool UnregisterThumbnails(out string error)
        {
            error = "";
            try
            {
                if (!IsElevated())
                {
                    error = "NEED_ADMIN";
                    return false;
                }

                Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\CLSID\" + ThumbnailClsid, false);
                using (RegistryKey? approved = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", true))
                    approved?.DeleteValue(ThumbnailClsid, false);

                foreach (string ext in ThumbnailExtensions)
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ext + @"\ShellEx\" + ThumbnailHandlerKey, false);
                foreach (string ext in ThumbnailExtensions)
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\" + ext + @"\ShellEx\" + ThumbnailHandlerKey, false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId + @"\ShellEx\" + ThumbnailHandlerKey, false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\" + ThumbnailClsid, false);
                using (RegistryKey? approvedUser = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", true))
                    approvedUser?.DeleteValue(ThumbnailClsid, false);

                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                error = "NEED_ADMIN";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool Register(out string error)
        {
            error = "";
            try
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0) throw new InvalidOperationException("не удалось определить путь к программе");

                using (RegistryKey prog = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
                {
                    prog.SetValue("", "IwI Universal Tool texture");
                    using (RegistryKey icon = prog.CreateSubKey("DefaultIcon"))
                        icon.SetValue("", "\"" + exe + "\",0");
                    using (RegistryKey cmd = prog.CreateSubKey(@"shell\open\command"))
                        cmd.SetValue("", "\"" + exe + "\" \"%1\"");
                }

                foreach (string ext in Extensions)
                {
                    using RegistryKey withProgIds = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext + @"\OpenWithProgids");
                    withProgIds.SetValue(ProgId, new byte[0], RegistryValueKind.None);
                }

                // .iwi — формат игры, другой программы для него обычно нет: назначаем по умолчанию
                using (RegistryKey iwi = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.iwi"))
                    iwi.SetValue("", ProgId);

                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool Unregister(out string error)
        {
            error = "";
            try
            {
                foreach (string ext in Extensions)
                {
                    using (RegistryKey? withProgIds = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext + @"\OpenWithProgids", true))
                        withProgIds?.DeleteValue(ProgId, false);
                }

                using (RegistryKey? iwi = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.iwi", true))
                {
                    if (iwi != null && string.Equals(iwi.GetValue("") as string, ProgId, StringComparison.Ordinal))
                        iwi.DeleteValue("", false);
                }

                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false);
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    

        public static string ThumbnailLogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IwIUniversalTool", "thumbs.log");

        /// <summary>
        /// Проверка компонента миниатюр по шагам: регистрация, наличие файлов и загрузка COM-объекта в отдельном процессе
        /// (так же, как это делает проводник), плюс последние строки журнала компонента.
        /// </summary>
        public static string RunThumbnailSelfTest(bool ru)
        {
            string T(string a, string b) => ru ? a : b;
            var sb = new StringBuilder();

            if (!AreThumbnailsRegistered())
            {
                return T("✕ Миниатюры не включены. Нажмите «Включить миниатюры» (Windows один раз спросит права администратора).",
                         "✕ Thumbnails are not enabled. Press \"Enable thumbnails\" (Windows will ask for administrator rights once).");
            }
            sb.AppendLine(T("✓ Зарегистрировано в системе (HKLM).", "✓ Registered system-wide (HKLM)."));

            string? host = null;
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"Software\Classes\CLSID\" + ThumbnailClsid + @"\InprocServer32");
                host = key?.GetValue("") as string;
            }
            catch { }

            if (string.IsNullOrEmpty(host) || !File.Exists(host))
            {
                sb.Append(T("✕ Файл компонента не найден: ", "✕ The component file was not found: ") + host + T(". Папку Thumbnails перемещали? Запустите BuildThumbnails.cmd и включите снова.", ". Was the Thumbnails folder moved? Run BuildThumbnails.cmd and enable again."));
                return sb.ToString();
            }

            string dir = Path.GetDirectoryName(host) ?? "";
            string[] missing = new[] { "IwiThumbs.dll", "IwiThumbs.runtimeconfig.json", "IwiThumbs.deps.json" }.Where(n => !File.Exists(Path.Combine(dir, n))).ToArray();
            if (missing.Length > 0)
            {
                sb.Append(T("✕ В папке компонента не хватает файлов: ", "✕ Files are missing in the component folder: ") + string.Join(", ", missing) + T(". Запустите BuildThumbnails.cmd заново.", ". Run BuildThumbnails.cmd again."));
                return sb.ToString();
            }
            sb.AppendLine(T("✓ Файлы компонента на месте: ", "✓ The component files are in place: ") + dir);

            // загрузка COM-объекта отдельным процессом (в основной программе нельзя: у неё уже запущена своя среда .NET)
            string script = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; "
                + "try { $t = [Type]::GetTypeFromCLSID([Guid]'" + ThumbnailClsid.Trim('{', '}') + "'); $o = [Activator]::CreateInstance($t); 'OK' } "
                + "catch { 'FAIL: ' + $_.Exception.GetBaseException().Message }";
            try
            {
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using Process? process = Process.Start(psi);
                if (process == null)
                {
                    sb.AppendLine(T("? Не удалось запустить PowerShell для проверки загрузки.", "? Could not start PowerShell to test loading."));
                }
                else if (!process.WaitForExit(30000))
                {
                    try { process.Kill(); } catch { }
                    sb.AppendLine(T("✕ Загрузка компонента не завершилась за 30 секунд.", "✕ Loading the component did not finish within 30 seconds."));
                }
                else
                {
                    string output = process.StandardOutput.ReadToEnd().Trim();
                    if (output == "OK")
                        sb.AppendLine(T("✓ Компонент загружается (среда .NET поднялась, объект создан).", "✓ The component loads (the .NET runtime started, the object was created)."));
                    else
                        sb.AppendLine(T("✕ Компонент не загрузился: ", "✕ The component failed to load: ") + (output.Length > 0 ? output : process.StandardError.ReadToEnd().Trim()));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine(T("? Проверка загрузки не выполнена: ", "? The loading test was not run: ") + ex.Message);
            }

            // последние строки журнала компонента
            try
            {
                if (File.Exists(ThumbnailLogPath))
                {
                    string[] lines = File.ReadAllLines(ThumbnailLogPath);
                    sb.AppendLine(T("Журнал компонента (последние строки):", "Component log (last lines):"));
                    foreach (string line in lines.Skip(Math.Max(0, lines.Length - 4))) sb.AppendLine("  " + line);
                }
                else
                {
                    sb.AppendLine(T("Журнал компонента пока пуст: к компоненту никто не обращался.", "The component log is empty: nothing has used the component yet."));
                }
            }
            catch { }

            return sb.ToString().TrimEnd();
        }
    }
}
