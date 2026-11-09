using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace IwIUniversalTool
{
    /// <summary>Какой формат пикселей писать в DDS.</summary>
    public enum DdsKind { Dxt1, Dxt5, Uncompressed, Bc5, Bc7 }

    /// <summary>Тип текстуры по имени файла (нужен для автопрофиля под игру).</summary>
    public enum TexKind { Color, Normal, Ui }

    /// <summary>Один уровень изображения в виде сырых байт RGBA (по 4 байта на пиксель, строки сверху вниз).</summary>
    public sealed record RgbaLevel(int Width, int Height, byte[] Rgba);

    /// <summary>
    /// Собственные кодировщики BC5 (карты нормалей) и BC7 (высокое качество, режим 6) и сборка DDS-файла.
    /// DXT1, DXT5 и «без сжатия» по-прежнему делает ImageMagick.
    /// </summary>
    public static class DdsEncoders
    {
        private const uint DdsMagic = 0x20534444;   // "DDS "
        private const uint FourCcAti2 = 0x32495441; // "ATI2" (BC5 для DirectX 9)
        private const uint FourCcDx10 = 0x30315844; // "DX10"

        private static readonly int[] Weights4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

        private static readonly string[] UiKeys =
        {
            "ui_", "hud", "font", "reticle", "crosshair", "menu", "icon", "compass", "cursor", "overlay"
        };

        // ------------------------------------------------------------------ классификация и профиль

        /// <summary>Определяет по имени файла: карта нормалей, интерфейс или обычный цвет.</summary>
        public static TexKind Classify(string fileName)
        {
            string n = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();

            if (n.Contains("_nml") || n.Contains("_nrm") || n.Contains("_norm") || n.Contains("normal") ||
                n.Contains("bump") || n.EndsWith("_n") || n.EndsWith("_nm"))
                return TexKind.Normal;

            foreach (string key in UiKeys)
            {
                if (n.Contains(key)) return TexKind.Ui;
            }
            return TexKind.Color;
        }

        /// <summary>
        /// Автопрофиль под игру.
        /// Black Ops 2 (DirectX 11): BC7 для цвета, BC5 для нормалей.
        /// Остальные (DirectX 9 и старше): DXT1 без альфы, DXT5 с альфой, ATI2/BC5 для нормалей, ARGB32 для интерфейса.
        /// </summary>
        public static (DdsKind Kind, bool Dx10) ChooseProfile(int codVerIndex, TexKind kind, bool hasAlpha)
        {
            bool dx11 = codVerIndex == 0; // первый пункт списка — Black Ops 2 (v27)
            if (dx11)
                return kind == TexKind.Normal ? (DdsKind.Bc5, true) : (DdsKind.Bc7, true);

            switch (kind)
            {
                case TexKind.Normal: return (DdsKind.Bc5, false);        // ATI2
                case TexKind.Ui: return (DdsKind.Uncompressed, false);   // ARGB32
                default: return (hasAlpha ? DdsKind.Dxt5 : DdsKind.Dxt1, false);
            }
        }

        // ------------------------------------------------------------------ сборка DDS

        private static void W32(Span<byte> s, int offset, uint value)
            => BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(offset, 4), value);

        /// <summary>Кодирует все уровни в BC5 или BC7 и собирает готовый DDS-файл.</summary>
        public static byte[] BuildBcDds(DdsKind kind, bool dx10, IReadOnlyList<RgbaLevel> levels)
        {
            if (kind != DdsKind.Bc5 && kind != DdsKind.Bc7)
                throw new ArgumentException("Для этого метода нужен BC5 или BC7.", nameof(kind));
            if (levels.Count == 0)
                throw new InvalidDataException("Нет данных изображения для кодирования.");

            var encoded = new List<byte[]>(levels.Count);
            foreach (RgbaLevel level in levels)
                encoded.Add(kind == DdsKind.Bc5 ? EncodeBc5(level) : EncodeBc7(level));

            int headerSize = 128 + (dx10 ? 20 : 0);
            long total = headerSize;
            foreach (byte[] block in encoded) total += block.Length;
            if (total > int.MaxValue) throw new InvalidDataException("Текстура слишком большая.");

            var dds = new byte[total];
            Span<byte> s = dds;

            W32(s, 0, DdsMagic);
            W32(s, 4, 124);
            // CAPS | HEIGHT | WIDTH | PIXELFORMAT | LINEARSIZE | (MIPMAPCOUNT)
            W32(s, 8, 0x1007u | 0x80000u | (levels.Count > 1 ? 0x20000u : 0u));
            W32(s, 12, (uint)levels[0].Height);
            W32(s, 16, (uint)levels[0].Width);
            W32(s, 20, (uint)encoded[0].Length);
            W32(s, 28, levels.Count > 1 ? (uint)levels.Count : 0u);
            W32(s, 76, 32);
            W32(s, 80, 4); // DDPF_FOURCC
            W32(s, 84, dx10 ? FourCcDx10 : FourCcAti2);
            W32(s, 108, 0x1000u | (levels.Count > 1 ? 0x400008u : 0u));

            int pos = 128;
            if (dx10)
            {
                W32(s, 128, kind == DdsKind.Bc7 ? 98u : 83u); // DXGI_FORMAT_BC7_UNORM / BC5_UNORM
                W32(s, 132, 3);                                // TEXTURE2D
                W32(s, 136, 0);
                W32(s, 140, 1);                                // размер массива
                W32(s, 144, 0);
                pos = 148;
            }

            foreach (byte[] block in encoded)
            {
                Buffer.BlockCopy(block, 0, dds, pos, block.Length);
                pos += block.Length;
            }
            return dds;
        }

        // ------------------------------------------------------------------ BC4 / BC5

        private static byte[] EncodeBc5(RgbaLevel level)
        {
            int bw = (level.Width + 3) / 4, bh = (level.Height + 3) / 4;
            var output = new byte[bw * bh * 16];
            int offset = 0;
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    EncodeBc5Block(level, bx, by, new Span<byte>(output, offset, 16));
                    offset += 16;
                }
            }
            return output;
        }

        private static void EncodeBc5Block(RgbaLevel level, int bx, int by, Span<byte> dst)
        {
            Span<byte> red = stackalloc byte[16];
            Span<byte> green = stackalloc byte[16];
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    int sx = Math.Min(level.Width - 1, bx * 4 + x);
                    int sy = Math.Min(level.Height - 1, by * 4 + y);
                    int src = (sy * level.Width + sx) * 4;
                    red[y * 4 + x] = level.Rgba[src];
                    green[y * 4 + x] = level.Rgba[src + 1];
                }
            }
            EncodeBc4(red, dst.Slice(0, 8));     // X (красный канал)
            EncodeBc4(green, dst.Slice(8, 8));   // Y (зелёный канал)
        }

        private static void EncodeBc4(ReadOnlySpan<byte> values, Span<byte> dst)
        {
            int min = 255, max = 0;
            for (int i = 0; i < 16; i++)
            {
                if (values[i] < min) min = values[i];
                if (values[i] > max) max = values[i];
            }

            int e0 = max, e1 = min;
            Span<int> palette = stackalloc int[8];
            palette[0] = e0;
            palette[1] = e1;
            if (e0 > e1)
            {
                for (int i = 2; i < 8; i++) palette[i] = ((8 - i) * e0 + (i - 1) * e1) / 7;
            }
            else
            {
                for (int i = 2; i < 6; i++) palette[i] = ((6 - i) * e0 + (i - 1) * e1) / 5;
                palette[6] = 0;
                palette[7] = 255;
            }

            ulong bits = 0;
            for (int p = 0; p < 16; p++)
            {
                int best = 0, bestError = int.MaxValue;
                for (int k = 0; k < 8; k++)
                {
                    int d = palette[k] - values[p];
                    d *= d;
                    if (d < bestError) { bestError = d; best = k; }
                }
                bits |= (ulong)best << (3 * p);
            }

            dst[0] = (byte)e0;
            dst[1] = (byte)e1;
            for (int i = 0; i < 6; i++) dst[2 + i] = (byte)(bits >> (8 * i));
        }

        // ------------------------------------------------------------------ BC7 (режим 6)

        private static byte[] EncodeBc7(RgbaLevel level)
        {
            int bw = (level.Width + 3) / 4, bh = (level.Height + 3) / 4;
            var output = new byte[bw * bh * 16];
            var px = new int[64];
            int offset = 0;

            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            int sx = Math.Min(level.Width - 1, bx * 4 + x);
                            int sy = Math.Min(level.Height - 1, by * 4 + y);
                            int src = (sy * level.Width + sx) * 4;
                            int dst = (y * 4 + x) * 4;
                            px[dst] = level.Rgba[src];
                            px[dst + 1] = level.Rgba[src + 1];
                            px[dst + 2] = level.Rgba[src + 2];
                            px[dst + 3] = level.Rgba[src + 3];
                        }
                    }
                    EncodeBc7Block(px, new Span<byte>(output, offset, 16));
                    offset += 16;
                }
            }
            return output;
        }

        /// <summary>
        /// Режим 6: один набор цветов, концы отрезка 7+1 бит на канал (с общим p-битом), 4-битные индексы.
        /// Концы выбираются по главной оси цветов блока.
        /// </summary>
        private static void EncodeBc7Block(int[] px, Span<byte> dst)
        {
            Span<double> mean = stackalloc double[4];
            for (int c = 0; c < 4; c++)
            {
                double sum = 0;
                for (int i = 0; i < 16; i++) sum += px[i * 4 + c];
                mean[c] = sum / 16.0;
            }

            Span<double> cov = stackalloc double[16];
            for (int a = 0; a < 4; a++)
            {
                for (int b = 0; b < 4; b++)
                {
                    double sum = 0;
                    for (int i = 0; i < 16; i++) sum += (px[i * 4 + a] - mean[a]) * (px[i * 4 + b] - mean[b]);
                    cov[a * 4 + b] = sum;
                }
            }

            Span<double> axis = stackalloc double[4];
            Span<double> next = stackalloc double[4];
            for (int c = 0; c < 4; c++) axis[c] = 1.0;
            for (int iter = 0; iter < 8; iter++)
            {
                double len = 0;
                for (int a = 0; a < 4; a++)
                {
                    double sum = 0;
                    for (int b = 0; b < 4; b++) sum += cov[a * 4 + b] * axis[b];
                    next[a] = sum;
                    len += sum * sum;
                }
                len = Math.Sqrt(len);
                if (len < 1e-9) break;
                for (int a = 0; a < 4; a++) axis[a] = next[a] / len;
            }

            double tMin = double.MaxValue, tMax = double.MinValue;
            for (int i = 0; i < 16; i++)
            {
                double t = 0;
                for (int c = 0; c < 4; c++) t += (px[i * 4 + c] - mean[c]) * axis[c];
                if (t < tMin) tMin = t;
                if (t > tMax) tMax = t;
            }

            Span<double> lo = stackalloc double[4];
            Span<double> hi = stackalloc double[4];
            for (int c = 0; c < 4; c++)
            {
                lo[c] = Math.Clamp(mean[c] + axis[c] * tMin, 0.0, 255.0);
                hi[c] = Math.Clamp(mean[c] + axis[c] * tMax, 0.0, 255.0);
            }

            Span<int> q0 = stackalloc int[4];
            Span<int> q1 = stackalloc int[4];
            int p0 = Quantize(lo, q0);
            int p1 = Quantize(hi, q1);

            Span<int> palette = stackalloc int[64];
            for (int k = 0; k < 16; k++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int a0 = q0[c] * 2 + p0;
                    int a1 = q1[c] * 2 + p1;
                    palette[k * 4 + c] = ((64 - Weights4[k]) * a0 + Weights4[k] * a1 + 32) >> 6;
                }
            }

            Span<int> index = stackalloc int[16];
            for (int i = 0; i < 16; i++)
            {
                int best = 0, bestError = int.MaxValue;
                for (int k = 0; k < 16; k++)
                {
                    int error = 0;
                    for (int c = 0; c < 4; c++)
                    {
                        int d = palette[k * 4 + c] - px[i * 4 + c];
                        error += d * d;
                    }
                    if (error < bestError) { bestError = error; best = k; }
                }
                index[i] = best;
            }

            // У первого пикселя старший бит индекса не хранится — он обязан быть нулевым
            if (index[0] >= 8)
            {
                for (int c = 0; c < 4; c++)
                {
                    int tmp = q0[c];
                    q0[c] = q1[c];
                    q1[c] = tmp;
                }
                int tp = p0;
                p0 = p1;
                p1 = tp;
                for (int i = 0; i < 16; i++) index[i] = 15 - index[i];
            }

            dst.Clear();
            int bit = 0;
            PutBits(dst, ref bit, 64, 7); // признак режима 6: шесть нулей и единица
            for (int c = 0; c < 4; c++)
            {
                PutBits(dst, ref bit, (uint)q0[c], 7);
                PutBits(dst, ref bit, (uint)q1[c], 7);
            }
            PutBits(dst, ref bit, (uint)p0, 1);
            PutBits(dst, ref bit, (uint)p1, 1);
            PutBits(dst, ref bit, (uint)index[0], 3);
            for (int i = 1; i < 16; i++) PutBits(dst, ref bit, (uint)index[i], 4);
        }

        /// <summary>Приводит конец отрезка к 7 битам + общий p-бит. Возвращает p-бит.</summary>
        private static int Quantize(ReadOnlySpan<double> color, Span<int> q)
        {
            double bestError = double.MaxValue;
            int bestP = 0;
            Span<int> tmp = stackalloc int[4];

            for (int p = 0; p < 2; p++)
            {
                double error = 0;
                for (int i = 0; i < 4; i++)
                {
                    int value = (int)Math.Round((color[i] - p) / 2.0);
                    if (value < 0) value = 0;
                    if (value > 127) value = 127;
                    double diff = value * 2 + p - color[i];
                    error += diff * diff;
                    tmp[i] = value;
                }
                if (error < bestError)
                {
                    bestError = error;
                    bestP = p;
                    for (int i = 0; i < 4; i++) q[i] = tmp[i];
                }
            }
            return bestP;
        }

        private static void PutBits(Span<byte> dst, ref int bitPos, uint value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (((value >> i) & 1u) != 0)
                    dst[bitPos >> 3] |= (byte)(1 << (bitPos & 7));
                bitPos++;
            }
        }
    }
}
