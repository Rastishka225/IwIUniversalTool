using System;
using System.Buffers.Binary;
using System.IO;

namespace IwIUniversalTool
{
    /// <summary>Разобранный заголовок VTF (Valve Texture Format, версии 7.0–7.5).</summary>
    public sealed record VtfHeader(
        int Major, int Minor, int HeaderSize,
        int Width, int Height, uint Flags,
        int Frames, int Faces, int Depth, int MipCount,
        int HighFormat, int LowFormat, int LowWidth, int LowHeight,
        long HighDataOffset);

    /// <summary>
    /// Чтение текстур Valve Source (CS:GO, CS:Source, TF2, Half-Life 2, GMod, L4D2).
    /// Берётся верхний уровень (мип 0, кадр 0, грань 0) и упаковывается в обычный DDS.
    /// </summary>
    public static class VtfCodec
    {
        private static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o, 2));
        private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o, 4));
        private static void W32(Span<byte> s, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o, 4), v);

        public static bool IsVtf(byte[] d)
            => d.Length >= 4 && d[0] == (byte)'V' && d[1] == (byte)'T' && d[2] == (byte)'F' && d[3] == 0;

        // ------------------------------------------------------------------ форматы пикселей

        /// <summary>Названия форматов по таблице Valve (IMAGE_FORMAT_*).</summary>
        public static string FormatName(int f) => f switch
        {
            0 => "RGBA8888",
            1 => "ABGR8888",
            2 => "RGB888",
            3 => "BGR888",
            4 => "RGB565",
            5 => "I8",
            6 => "IA88",
            7 => "P8",
            8 => "A8",
            11 => "ARGB8888",
            12 => "BGRA8888",
            13 => "DXT1",
            14 => "DXT3",
            15 => "DXT5",
            16 => "BGRX8888",
            17 => "BGR565",
            20 => "DXT1 (1-bit alpha)",
            _ => $"#{f}"
        };

        private static bool IsCompressed(int f) => f is 13 or 14 or 15 or 20;

        private static int BytesPerPixel(int f) => f switch
        {
            0 or 1 or 11 or 12 or 16 => 4,
            2 or 3 => 3,
            4 or 6 or 17 => 2,
            5 or 8 => 1,
            _ => 0
        };

        public static bool IsSupportedFormat(int f) => IsCompressed(f) || BytesPerPixel(f) > 0;

        /// <summary>Размер одного изображения заданного формата в байтах.</summary>
        public static long ImageSize(int format, int w, int h)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            if (IsCompressed(format))
            {
                int block = format == 13 || format == 20 ? 8 : 16;
                return (long)((w + 3) / 4) * ((h + 3) / 4) * block;
            }

            int bpp = BytesPerPixel(format);
            if (bpp == 0)
                throw new InvalidDataException($"Формат пикселей VTF {FormatName(format)} не поддерживается.");
            return (long)w * h * bpp;
        }

        // ------------------------------------------------------------------ заголовок

        public static VtfHeader ParseHeader(byte[] d)
        {
            if (!IsVtf(d))
            {
                string head = BitConverter.ToString(d, 0, Math.Min(4, d.Length)).Replace('-', ' ');
                throw new InvalidDataException($"Это не VTF: файл начинается с {head}, а должен — с 56 54 46 00 («VTF»).");
            }
            if (d.Length < 64)
                throw new InvalidDataException($"Файл слишком короткий для VTF ({d.Length} байт).");

            uint major = U32(d, 4), minor = U32(d, 8);
            if (major != 7 || minor > 5)
                throw new InvalidDataException($"Версия VTF {major}.{minor} не поддерживается (нужна 7.0–7.5).");

            int headerSize = (int)U32(d, 12);
            int width = U16(d, 16), height = U16(d, 18);
            uint flags = U32(d, 20);
            int frames = Math.Max(1, (int)U16(d, 24));
            int highFormat = (int)U32(d, 52);
            int mips = Math.Max(1, (int)d[56]);
            int lowFormat = (int)U32(d, 57);   // -1, если уменьшенной копии нет
            int lowWidth = d[61], lowHeight = d[62];
            int depth = minor >= 2 && d.Length >= 65 ? Math.Max(1, (int)U16(d, 63)) : 1;

            if (width == 0 || height == 0)
                throw new InvalidDataException("В заголовке VTF нулевой размер картинки.");
            if (headerSize < 64 || headerSize > d.Length)
                throw new InvalidDataException("Заголовок VTF повреждён: неверный размер заголовка.");

            // Кубические карты: в версиях до 7.5 лежит ещё одна «шестая с половиной» грань (spheremap)
            bool cubemap = (flags & 0x4000u) != 0;
            int faces = cubemap ? (minor >= 5 ? 6 : 7) : 1;

            // Где лежат данные основного изображения
            long highOffset = -1;
            if (minor >= 3 && d.Length >= 80)
            {
                uint resources = U32(d, 68);
                for (uint i = 0; i < resources && i < 32; i++)
                {
                    int e = 80 + (int)i * 8;
                    if (e + 8 > d.Length) break;
                    if (d[e] == 0x30 && d[e + 1] == 0 && d[e + 2] == 0) // ресурс «данные изображения»
                    {
                        highOffset = U32(d, e + 4);
                        break;
                    }
                }
            }
            if (highOffset < 0)
            {
                long lowSize = 0;
                if (lowFormat >= 0 && lowWidth > 0 && lowHeight > 0)
                {
                    try { lowSize = ImageSize(lowFormat, lowWidth, lowHeight); }
                    catch (InvalidDataException) { lowSize = 0; }
                }
                highOffset = headerSize + lowSize;
            }

            return new VtfHeader((int)major, (int)minor, headerSize, width, height, flags, frames, faces, depth, mips,
                                 highFormat, lowFormat, lowWidth, lowHeight, highOffset);
        }

        // ------------------------------------------------------------------ VTF → DDS

        /// <summary>Достаёт верхний уровень (мип 0, кадр 0, грань 0) и упаковывает его в DDS в памяти.</summary>
        public static byte[] VtfToDds(byte[] vtf)
        {
            VtfHeader h = ParseHeader(vtf);
            if (!IsSupportedFormat(h.HighFormat))
                throw new InvalidDataException($"Формат пикселей VTF {FormatName(h.HighFormat)} не поддерживается.");

            // В файле уровни идут от самого маленького к самому большому — пропускаем все меньшие
            long skip = 0;
            for (int m = h.MipCount - 1; m >= 1; m--)
            {
                long levelSize = ImageSize(h.HighFormat, Math.Max(1, h.Width >> m), Math.Max(1, h.Height >> m));
                skip += levelSize * h.Frames * h.Faces * h.Depth;
            }

            long start = h.HighDataOffset + skip;
            long size = ImageSize(h.HighFormat, h.Width, h.Height);
            if (start < 0 || start + size > vtf.Length)
                throw new InvalidDataException($"Файл VTF оборван: нужны данные до байта {start + size:N0}, а в файле {vtf.Length:N0} байт.");
            int pos = (int)start;

            // DXT: данные копируем как есть
            if (IsCompressed(h.HighFormat))
            {
                uint fourCc = h.HighFormat switch { 14 => 0x33545844u, 15 => 0x35545844u, _ => 0x31545844u };
                var dds = new byte[128 + (int)size];
                WriteHeader(dds, h.Width, h.Height, fourCc, (int)size);
                Buffer.BlockCopy(vtf, pos, dds, 128, (int)size);
                return dds;
            }

            // Несжатые форматы приводим к BGRA32 (A8R8G8B8)
            int bpp = BytesPerPixel(h.HighFormat);
            int count = h.Width * h.Height;
            var bgra = new byte[128 + count * 4];
            WriteHeader(bgra, h.Width, h.Height, 0, h.Width * 4);

            int o = 128;
            for (int i = 0; i < count; i++, pos += bpp, o += 4)
            {
                byte r, g, b, a = 255;
                switch (h.HighFormat)
                {
                    case 0:  r = vtf[pos]; g = vtf[pos + 1]; b = vtf[pos + 2]; a = vtf[pos + 3]; break;      // RGBA8888
                    case 1:  a = vtf[pos]; b = vtf[pos + 1]; g = vtf[pos + 2]; r = vtf[pos + 3]; break;      // ABGR8888
                    case 11: a = vtf[pos]; r = vtf[pos + 1]; g = vtf[pos + 2]; b = vtf[pos + 3]; break;      // ARGB8888
                    case 12: b = vtf[pos]; g = vtf[pos + 1]; r = vtf[pos + 2]; a = vtf[pos + 3]; break;      // BGRA8888
                    case 16: b = vtf[pos]; g = vtf[pos + 1]; r = vtf[pos + 2]; break;                        // BGRX8888
                    case 2:  r = vtf[pos]; g = vtf[pos + 1]; b = vtf[pos + 2]; break;                        // RGB888
                    case 3:  b = vtf[pos]; g = vtf[pos + 1]; r = vtf[pos + 2]; break;                        // BGR888
                    case 4:                                                                                  // RGB565
                    {
                        int v = vtf[pos] | (vtf[pos + 1] << 8);
                        r = (byte)(((v >> 11) & 31) * 255 / 31);
                        g = (byte)(((v >> 5) & 63) * 255 / 63);
                        b = (byte)((v & 31) * 255 / 31);
                        break;
                    }
                    case 17:                                                                                 // BGR565
                    {
                        int v = vtf[pos] | (vtf[pos + 1] << 8);
                        b = (byte)(((v >> 11) & 31) * 255 / 31);
                        g = (byte)(((v >> 5) & 63) * 255 / 63);
                        r = (byte)((v & 31) * 255 / 31);
                        break;
                    }
                    case 5:  r = g = b = vtf[pos]; break;                                                    // I8
                    case 6:  r = g = b = vtf[pos]; a = vtf[pos + 1]; break;                                  // IA88
                    case 8:  r = g = b = 255; a = vtf[pos]; break;                                           // A8
                    default: throw new InvalidDataException($"Формат пикселей VTF {FormatName(h.HighFormat)} не поддерживается.");
                }
                bgra[o] = b;
                bgra[o + 1] = g;
                bgra[o + 2] = r;
                bgra[o + 3] = a;
            }
            return bgra;
        }

        private static void WriteHeader(byte[] d, int w, int h, uint fourCc, int sizeOrPitch)
        {
            Span<byte> s = d;
            bool compressed = fourCc != 0;

            W32(s, 0, 0x20534444); // "DDS "
            W32(s, 4, 124);
            W32(s, 8, 0x1007u | (compressed ? 0x80000u : 0x8u));
            W32(s, 12, (uint)h);
            W32(s, 16, (uint)w);
            W32(s, 20, (uint)sizeOrPitch);
            W32(s, 76, 32);
            if (compressed)
            {
                W32(s, 80, 0x4);
                W32(s, 84, fourCc);
            }
            else
            {
                W32(s, 80, 0x41);   // RGB + ALPHAPIXELS
                W32(s, 88, 32);
                W32(s, 92, 0x00FF0000);
                W32(s, 96, 0x0000FF00);
                W32(s, 100, 0x000000FF);
                W32(s, 104, 0xFF000000);
            }
            W32(s, 108, 0x1000);
        }

        // ------------------------------------------------------------------ описание (предпросмотр и Debug)

        /// <summary>Контейнер, сжатие и число мипов — для бейджей в предпросмотре.</summary>
        public static (string Container, string Compression, int Mips) Describe(byte[] d, bool ru)
        {
            try
            {
                VtfHeader h = ParseHeader(d);
                string unc = ru ? "без сжатия" : "uncompressed";
                string comp = IsCompressed(h.HighFormat) ? FormatName(h.HighFormat) : $"{FormatName(h.HighFormat)} ({unc})";
                return ($"VTF v{h.Major}.{h.Minor} (Source)", comp, h.MipCount);
            }
            catch (InvalidDataException ex)
            {
                return ("VTF", ex.Message, 1);
            }
        }

        /// <summary>Строка с подробностями о заголовке для режима Debug.</summary>
        public static string DescribeHeadLines(byte[] head, bool ru)
        {
            if (head.Length < 63)
                return ru ? "    кодек: VTF, заголовок обрезан\n" : "    codec: VTF, header is truncated\n";

            uint major = U32(head, 4), minor = U32(head, 8);
            int w = U16(head, 16), h = U16(head, 18);
            uint flags = U32(head, 20);
            int format = (int)U32(head, 52);
            int mips = head[56];
            return ru
                ? $"    кодек: VTF v{major}.{minor}, формат {FormatName(format)} (код {format}), {w}×{h}, мипов: {mips}, флаги: 0x{flags:X8}\n"
                : $"    codec: VTF v{major}.{minor}, format {FormatName(format)} (code {format}), {w}×{h}, mips: {mips}, flags: 0x{flags:X8}\n";
        }
    }
}
