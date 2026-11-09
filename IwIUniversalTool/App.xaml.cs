using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace IwIUniversalTool
{
    /// <summary>
    /// Запуск программы. Если файл открыт двойным кликом или через «Открыть с помощью», а программа уже работает,
    /// путь уходит в работающее окно (вторая копия не открывается).
    /// </summary>
    public partial class App : Application
    {
        private Mutex? _instanceMutex;
        private CancellationTokenSource? _pipeCts;
        private MainWindow? _mainWin;

        public App()
        {
            // если запуск сорвался, пишем причину в журнал запуска, а не молчим
            AppDomain.CurrentDomain.UnhandledException += (_, args) => Log("AppDomain: " + args.ExceptionObject);
            DispatcherUnhandledException += (_, args) => Log("Dispatcher: " + args.Exception);
        }

        /// <summary>Журнал запуска: %LOCALAPPDATA%\IwIUniversalTool\startup.log.</summary>
        private static void Log(string message)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IwIUniversalTool");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "startup.log");
                var info = new FileInfo(file);
                if (info.Exists && info.Length > 128 * 1024) info.Delete();
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + Environment.ProcessId + "] " + message + Environment.NewLine);
            }
            catch
            {
                // журнал не должен мешать запуску
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Log("start args=" + string.Join("|", e.Args));

            try
            {
                // Служебный режим: регистрация миниатюр в проводнике требует прав администратора (запись в HKLM).
                // Окно не открывается: программа делает работу и сразу закрывается с кодом 0/1.
                if (e.Args.Length > 0 && (e.Args[0] == "--register-thumbnails" || e.Args[0] == "--unregister-thumbnails"))
                {
                    bool ok = e.Args[0] == "--register-thumbnails"
                        ? FileAssociation.RegisterThumbnails(out _)
                        : FileAssociation.UnregisterThumbnails(out _);
                    Log("service mode: " + (ok ? "ok" : "failed"));
                    Shutdown(ok ? 0 : 1);
                    return;
                }

                bool first = true;
                try { _instanceMutex = new Mutex(true, SingleInstance.MutexName, out first); }
                catch { first = true; }

                // файлы переданы, а программа уже запущена: отдаём их работающей копии и выходим
                if (!first && e.Args.Length > 0 && SingleInstance.Send(e.Args))
                {
                    Log("sent to running instance, exit");
                    Shutdown();
                    return;
                }

                if (first)
                {
                    _pipeCts = new CancellationTokenSource();
                    SingleInstance.StartServer(files => Dispatcher.BeginInvoke(new Action(() => _mainWin?.OpenExternalFiles(files))), _pipeCts.Token);
                }

                _mainWin = new MainWindow();
                MainWindow = _mainWin;
                _mainWin.Show();
                Log("window shown, first=" + first);

                // файл, с которым запустили программу (двойной клик, «Открыть с помощью»): добавляем в список, показываем превью
                // и/или конвертируем по настройкам. Ждём первой отрисовки окна, чтобы оно успело полностью загрузиться.
                if (e.Args.Length > 0)
                {
                    string[] startFiles = e.Args;
                    _mainWin.ContentRendered += (_, _) =>
                    {
                        Log("open files: " + string.Join("|", startFiles));
                        _mainWin?.OpenExternalFiles(startFiles);
                    };
                }
            }
            catch (Exception ex)
            {
                Log("FAIL " + ex);
                MessageBox.Show(ex.ToString(), "IwI Universal Tool — ошибка запуска");
                Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _pipeCts?.Cancel(); } catch { }
            try { _instanceMutex?.ReleaseMutex(); } catch { }
            Log("exit code=" + e.ApplicationExitCode);
            base.OnExit(e);
        }
    }
}