using System;
using System.IO;

namespace IwiThumbs
{
    /// <summary>
    /// DDS (верхний уровень) → пиксели BGRA, уменьшение и premultiplied-альфа для миниатюры.
    /// Понимает ровно то, что выдают кодеки IWI/VTF: DXT1, DXT3, DXT5, 24-битный BGR и 32-битный BGRA.
    /// </summary>
    internal static class TextureBitmap
    {
        private const int MaxSide = 16384;

        private static uint U32(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));

        public static void DecodeTopLevel(byte[] dds, out int width, out int height, out byte[] bgra)
        {
            if (dds.Length < 128 || U32(dds, 0) != 0x20534444u) throw new InvalidDataException("Это не DDS.");

            height = (int)U32(dds, 12);
            width = (int)U32(dds, 16);
            if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || (long)width * height > 100_000_000L)
                throw new InvalidDataException("Недопустимый размер текстуры.");

            uint pfFlags = U32(dds, 80);
            uint bits = U32(dds, 88);
            int pos = 128;
            bgra = new byte[checked(width * height * 4)];

            if ((pfFlags & 0x4) != 0)
            {
                uint fourCc = U32(dds, 84);
                int kind = fourCc == 0x31545844u ? 1
                         : fourCc == 0x33545844u ? 3
                         : fourCc == 0x35545844u ? 5
                         : fourCc == 0x32495441u ? 14   // ATI2 / BC5 (Black Ops 2 normal maps)
                         : 0;
                if (kind == 0) throw new InvalidDataException("Неподдерживаемое сжатие DDS.");
                if (kind == 14) DecodeBc5(dds, pos, width, height, bgra);
                else DecodeDxt(dds, pos, width, height, kind, bgra);
            }
            else if (bits == 8 && (pfFlags & 0x20000u) != 0)
            {
                // L8/R8 — например, однокомпонентные текстуры Black Ops 2.
                if (dds.Length < pos + width * height) throw new InvalidDataException("Файл оборван.");
                for (int i = 0; i < width * height; i++)
                {
                    byte v = dds[pos + i];
                    bgra[i * 4] = v;
                    bgra[i * 4 + 1] = v;
                    bgra[i * 4 + 2] = v;
                    bgra[i * 4 + 3] = 255;
                }
            }
            else if (bits == 24)
            {
                if (dds.Length < pos + width * height * 3) throw new InvalidDataException("Файл оборван.");
                for (int i = 0; i < width * height; i++)
                {
                    bgra[i * 4] = dds[pos + i * 3];
                    bgra[i * 4 + 1] = dds[pos + i * 3 + 1];
                    bgra[i * 4 + 2] = dds[pos + i * 3 + 2];
                    bgra[i * 4 + 3] = 255;
                }
            }
            else
            {
                if (dds.Length < pos + width * height * 4) throw new InvalidDataException("Файл оборван.");
                Buffer.BlockCopy(dds, pos, bgra, 0, width * height * 4);
            }
        }

        private static void Expand565(int v, out int r, out int g, out int b)
        {
            int r5 = (v >> 11) & 31, g6 = (v >> 5) & 63, b5 = v & 31;
            r = (r5 << 3) | (r5 >> 2);
            g = (g6 << 2) | (g6 >> 4);
            b = (b5 << 3) | (b5 >> 2);
        }

        /// <summary>Таблица из 4 цветов блока: r, g, b, a подряд (16 чисел).</summary>
        private static void ColorTable(int c0, int c1, bool fourColors, int[] t)
        {
            Expand565(c0, out int r0, out int g0, out int b0);
            Expand565(c1, out int r1, out int g1, out int b1);
            t[0] = r0; t[1] = g0; t[2] = b0; t[3] = 255;
            t[4] = r1; t[5] = g1; t[6] = b1; t[7] = 255;
            if (fourColors)
            {
                t[8] = (2 * r0 + r1) / 3; t[9] = (2 * g0 + g1) / 3; t[10] = (2 * b0 + b1) / 3; t[11] = 255;
                t[12] = (r0 + 2 * r1) / 3; t[13] = (g0 + 2 * g1) / 3; t[14] = (b0 + 2 * b1) / 3; t[15] = 255;
            }
            else
            {
                t[8] = (r0 + r1) / 2; t[9] = (g0 + g1) / 2; t[10] = (b0 + b1) / 2; t[11] = 255;
                t[12] = 0; t[13] = 0; t[14] = 0; t[15] = 0; // прозрачный чёрный (DXT1 с 1-битной альфой)
            }
        }

        /// <summary>Таблица из 8 значений альфы для DXT5.</summary>
        private static void Alpha5(int a0, int a1, int[] t)
        {
            t[0] = a0;
            t[1] = a1;
            if (a0 > a1)
            {
                for (int i = 1; i < 7; i++) t[1 + i] = ((7 - i) * a0 + i * a1) / 7;
            }
            else
            {
                for (int i = 1; i < 5; i++) t[1 + i] = ((5 - i) * a0 + i * a1) / 5;
                t[6] = 0;
                t[7] = 255;
            }
        }

        private static void DecodeDxt(byte[] d, int pos, int w, int h, int kind, byte[] bgra)
        {
            int blocksX = (w + 3) / 4, blocksY = (h + 3) / 4;
            long need = (long)blocksX * blocksY * (kind == 1 ? 8 : 16);
            if (d.Length < pos + need) throw new InvalidDataException("Файл оборван.");

            var colors = new int[16];
            var alphas = new int[8];

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int cb, ab = -1;
                    if (kind == 1)
                    {
                        cb = pos;
                        pos += 8;
                    }
                    else
                    {
                        ab = pos;
                        cb = pos + 8;
                        pos += 16;
                    }

                    int c0 = d[cb] | (d[cb + 1] << 8);
                    int c1 = d[cb + 2] | (d[cb + 3] << 8);
                    uint idx = U32(d, cb + 4);
                    ColorTable(c0, c1, kind != 1 || c0 > c1, colors);

                    ulong alphaBits = 0;
                    if (kind == 5)
                    {
                        Alpha5(d[ab], d[ab + 1], alphas);
                        for (int k = 0; k < 6; k++) alphaBits |= (ulong)d[ab + 2 + k] << (8 * k);
                    }

                    for (int i = 0; i < 16; i++)
                    {
                        int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
                        if (x >= w || y >= h) continue;

                        int ci = (int)((idx >> (2 * i)) & 3) * 4;
                        int a = colors[ci + 3];
                        if (kind == 3) a = ((d[ab + (i >> 1)] >> (4 * (i & 1))) & 15) * 17;
                        else if (kind == 5) a = alphas[(int)((alphaBits >> (3 * i)) & 7)];

                        int o = (y * w + x) * 4;
                        bgra[o] = (byte)colors[ci + 2];
                        bgra[o + 1] = (byte)colors[ci + 1];
                        bgra[o + 2] = (byte)colors[ci];
                        bgra[o + 3] = (byte)a;
                    }
                }
            }
        }

        /// <summary>BC5 (ATI2): две сжатые 8-битные компоненты, показываем как normal map.</summary>
        private static void DecodeBc5(byte[] d, int pos, int w, int h, byte[] bgra)
        {
            int blocksX = (w + 3) / 4, blocksY = (h + 3) / 4;
            long need = (long)blocksX * blocksY * 16;
            if (d.Length < pos + need) throw new InvalidDataException("Файл оборван.");

            var tx = new int[8];
            var ty = new int[8];

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int xb = pos, yb = pos + 8;
                    pos += 16;

                    Alpha5(d[xb], d[xb + 1], tx);
                    Alpha5(d[yb], d[yb + 1], ty);

                    ulong ix = 0, iy = 0;
                    for (int k = 0; k < 6; k++)
                    {
                        ix |= (ulong)d[xb + 2 + k] << (8 * k);
                        iy |= (ulong)d[yb + 2 + k] << (8 * k);
                    }

                    for (int py = 0; py < 4; py++)
                    {
                        int y = by * 4 + py;
                        if (y >= h) break;
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px;
                            if (x >= w) break;

                            int bit = 3 * (py * 4 + px);
                            byte nx = (byte)tx[(int)((ix >> bit) & 7)];
                            byte ny = (byte)ty[(int)((iy >> bit) & 7)];

                            float fx = (nx / 255f) * 2f - 1f;
                            float fy = (ny / 255f) * 2f - 1f;
                            float fz = MathF.Sqrt(MathF.Max(0f, 1f - fx * fx - fy * fy));

                            int o = (y * w + x) * 4;
                            bgra[o] = (byte)(fz * 255f);
                            bgra[o + 1] = ny;
                            bgra[o + 2] = nx;
                            bgra[o + 3] = 255;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Вписывает картинку в квадрат maxSize (без увеличения) и сразу переводит в premultiplied BGRA:
        /// проводник рисует миниатюры именно так, и прозрачность текстуры (альфа-канал) показывается как есть.
        /// </summary>
        public static byte[] FitPremultiplied(byte[] src, int sw, int sh, int maxSize, out int dw, out int dh)
        {
            dw = sw;
            dh = sh;
            if (maxSize > 0 && Math.Max(sw, sh) > maxSize)
            {
                double f = (double)maxSize / Math.Max(sw, sh);
                dw = Math.Max(1, (int)Math.Round(sw * f));
                dh = Math.Max(1, (int)Math.Round(sh * f));
            }

            var dst = new byte[dw * dh * 4];
            for (int y = 0; y < dh; y++)
            {
                int y0 = (int)((long)y * sh / dh);
                int y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * sh / dh));
                for (int x = 0; x < dw; x++)
                {
                    int x0 = (int)((long)x * sw / dw);
                    int x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * sw / dw));

                    long sb = 0, sg = 0, sr = 0, sa = 0;
                    int count = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        for (int xx = x0; xx < x1; xx++)
                        {
                            int o = (yy * sw + xx) * 4;
                            int a = src[o + 3];
                            sb += src[o] * a;
                            sg += src[o + 1] * a;
                            sr += src[o + 2] * a;
                            sa += a;
                            count++;
                        }
                    }

                    int d = (y * dw + x) * 4;
                    long div = 255L * count;
                    dst[d] = (byte)((sb + 127L * count) / div);
                    dst[d + 1] = (byte)((sg + 127L * count) / div);
                    dst[d + 2] = (byte)((sr + 127L * count) / div);
                    dst[d + 3] = (byte)((sa + count / 2) / count);
                }
            }
            return dst;
        }
    }
}
