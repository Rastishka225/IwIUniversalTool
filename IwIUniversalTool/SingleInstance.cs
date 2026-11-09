using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IwIUniversalTool
{
    /// <summary>
    /// Если программа уже запущена, а в проводнике открыли ещё один файл (двойной клик, «Открыть с помощью»),
    /// путь передаётся в работающее окно, а не открывается вторая копия программы.
    /// </summary>
    internal static class SingleInstance
    {
        public const string MutexName = @"Local\IwIUniversalTool.SingleInstance.v1";
        private const string PipeName = "IwIUniversalTool.OpenFiles.v1";

        /// <summary>Отправляет пути в уже работающую копию. false — работающей копии нет или она не ответила.</summary>
        public static bool Send(string[] files)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(3000);
                using var writer = new StreamWriter(client, new UTF8Encoding(false));
                foreach (string f in files)
                {
                    string full;
                    try { full = Path.GetFullPath(f); }
                    catch { continue; }
                    writer.WriteLine(full);
                }
                writer.Flush();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Слушает пути от других копий. onFiles вызывается из фонового потока.</summary>
        public static void StartServer(Action<string[]> onFiles, CancellationToken token)
        {
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                        using var reader = new StreamReader(server, Encoding.UTF8);
                        var list = new List<string>();
                        string? line;
                        while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) != null)
                        {
                            if (line.Length > 0) list.Add(line);
                        }
                        if (list.Count > 0) onFiles(list.ToArray());
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        try { await Task.Delay(200, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }, token);
        }
    }
}
