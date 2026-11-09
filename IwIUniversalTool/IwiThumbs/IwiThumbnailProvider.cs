using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using IwIUniversalTool;

namespace IwiThumbs
{
    /// <summary>
    /// Журнал компонента: %LOCALAPPDATA%\IwIUniversalTool\thumbs.log. Нужен, чтобы видеть, обращался ли проводник к компоненту
    /// и чем закончилось (проводник вызывает его из своего процесса, и иначе ошибок не видно).
    /// </summary>
    internal static class ThumbLog
    {
        private static readonly object Gate = new();

        public static void Write(string message)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IwIUniversalTool");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "thumbs.log");
                lock (Gate)
                {
                    var info = new FileInfo(file);
                    if (info.Exists && info.Length > 256 * 1024) File.WriteAllText(file, "");
                    File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + Environment.ProcessId + "] " + message + Environment.NewLine);
                }
            }
            catch
            {
                // журнал не должен ломать показ миниатюр
            }
        }
    }

    [ComImport]
    [Guid("e357fccd-a995-4576-b01f-234630154e96")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IThumbnailProvider
    {
        [PreserveSig]
        int GetThumbnail(uint cx, out IntPtr phbmp, out uint pdwAlpha);
    }

    [ComImport]
    [Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInitializeWithStream
    {
        [PreserveSig]
        int Initialize(IStream pstream, uint grfMode);
    }

    /// <summary>
    /// Обработчик миниатюр для проводника Windows: показывает саму текстуру (.iwi и .vtf) в режимах «Обычные / Крупные / Огромные значки»,
    /// без запуска программы. Прозрачность (альфа-канал) сохраняется. GUID должен совпадать с FileAssociation.ThumbnailClsid в основной программе.
    /// </summary>
    [ComVisible(true)]
    [Guid("6C0E1B8A-3D6F-4B7B-9C58-0A4F2D1E7B91")]
    [ClassInterface(ClassInterfaceType.None)]
    [ProgId("IwIUniversalTool.IwiThumbnailProvider")]
    public sealed class IwiThumbnailProvider : IInitializeWithStream, IThumbnailProvider
    {
        private const int SOk = 0;
        private const int EFail = unchecked((int)0x80004005);
        private const uint WtsatArgb = 2;
        private const long MaxFileBytes = 256L * 1024 * 1024;

        private static int _logged;
        private byte[]? _data;

        public IwiThumbnailProvider()
        {
            string process = "?";
            try { process = System.Diagnostics.Process.GetCurrentProcess().ProcessName; } catch { }
            ThumbLog.Write("Компонент загружен в процессе «" + process + "».");
        }

        public int Initialize(IStream pstream, uint grfMode)
        {
            try
            {
                // Stat/Seek через прокси-поток в отдельном процессе (dllhost) может зависнуть,
                // поэтому размер не запрашиваем вообще: просто читаем поток до конца.
                using var buffer = new MemoryStream();
                var chunk = new byte[64 * 1024];
                IntPtr readPtr = Marshal.AllocCoTaskMem(sizeof(int));
                try
                {
                    while (buffer.Length < MaxFileBytes)
                    {
                        int want = (int)Math.Min(chunk.Length, MaxFileBytes - buffer.Length);
                        Marshal.WriteInt32(readPtr, 0);
                        pstream.Read(chunk, want, readPtr);
                        int got = Marshal.ReadInt32(readPtr);
                        if (got <= 0) break;
                        buffer.Write(chunk, 0, got);
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(readPtr);
                }

                if (buffer.Length == 0)
                {
                    ThumbLog.Write("Поток пуст, прочитано 0 байт.");
                    return EFail;
                }

                _data = buffer.ToArray();
                ThumbLog.Write("Прочитано " + _data.Length + " байт.");
                return SOk;
            }
            catch (Exception ex)
            {
                ThumbLog.Write("Ошибка чтения потока: " + ex);
                return EFail;
            }
        }

        public int GetThumbnail(uint cx, out IntPtr phbmp, out uint pdwAlpha)
        {
            phbmp = IntPtr.Zero;
            pdwAlpha = 0;
            try
            {
                byte[]? data = _data;
                if (data == null || data.Length < 8)
                {
                    ThumbLog.Write("Нет данных файла (Initialize не вызывался или файл пуст).");
                    return EFail;
                }

                // формат определяем по сигнатуре: у потока нет имени файла
                byte[] dds;
                if (data[0] == (byte)'I' && data[1] == (byte)'W' && data[2] == (byte)'i') dds = IwiCodec.IwiToDds(data);
                else if (VtfCodec.IsVtf(data)) dds = VtfCodec.VtfToDds(data);
                else
                {
                    ThumbLog.Write("Неизвестная сигнатура файла.");
                    return EFail;
                }

                TextureBitmap.DecodeTopLevel(dds, out int width, out int height, out byte[] bgra);
                int side = cx == 0 ? 256 : (int)Math.Min(cx, 4096u);
                byte[] pixels = TextureBitmap.FitPremultiplied(bgra, width, height, side, out int w, out int h);

                IntPtr bitmap = CreateBitmap(pixels, w, h);
                if (bitmap == IntPtr.Zero)
                {
                    ThumbLog.Write("CreateDIBSection вернул 0 (нет GDI-ресурсов?), миниатюра не создана.");
                    return EFail;
                }

                phbmp = bitmap;
                pdwAlpha = WtsatArgb;
                if (System.Threading.Interlocked.Increment(ref _logged) <= 20)
                    ThumbLog.Write("Миниатюра готова: " + width + "x" + height + " -> " + w + "x" + h + " (запрошено " + cx + ").");
                return SOk;
            }
            catch (Exception ex)
            {
                ThumbLog.Write("Не удалось сделать миниатюру: " + ex);
                return EFail;
            }
        }

        // ---- GDI: 32-битный DIB снизу вверх, premultiplied ARGB

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader pbmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        private static IntPtr CreateBitmap(byte[] pixels, int w, int h)
        {
            var header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = w,
                Height = h, // положительная высота: строки идут снизу вверх
                Planes = 1,
                BitCount = 32,
                Compression = 0 // BI_RGB
            };

            IntPtr hBitmap = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;

            int stride = w * 4;
            for (int y = 0; y < h; y++)
                Marshal.Copy(pixels, (h - 1 - y) * stride, IntPtr.Add(bits, y * stride), stride);
            return hBitmap;
        }
    }
}
