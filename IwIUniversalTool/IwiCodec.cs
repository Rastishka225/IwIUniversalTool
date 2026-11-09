using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Runtime.CompilerServices;

namespace IwIUniversalTool
{
    public enum IwiFormat : byte
    {
        Argb32 = 0x01,
        Rgb24 = 0x02,
        GrayAlpha16 = 0x03,
        Alpha8 = 0x04,
        R8 = 0x05,
        WaveletRgba = 0x06,
        WaveletRgb = 0x07,
        WaveletGa = 0x08,
        WaveletGray = 0x09,
        WaveletAlpha = 0x0A,
        Dxt1 = 0x0B,
        Dxt3 = 0x0C,
        Dxt5 = 0x0D,
        Bc5 = 0x0E
    }

    public enum IwiBuildResult { Ok, NotDxt, NeedMips }

    public readonly record struct IwiHeader(byte Version, int HeaderSize, IwiFormat Format, int Width, int Height);

    public static class IwiCodec
    {
        public static bool MipsSmallestFirst { get; set; } = true;

        public static readonly byte[] VersionByIndex = { 27, 8, 13, 8, 6, 6, 5 };
        public static readonly int[] HeaderSizeByIndex = { 64, 32, 48, 32, 28, 28, 28 };

        private const uint DdsMagic = 0x20534444;
        private const uint FourCcDxt1 = 0x31545844;
        private const uint FourCcDxt3 = 0x33545844;
        private const uint FourCcDxt5 = 0x35545844;
        private const uint FourCcDx10 = 0x30315844;
        private const uint FourCcAti2 = 0x32495441; // "ATI2" — BC5 (две 8-битные компоненты)

        private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));
        private static void W32(Span<byte> s, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o, 4), v);

        // ----- Таблицы wavelet: разворачиваются из компактного кодового набора (WaveletCodebook) -----
        private const int WaveletTablesSize = 49152;
        private static byte[]? _waveletTables;

        /// <summary>Откуда взяты таблицы wavelet (для режима Debug).</summary>
        public static string? WaveletTablesSource { get; private set; }
        private static readonly object WaveletTablesLock = new();

        private static byte[] GetWaveletTables()
        {
            byte[]? cached = _waveletTables;
            if (cached != null) return cached;

            lock (WaveletTablesLock)
            {
                if (_waveletTables != null) return _waveletTables;

                byte[] data = WaveletCodebook.Generate();
                if (data.Length != WaveletTablesSize)
                    throw new InvalidDataException("Не удалось собрать таблицы wavelet.");

                WaveletTablesSource = "сгенерированы из WaveletCodebook";
                _waveletTables = data;
                return data;
            }
        }

        /// <summary>wavelet-форматы CoD4 лежат как один общий поток: уровни мипов внутри него.</summary>
        public static bool IsWavelet(IwiFormat f) => (byte)f >= 0x05 && (byte)f <= 0x0A;

        /// <summary>
        /// В IWI v27 (Black Ops 2) код 0x05 — это R8, а не wavelet CoD4,
        /// поэтому «вейвлетность» зависит ещё и от версии файла.
        /// </summary>
        public static bool IsWaveletFor(IwiHeader h) => h.Version != 27 && IsWavelet(h.Format);

        public static string? WaveletName(byte fmt) => fmt switch
        {
            0x05 or 0x06 => "Wavelet RGBA",
            0x07 => "Wavelet RGB",
            0x08 => "Wavelet L+A",
            0x09 => "Wavelet L",
            0x0A => "Wavelet Alpha",
            _ => null
        };

        public static IwiHeader ParseIwiHeader(byte[] iwi)
        {
            if (iwi.Length < 28)
                throw new InvalidDataException($"Файл слишком короткий для IWI ({iwi.Length} байт).");
            if (iwi[0] != (byte)'I' || iwi[1] != (byte)'W' || iwi[2] != (byte)'i')
                throw new InvalidDataException($"Это не IWI: файл начинается с {HexHead(iwi, 4)}, а должен — с 49 57 69 («IWi»).");

            byte version = iwi[3];
            int headerSize = version switch
            {
                27 => 64,
                13 => 48,
                8 => 32,
                5 or 6 => 28,
                _ => throw new InvalidDataException($"Версия IWI {version} не поддерживается.")
            };
            if (iwi.Length < headerSize)
                throw new InvalidDataException("Файл IWI оборван: заголовок неполный.");

            // У IWI v8 (MW3/Plutonium) другой заголовок: после версии идут 4 байта флагов,
            // и только затем формат, ширина и высота.
            //   v5/v6: [4]=format, [6]=width, [8]=height
            //   v8:    [4..8)=flags, [8]=format, [10]=width, [12]=height
            byte fmt;
            int w, h;
            if (version == 8)
            {
                fmt = iwi[8];
                w = BinaryPrimitives.ReadUInt16LittleEndian(iwi.AsSpan(10, 2));
                h = BinaryPrimitives.ReadUInt16LittleEndian(iwi.AsSpan(12, 2));
            }
            else
            {
                fmt = iwi[4];
                w = BinaryPrimitives.ReadUInt16LittleEndian(iwi.AsSpan(6, 2));
                h = BinaryPrimitives.ReadUInt16LittleEndian(iwi.AsSpan(8, 2));
            }

            if (!(fmt is 0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06 or 0x07 or 0x08 or 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x0E))
                throw new InvalidDataException($"Формат IWI 0x{fmt:X2} не поддерживается.");
            if (w == 0 || h == 0)
                throw new InvalidDataException("В заголовке IWI нулевой размер картинки.");

            return new IwiHeader(version, headerSize, (IwiFormat)fmt, w, h);
        }

        private static long LevelSizeLong(IwiFormat f, int w, int h)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            return f switch
            {
                IwiFormat.Dxt1 => (long)((w + 3) / 4) * ((h + 3) / 4) * 8,
                IwiFormat.Dxt3 or IwiFormat.Dxt5 or IwiFormat.Bc5 => (long)((w + 3) / 4) * ((h + 3) / 4) * 16,
                IwiFormat.Argb32 or IwiFormat.WaveletRgba => (long)w * h * 4,
                IwiFormat.Rgb24 or IwiFormat.WaveletRgb => (long)w * h * 3,
                IwiFormat.GrayAlpha16 or IwiFormat.WaveletGa => (long)w * h * 2,
                IwiFormat.Alpha8 or IwiFormat.R8 or IwiFormat.WaveletGray or IwiFormat.WaveletAlpha => (long)w * h,
                _ => (long)w * h * 4
            };
        }

        public static int LevelSize(IwiFormat f, int w, int h)
        {
            long size = LevelSizeLong(f, w, h);
            if (size > int.MaxValue) throw new InvalidDataException("Текстура слишком большая.");
            return (int)size;
        }

        private static int DetectMipCount(IwiFormat f, int w, int h, long payloadLen)
        {
            long total = 0;
            int cw = w, ch = h;
            for (int n = 1; n <= 16; n++)
            {
                total += LevelSizeLong(f, cw, ch);
                if (total == payloadLen) return n;
                if (total > payloadLen) return 0;
                if (cw == 1 && ch == 1) break;
                cw = Math.Max(1, cw >> 1);
                ch = Math.Max(1, ch >> 1);
            }
            return 0;
        }

        public static int GetMipCount(byte[] iwi)
        {
            IwiHeader h = ParseIwiHeader(iwi);
            if (IsWaveletFor(h))
                return (iwi[5] & 2) != 0 ? 1 : FullMipCount(h.Width, h.Height); // для wavelet — как считает декодер
            return Math.Max(1, DetectMipCount(h.Format, h.Width, h.Height, iwi.Length - h.HeaderSize));
        }

        // ==================== ДЕКОДЕР ВЕЙВЛЕТА COD4 ====================

        public static byte[] DecodeWavelet(byte[] iwi, IwiHeader hdr)
        {
            byte[] tables = GetWaveletTables();
            ReadOnlySpan<byte> t1 = tables.AsSpan(0, 16384);
            ReadOnlySpan<byte> t2 = tables.AsSpan(16384, 16384);
            ReadOnlySpan<byte> t3 = tables.AsSpan(32768, 16384);

            int width = hdr.Width;
            int height = hdr.Height;
            byte flags = iwi[5];

            int numLevels = 1;
            if ((flags & 2) == 0)
            {
                int sw = width, sh = height;
                while (sw > 1 || sh > 1)
                {
                    sw = Math.Max(1, sw >> 1);
                    sh = Math.Max(1, sh >> 1);
                    numLevels++;
                }
            }

            int fmtCode = (int)hdr.Format;
            int components = fmtCode switch
            {
                1 or 5 or 6 => 4,
                2 or 7 => 3,
                3 or 8 => 2,
                4 or 9 or 10 => 1,
                _ => 4
            };

            var reader = new WaveletBitReader(iwi, hdr.HeaderSize);

            byte[] prevBuf = Array.Empty<byte>();
            byte[] currBuf = Array.Empty<byte>();

            for (int level = numLevels - 1; level >= 0; level--)
            {
                int currW = Math.Max(1, width >> level);
                int currH = Math.Max(1, height >> level);
                currBuf = new byte[currW * currH * components];

                if (currW < 2 || currH < 2)
                {
                    int total = currW * currH;
                    for (int p = 0; p < total; p++)
                    {
                        for (int c = 0; c < components; c++)
                        {
                            currBuf[p * components + c] = reader.ReadRawByte();
                        }
                    }
                }
                else
                {
                    if (!reader.IsInitialized)
                    {
                        reader.InitBitstream();
                    }

                    if (reader.ReadBit() != 0)
                    {
                        int count = (currW * currH) >> 2;
                        for (int p = 0; p < count; p++)
                        {
                            for (int c = 0; c < components; c++)
                            {
                                short delta = reader.DecodeSymbolT3(t3);
                                prevBuf[p * components + c] = (byte)(prevBuf[p * components + c] + (sbyte)delta);
                            }
                        }
                    }

                    int prevW = Math.Max(1, currW >> 1);
                    int blocksH = currH >> 1;
                    int blocksW = currW >> 1;

                    for (int by = 0; by < blocksH; by++)
                    {
                        for (int bx = 0; bx < blocksW; bx++)
                        {
                            int prevPixelIdx = (by * prevW + bx) * components;

                            if (components != 1)
                            {
                                byte bit0 = reader.ReadBit();
                                short d1_ch0 = reader.DecodeSymbolT1(t1);
                                short d2_ch0 = reader.DecodeSymbolT1(t1);
                                short d3_ch0 = reader.DecodeSymbolT1(t1);

                                int a0 = prevBuf[prevPixelIdx + 0] * 2;
                                Set2x2(currBuf, currW, components, bx, by, 0,
                                    (byte)(((d2_ch0 + d3_ch0 + d1_ch0 + a0) >> 1) + bit0),
                                    (byte)(((a0 - d3_ch0 - d2_ch0 + d1_ch0) >> 1)),
                                    (byte)(((a0 - d3_ch0 - d1_ch0 + d2_ch0) >> 1)),
                                    (byte)(((d3_ch0 - d2_ch0 - d1_ch0 + a0) >> 1)));

                                if (components > 2)
                                {
                                    byte bit1 = reader.ReadBit();
                                    short d1_ch1 = (short)(reader.DecodeSymbolT2(t2) + d1_ch0);
                                    short d2_ch1 = (short)(reader.DecodeSymbolT2(t2) + d2_ch0);
                                    short d3_ch1 = (short)(reader.DecodeSymbolT2(t2) + d3_ch0);

                                    int a1 = prevBuf[prevPixelIdx + 1] * 2;
                                    Set2x2(currBuf, currW, components, bx, by, 1,
                                        (byte)(((d3_ch1 + d2_ch1 + d1_ch1 + a1) >> 1) + bit1),
                                        (byte)(((d1_ch1 - d3_ch1 - d2_ch1 + a1) >> 1)),
                                        (byte)(((d2_ch1 - d3_ch1 - d1_ch1 + a1) >> 1)),
                                        (byte)(((d3_ch1 - d2_ch1 - d1_ch1 + a1) >> 1)));

                                    byte bit2 = reader.ReadBit();
                                    short d1_ch2 = (short)(reader.DecodeSymbolT2(t2) + d1_ch0);
                                    short d2_ch2 = (short)(reader.DecodeSymbolT2(t2) + d2_ch0);
                                    short d3_ch2 = (short)(reader.DecodeSymbolT2(t2) + d3_ch0);

                                    int a2 = prevBuf[prevPixelIdx + 2] * 2;
                                    Set2x2(currBuf, currW, components, bx, by, 2,
                                        (byte)(((d3_ch2 + d2_ch2 + d1_ch2 + a2) >> 1) + bit2),
                                        (byte)(((d1_ch2 - d3_ch2 - d2_ch2 + a2) >> 1)),
                                        (byte)(((d2_ch2 - d3_ch2 - d1_ch2 + a2) >> 1)),
                                        (byte)(((d3_ch2 - d2_ch2 - d1_ch2 + a2) >> 1)));
                                }

                                if (components != 3)
                                {
                                    int chAlpha = components - 1;
                                    byte bitA = reader.ReadBit();
                                    short d1_a = reader.DecodeSymbolT3(t3);
                                    short d2_a = reader.DecodeSymbolT3(t3);
                                    short d3_a = reader.DecodeSymbolT3(t3);

                                    int aA = prevBuf[prevPixelIdx + chAlpha] * 2;
                                    Set2x2(currBuf, currW, components, bx, by, chAlpha,
                                        (byte)(((d3_a + d2_a + d1_a + aA) >> 1) + bitA),
                                        (byte)(((d1_a - d3_a - d2_a + aA) >> 1)),
                                        (byte)(((d2_a - d3_a - d1_a + aA) >> 1)),
                                        (byte)(((d3_a - d2_a - d1_a + aA) >> 1)));
                                }
                            }
                            else
                            {
                                byte bit0 = reader.ReadBit();
                                short d1 = reader.DecodeSymbolT3(t3);
                                short d2 = reader.DecodeSymbolT3(t3);
                                short d3 = reader.DecodeSymbolT3(t3);

                                int a = prevBuf[prevPixelIdx] * 2;
                                Set2x2(currBuf, currW, 1, bx, by, 0,
                                    (byte)(((d3 + d2 + d1 + a) >> 1) + bit0),
                                    (byte)(((d1 - d3 - d2 + a) >> 1)),
                                    (byte)(((d2 - d3 - d1 + a) >> 1)),
                                    (byte)(((d3 - d2 - d1 + a) >> 1)));
                            }
                        }
                    }
                }

                prevBuf = currBuf;
            }

            int totalPixels = width * height;
            byte[] bgra = new byte[totalPixels * 4];

            switch (fmtCode)
            {
                case 1 or 5 or 6:
                    for (int p = 0, s = 0, d = 0; p < totalPixels; p++, s += 4, d += 4)
                    {
                        bgra[d + 0] = currBuf[s + 2];
                        bgra[d + 1] = currBuf[s + 1];
                        bgra[d + 2] = currBuf[s + 0];
                        bgra[d + 3] = currBuf[s + 3];
                    }
                    break;
                case 2 or 7:
                    for (int p = 0, s = 0, d = 0; p < totalPixels; p++, s += 3, d += 4)
                    {
                        bgra[d + 0] = currBuf[s + 2];
                        bgra[d + 1] = currBuf[s + 1];
                        bgra[d + 2] = currBuf[s + 0];
                        bgra[d + 3] = 255;
                    }
                    break;
                case 3 or 8:
                    for (int p = 0, s = 0, d = 0; p < totalPixels; p++, s += 2, d += 4)
                    {
                        byte g = currBuf[s + 0];
                        bgra[d + 0] = g;
                        bgra[d + 1] = g;
                        bgra[d + 2] = g;
                        bgra[d + 3] = currBuf[s + 1];
                    }
                    break;
                case 4 or 9:
                    for (int p = 0, s = 0, d = 0; p < totalPixels; p++, s++, d += 4)
                    {
                        byte g = currBuf[s];
                        bgra[d + 0] = g;
                        bgra[d + 1] = g;
                        bgra[d + 2] = g;
                        bgra[d + 3] = 255;
                    }
                    break;
                case 10:
                    for (int p = 0, s = 0, d = 0; p < totalPixels; p++, s++, d += 4)
                    {
                        bgra[d + 0] = 0;
                        bgra[d + 1] = 0;
                        bgra[d + 2] = 0;
                        bgra[d + 3] = currBuf[s];
                    }
                    break;
            }

            return bgra;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Set2x2(byte[] buf, int width, int stride, int bx, int by, int ch, byte p00, byte p10, byte p01, byte p11)
        {
            int row0 = (by * 2) * width * stride;
            int row1 = (by * 2 + 1) * width * stride;
            int col0 = (bx * 2) * stride + ch;
            int col1 = (bx * 2 + 1) * stride + ch;

            buf[row0 + col0] = p00;
            buf[row0 + col1] = p10;
            buf[row1 + col0] = p01;
            buf[row1 + col1] = p11;
        }

        private sealed class WaveletBitReader
        {
            private readonly byte[] _data;
            private int _pos;
            private int _bitOffset;
            public ushort BitBuf { get; private set; }
            public bool IsInitialized { get; private set; }

            public WaveletBitReader(byte[] data, int startPos)
            {
                _data = data;
                _pos = startPos;
                _bitOffset = 0;
                BitBuf = 0;
                IsInitialized = false;
            }

            public byte ReadRawByte() => _pos < _data.Length ? _data[_pos++] : (byte)0;

            public void InitBitstream()
            {
                if (_pos + 1 < _data.Length)
                {
                    BitBuf = (ushort)(_data[_pos] | (_data[_pos + 1] << 8));
                    _pos += 2;
                }
                _bitOffset = 0;
                IsInitialized = true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Consume(int bits)
            {
                if (bits <= 0) return;

                // Читаем dword со СТАРОЙ позиции _pos до смещения:
                uint nextDword = 0;
                if (_pos < _data.Length)
                {
                    int rem = _data.Length - _pos;
                    if (rem >= 4) nextDword = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_pos, 4));
                    else if (rem == 3) nextDword = (uint)(_data[_pos] | (_data[_pos + 1] << 8) | (_data[_pos + 2] << 16));
                    else if (rem == 2) nextDword = (uint)(_data[_pos] | (_data[_pos + 1] << 8));
                    else if (rem == 1) nextDword = _data[_pos];
                }

                // Сдвигаем со СТАРОГО _bitOffset:
                ushort newBits = (ushort)(nextDword >> _bitOffset);
                BitBuf = (ushort)(((newBits) << (16 - bits)) | (BitBuf >> bits));

                // И только после этого передвигаем указатель вперед:
                int total = _bitOffset + bits;
                _pos += total >> 3;
                _bitOffset = total & 7;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public byte ReadBit()
            {
                byte b = (byte)(BitBuf & 1);
                Consume(1);
                return b;
            }

            public short DecodeSymbolT1(ReadOnlySpan<byte> t1)
            {
                int idx = BitBuf & 0xFFF;
                int off = idx * 4;
                short val = BinaryPrimitives.ReadInt16LittleEndian(t1.Slice(off, 2));
                ushort len = BinaryPrimitives.ReadUInt16LittleEndian(t1.Slice(off + 2, 2));
                Consume(len);

                if (val == -0x8000)
                {
                    val = (short)((BitBuf & 0x1FF) - 0xFF);
                    Consume(9);
                }
                return val;
            }

            public short DecodeSymbolT2(ReadOnlySpan<byte> t2)
            {
                int idx = BitBuf & 0xFFF;
                int off = idx * 4;
                short val = BinaryPrimitives.ReadInt16LittleEndian(t2.Slice(off, 2));
                ushort len = BinaryPrimitives.ReadUInt16LittleEndian(t2.Slice(off + 2, 2));
                Consume(len);

                if (val == -0x8000)
                {
                    val = (short)((BitBuf & 0x3FF) - 0x1FE);
                    Consume(10);
                }
                return val;
            }

            public short DecodeSymbolT3(ReadOnlySpan<byte> t3)
            {
                int idx = BitBuf & 0xFFF;
                int off = idx * 4;
                short val = BinaryPrimitives.ReadInt16LittleEndian(t3.Slice(off, 2));
                ushort len = BinaryPrimitives.ReadUInt16LittleEndian(t3.Slice(off + 2, 2));
                Consume(len);

                if (val == -0x8000)
                {
                    val = (short)((BitBuf & 0x1FF) - 0xFF);
                    Consume(9);
                }
                return val;
            }
        }

        // ==================== IWI -> DDS ====================

        public static byte[] IwiToDds(byte[] iwi)
        {
            IwiHeader hdr = ParseIwiHeader(iwi);

            if (IsWaveletFor(hdr))
            {
                byte[] bgra = DecodeWavelet(iwi, hdr);
                var ddsWavelet = new byte[128 + bgra.Length];
                WriteDdsHeader(ddsWavelet, IwiFormat.Argb32, hdr.Width, hdr.Height, 1);
                Buffer.BlockCopy(bgra, 0, ddsWavelet, 128, bgra.Length);
                return ddsWavelet;
            }

            int payloadLen = iwi.Length - hdr.HeaderSize;
            int topSize = LevelSize(hdr.Format, hdr.Width, hdr.Height);
            if (payloadLen < topSize)
                throw new InvalidDataException($"Файл IWI оборван: ожидалось минимум {topSize} байт данных, есть {payloadLen}.");

            int mips = DetectMipCount(hdr.Format, hdr.Width, hdr.Height, payloadLen);
            int levels = mips > 0 ? mips : 1;

            var srcOff = new int[levels];
            var srcLen = new int[levels];
            for (int i = 0; i < levels; i++)
                srcLen[i] = LevelSize(hdr.Format, Math.Max(1, hdr.Width >> i), Math.Max(1, hdr.Height >> i));

            if (mips > 0)
            {
                int pos = hdr.HeaderSize;
                if (MipsSmallestFirst)
                {
                    for (int i = levels - 1; i >= 0; i--) { srcOff[i] = pos; pos += srcLen[i]; }
                }
                else
                {
                    for (int i = 0; i < levels; i++) { srcOff[i] = pos; pos += srcLen[i]; }
                }
            }
            else
            {
                srcOff[0] = hdr.HeaderSize + (MipsSmallestFirst ? payloadLen - srcLen[0] : 0);
            }

            bool expand = hdr.Format is IwiFormat.GrayAlpha16 or IwiFormat.Alpha8;
            IwiFormat outFmt = expand ? IwiFormat.Argb32 : hdr.Format;

            var dstLen = new int[levels];
            long total = 128;
            for (int i = 0; i < levels; i++)
            {
                dstLen[i] = expand
                    ? LevelSize(IwiFormat.Argb32, Math.Max(1, hdr.Width >> i), Math.Max(1, hdr.Height >> i))
                    : srcLen[i];
                total += dstLen[i];
            }
            if (total > int.MaxValue) throw new InvalidDataException("Текстура слишком большая.");

            var dds = new byte[total];
            WriteDdsHeader(dds, outFmt, hdr.Width, hdr.Height, levels);

            int o = 128;
            for (int i = 0; i < levels; i++)
            {
                // Защита от «Index was outside the bounds of the array»: битый/нестандартный
                // файл должен давать понятную ошибку, а не падать внутри Buffer.BlockCopy.
                if (srcOff[i] < 0 || srcLen[i] < 0 || (long)srcOff[i] + srcLen[i] > iwi.Length)
                    throw new InvalidDataException(
                        $"Уровень {i} выходит за пределы файла: смещение {srcOff[i]}, размер {srcLen[i]}, файл {iwi.Length} байт.");

                switch (hdr.Format)
                {
                    case IwiFormat.GrayAlpha16:
                        for (int p = 0, s = srcOff[i], d = o; p < srcLen[i] / 2; p++, s += 2, d += 4)
                        {
                            byte l = iwi[s], a = iwi[s + 1];
                            dds[d] = l; dds[d + 1] = l; dds[d + 2] = l; dds[d + 3] = a;
                        }
                        break;
                    case IwiFormat.Alpha8:
                        for (int p = 0, s = srcOff[i], d = o; p < srcLen[i]; p++, s++, d += 4)
                        {
                            dds[d] = 255; dds[d + 1] = 255; dds[d + 2] = 255; dds[d + 3] = iwi[s];
                        }
                        break;
                    default:
                        Buffer.BlockCopy(iwi, srcOff[i], dds, o, srcLen[i]);
                        break;
                }
                o += dstLen[i];
            }
            return dds;
        }

        private static void WriteDdsHeader(byte[] d, IwiFormat fmt, int w, int h, int levels)
        {
            Span<byte> s = d;
            bool compressed = fmt is IwiFormat.Dxt1 or IwiFormat.Dxt3 or IwiFormat.Dxt5 or IwiFormat.Bc5;
            int bytesPerPixel = fmt == IwiFormat.Rgb24 ? 3 : fmt == IwiFormat.R8 ? 1 : 4;

            W32(s, 0, DdsMagic);
            W32(s, 4, 124);
            uint flags = 0x1007u | (levels > 1 ? 0x20000u : 0u) | (compressed ? 0x80000u : 0x8u);
            W32(s, 8, flags);
            W32(s, 12, (uint)h);
            W32(s, 16, (uint)w);
            W32(s, 20, compressed ? (uint)LevelSize(fmt, w, h) : (uint)(w * bytesPerPixel));
            W32(s, 28, levels > 1 ? (uint)levels : 0u);

            W32(s, 76, 32);
            switch (fmt)
            {
                case IwiFormat.Dxt1: W32(s, 80, 0x4); W32(s, 84, FourCcDxt1); break;
                case IwiFormat.Dxt3: W32(s, 80, 0x4); W32(s, 84, FourCcDxt3); break;
                case IwiFormat.Dxt5: W32(s, 80, 0x4); W32(s, 84, FourCcDxt5); break;
                case IwiFormat.Bc5: W32(s, 80, 0x4); W32(s, 84, FourCcAti2); break;
                case IwiFormat.R8:
                    W32(s, 80, 0x20000); W32(s, 88, 8);
                    W32(s, 92, 0x000000FF);
                    break;
                case IwiFormat.Rgb24:
                    W32(s, 80, 0x40); W32(s, 88, 24);
                    W32(s, 92, 0x00FF0000); W32(s, 96, 0x0000FF00); W32(s, 100, 0x000000FF);
                    break;
                default:
                    W32(s, 80, 0x41); W32(s, 88, 32);
                    W32(s, 92, 0x00FF0000); W32(s, 96, 0x0000FF00); W32(s, 100, 0x000000FF);
                    W32(s, 104, 0xFF000000);
                    break;
            }
            W32(s, 108, 0x1000u | (levels > 1 ? 0x400008u : 0u));
        }

        // ------------------------------------------------------------------ DDS -> IWI

        public static bool UsesPicmipTable(int codVerIndex)
            => (uint)codVerIndex < (uint)HeaderSizeByIndex.Length && HeaderSizeByIndex[codVerIndex] == 28;

        /// <summary>
        /// Поддерживает ли версия IWI мип-цепочку в файле.
        /// v6 (28 байт: CoD2/MW/WaW) — таблица из 4 размеров, v13 (48 байт: Black Ops) — из 8 размеров.
        /// Остальные версии (32/64 байта) пока пишем одним уровнем.
        /// </summary>
        public static bool SupportsMipChain(int codVerIndex)
            => (uint)codVerIndex < (uint)HeaderSizeByIndex.Length
               && HeaderSizeByIndex[codVerIndex] is 28 or 32 or 48 or 64;

        public static int FullMipCount(int w, int h)
        {
            int n = 1, m = Math.Max(w, h);
            while (m > 1) { m >>= 1; n++; }
            return n;
        }

        public static IwiBuildResult TryDdsToIwi(byte[] dds, int codVerIndex, bool wantMips, bool allowPartialChain, out byte[] iwi)
        {
            iwi = Array.Empty<byte>();
            if (dds.Length < 128 || U32(dds, 0) != DdsMagic || U32(dds, 4) != 124u) return IwiBuildResult.NotDxt;

            uint caps2 = U32(dds, 112);
            if ((caps2 & 0x200u) != 0 || (caps2 & 0x200000u) != 0) return IwiBuildResult.NotDxt;

            int h = (int)U32(dds, 12);
            int w = (int)U32(dds, 16);
            if (w < 1 || h < 1 || w > ushort.MaxValue || h > ushort.MaxValue) return IwiBuildResult.NotDxt;

            uint pfFlags = U32(dds, 80);
            uint fourCc = U32(dds, 84);
            bool isR8 = (pfFlags & 0x20000u) != 0 && U32(dds, 88) == 8;
            if ((pfFlags & 0x4u) == 0 && !isR8) return IwiBuildResult.NotDxt;

            int dataOffset = 128;
            IwiFormat fmt;
            if (isR8)
            {
                fmt = IwiFormat.R8;
            }
            else
            {
                switch (fourCc)
                {
                    case FourCcDxt1: fmt = IwiFormat.Dxt1; break;
                    case FourCcDxt3: fmt = IwiFormat.Dxt3; break;
                    case FourCcDxt5: fmt = IwiFormat.Dxt5; break;
                    case FourCcAti2: fmt = IwiFormat.Bc5; break;
                    case FourCcDx10:
                        if (dds.Length < 148 || U32(dds, 140) != 1u) return IwiBuildResult.NotDxt;
                        switch (U32(dds, 128))
                        {
                            case 71: case 72: fmt = IwiFormat.Dxt1; break;
                            case 74: case 75: fmt = IwiFormat.Dxt3; break;
                            case 77: case 78: fmt = IwiFormat.Dxt5; break;
                            case 83: fmt = IwiFormat.Bc5; break;
                            default: return IwiBuildResult.NotDxt;
                        }
                        dataOffset = 148;
                        break;
                    default:
                        return IwiBuildResult.NotDxt;
                }
            }

            // BC5/R8 есть только в Black Ops 2 (v27): форматы 0x0E и 0x05
            if ((fmt is IwiFormat.Bc5 or IwiFormat.R8) && codVerIndex != 0) return IwiBuildResult.NotDxt;

            int declared = (U32(dds, 8) & 0x20000u) != 0 ? (int)Math.Clamp(U32(dds, 28), 1u, 16u) : 1;
            long available = dds.Length - dataOffset;
            var offs = new List<int>();
            var lens = new List<int>();
            long used = 0;
            for (int i = 0; i < declared; i++)
            {
                long size = LevelSizeLong(fmt, Math.Max(1, w >> i), Math.Max(1, h >> i));
                if (used + size > available) break;
                offs.Add(dataOffset + (int)used);
                lens.Add((int)size);
                used += size;
            }
            if (offs.Count == 0) return IwiBuildResult.NotDxt;

            if (!SupportsMipChain(codVerIndex))
            {
                iwi = BuildIwi(codVerIndex, fmt, w, h, new ReadOnlySpan<byte>(dds, offs[0], lens[0]));
                return IwiBuildResult.Ok;
            }

            int levels = 1;
            if (wantMips)
            {
                if (offs.Count < FullMipCount(w, h) && !allowPartialChain) return IwiBuildResult.NeedMips;
                levels = offs.Count;
            }

            iwi = BuildIwiWithMips(codVerIndex, fmt, w, h, dds, offs.GetRange(0, levels), lens.GetRange(0, levels), wantMips);
            return IwiBuildResult.Ok;
        }

        private const byte FlagNoMipmaps = 0x02;

        private static byte[] BuildIwiWithMips(int codVerIndex, IwiFormat fmt, int w, int h, byte[] src,
                                               List<int> offs, List<int> lens, bool withMips)
        {
            int headerSize = HeaderSizeByIndex[codVerIndex];
            bool isV8 = headerSize == 32;   // MW2/MW3: 32-байтовый заголовок (флаги, затем формат)
            bool isV13 = headerSize == 48;  // Black Ops: 48-байтовый заголовок
            bool isV27 = headerSize == 64;  // Black Ops 2: 64-байтовый заголовок, таблица размеров с 32-го байта
            int sizesOffset = isV27 ? 32 : (isV8 ? 16 : (isV13 ? 16 : 12));
            int sizeEntries = (headerSize - sizesOffset) / 4;

            int levels = offs.Count;
            long total = headerSize;
            foreach (int len in lens) total += len;
            if (total > int.MaxValue) throw new InvalidDataException("Текстура слишком большая.");

            var iwi = new byte[total];
            iwi[0] = (byte)'I';
            iwi[1] = (byte)'W';
            iwi[2] = (byte)'i';
            iwi[3] = VersionByIndex[codVerIndex];

            if (isV8)
            {
                // v8: [4..8) — флаги (0 — с мипами, 0x173 — без), [8] — формат, [10]/[12] — размеры.
                BinaryPrimitives.WriteUInt32LittleEndian(iwi.AsSpan(4, 4), withMips ? 0u : 0x173u);
                iwi[8] = (byte)fmt;
                BinaryPrimitives.WriteUInt16LittleEndian(iwi.AsSpan(10, 2), (ushort)w);
                BinaryPrimitives.WriteUInt16LittleEndian(iwi.AsSpan(12, 2), (ushort)h);
                BinaryPrimitives.WriteUInt16LittleEndian(iwi.AsSpan(14, 2), 1);
            }
            else if (isV27)
            {
                // v27 (Black Ops 2): [4] — формат, [5] — флаги (0x10 — с мипами, 0xC3 — без),
                // [6]/[8] — ширина/высота, таблица размеров с 32-го байта.
                iwi[4] = (byte)fmt;
                iwi[5] = (byte)(withMips ? 0x10 : fmt == IwiFormat.R8 ? 0xC2 : 0xC3);
                BinaryPrimitives.WriteUInt16LittleEndian(iwi.AsSpan(6, 2), (ushort)w);
                BinaryPrimitives.WriteUInt16LittleEndian(iwi.AsSpan(8, 2), (ushort)h);
                BinaryPrimitives.WriteUInt16LittleEndian(iwi.AsSpan(10, 2), 1);
            }
            else
            {
                iwi[4] = (byte)fmt;
                iwi[5] = isV13
                    ? (byte)(withMips ? 0x10 : 0x12)
                    : (byte)(withMips ? 0x00 : FlagNoMipmaps);
                BinaryPrimitives.WriteUInt16LittleEndian(iwi.AsSpan(6, 2), (ushort)w);
                BinaryPrimitives.WriteUInt16LittleEndian(iwi.AsSpan(8, 2), (ushort)h);
                if (isV13) BinaryPrimitives.WriteUInt16LittleEndian(iwi.AsSpan(10, 2), 1);
            }

            var sizes = new uint[levels];
            uint running = (uint)headerSize;
            int pos = headerSize;
            if (MipsSmallestFirst)
            {
                for (int i = levels - 1; i >= 0; i--)
                {
                    Buffer.BlockCopy(src, offs[i], iwi, pos, lens[i]);
                    pos += lens[i];
                    running += (uint)lens[i];
                    sizes[i] = running;
                }
            }
            else
            {
                for (int i = 0; i < levels; i++)
                {
                    Buffer.BlockCopy(src, offs[i], iwi, pos, lens[i]);
                    pos += lens[i];
                }
                running = (uint)headerSize;
                for (int i = levels - 1; i >= 0; i--) { running += (uint)lens[i]; sizes[i] = running; }
            }

            for (int i = 0; i < sizeEntries; i++)
            {
                uint value = levels == 1 && i > 0
                    ? (isV8 || isV27 ? (uint)total : (uint)lens[0])
                    : sizes[Math.Min(i, levels - 1)];
                BinaryPrimitives.WriteUInt32LittleEndian(iwi.AsSpan(sizesOffset + i * 4, 4), value);
            }

            return iwi;
        }

        public static byte[] BuildIwi(int codVerIndex, IwiFormat fmt, int w, int h, ReadOnlySpan<byte> payload)
        {
            if ((uint)codVerIndex >= (uint)VersionByIndex.Length) codVerIndex = 4;
            int headerSize = HeaderSizeByIndex[codVerIndex];
            bool isV8 = headerSize == 32;
            bool isV27 = headerSize == 64;

            var iwi = new byte[headerSize + payload.Length];
            iwi[0] = (byte)'I';
            iwi[1] = (byte)'W';
            iwi[2] = (byte)'i';
            iwi[3] = VersionByIndex[codVerIndex];

            if (isV8)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(iwi.AsSpan(4, 4), 0x173u);
                iwi[8] = (byte)fmt;
                BinaryPrimitives.WriteUInt16LittleEndian(iwi.AsSpan(10, 2), (ushort)w);
                BinaryPrimitives.WriteUInt16LittleEndian(iwi.AsSpan(12, 2), (ushort)h);
                BinaryPrimitives.WriteUInt16LittleEndian(iwi.AsSpan(14, 2), 1);
                uint total = (uint)iwi.Length;
                for (int i = 0; i < 4; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(iwi.AsSpan(16 + i * 4, 4), total);
            }
            else if (isV27)
            {
                iwi[4] = (byte)fmt;
                iwi[5] = (byte)(fmt == IwiFormat.R8 ? 0xC2 : 0xC3);
                BinaryPrimitives.WriteUInt16LittleEndian(iwi.AsSpan(6, 2), (ushort)w);
                BinaryPrimitives.WriteUInt16LittleEndian(iwi.AsSpan(8, 2), (ushort)h);
                BinaryPrimitives.WriteUInt16LittleEndian(iwi.AsSpan(10, 2), 1);
                uint total = (uint)iwi.Length;
                for (int i = 0; i < 8; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(iwi.AsSpan(32 + i * 4, 4), total);
            }
            else
            {
                iwi[4] = (byte)fmt;
                BinaryPrimitives.WriteUInt16LittleEndian(iwi.AsSpan(6, 2), (ushort)w);
                BinaryPrimitives.WriteUInt16LittleEndian(iwi.AsSpan(8, 2), (ushort)h);
            }

            payload.CopyTo(iwi.AsSpan(headerSize));
            return iwi;
        }
        // ------------------------------------------------------------------ Debug

        private static string HexHead(byte[] data, int count)
            => BitConverter.ToString(data, 0, Math.Min(count, data.Length)).Replace('-', ' ');

        private static string FormatDisplayName(byte fmt) => fmt switch
        {
            0x01 => "ARGB32",
            0x02 => "RGB24",
            0x03 => "GA16",
            0x04 => "A8",
            0x0B => "DXT1",
            0x0C => "DXT3",
            0x0D => "DXT5",
            0x0E => "BC5 (ATI2)",
            _ => WaveletName(fmt) ?? "неизвестный"
        };

        /// <summary>Подробное описание файла по его началу: кодек, версия, формат, размеры и сами байты. Для режима Debug.</summary>
        public static string DescribeHead(byte[] head, long length, string ext, bool ru)
        {
            var sb = new StringBuilder();
            string hex = HexHead(head, 40);

            if (head.Length >= 12 && head[0] == (byte)'I' && head[1] == (byte)'W' && head[2] == (byte)'i')
            {
                byte ver = head[3];
                int headerSize = ver switch { 27 => 64, 13 => 48, 8 => 32, 5 or 6 => 28, _ => 0 };
                string games = ver switch { 5 => "CoD2", 6 => "CoD4 / WaW", 8 => "MW2 / MW3", 13 => "BO1", 27 => "BO2", _ => ru ? "неизвестная игра" : "unknown game" };
                byte fmt = ver == 8 ? head[8] : head[4];
                string fmtName = ver == 27 && fmt == 0x05 ? "R8" : FormatDisplayName(fmt);
                int w = ver == 8
                    ? BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10, 2))
                    : BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6, 2));
                int h = ver == 8
                    ? BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(12, 2))
                    : BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(8, 2));
                bool wavelet = ver != 27 && IsWavelet((IwiFormat)fmt);

                sb.Append(ru ? "    кодек: " : "    codec: ").Append($"IWI v{ver} ({games}), ")
                  .Append(ru ? "формат " : "format ").Append($"0x{fmt:X2} — {fmtName}").Append(wavelet ? (ru ? " (wavelet-сжатие CoD4)" : " (CoD4 wavelet compression)") : "").Append('\n');
                sb.Append(ru ? "    флаги: " : "    flags: ").Append($"0x{head[5]:X2}, ").Append(ru ? "размер: " : "size: ").Append($"{w}×{h}, ")
                  .Append(ru ? "заголовок: " : "header: ").Append(headerSize > 0 ? $"{headerSize} " + (ru ? "байт" : "bytes") : (ru ? "неизвестен" : "unknown")).Append('\n');

                if (headerSize > 0)
                {
                    long payload = length - headerSize;
                    sb.Append(ru ? "    данные после заголовка: " : "    data after header: ").Append($"{payload:N0} ").Append(ru ? "байт" : "bytes");
                    if (!wavelet && fmtName != "неизвестный" && w > 0 && h > 0)
                    {
                        try { sb.Append(ru ? $", верхний уровень: {LevelSizeLong((IwiFormat)fmt, w, h):N0} байт" : $", top level: {LevelSizeLong((IwiFormat)fmt, w, h):N0} bytes"); }
                        catch { }
                    }
                    sb.Append('\n');
                }
            }
            else if (head.Length >= 88 && U32(head, 0) == DdsMagic)
            {
                uint pfFlags = U32(head, 80), fcc = U32(head, 84);
                string fourCc = (pfFlags & 4u) != 0 ? Encoding.ASCII.GetString(head, 84, 4) : (ru ? "без сжатия" : "uncompressed");
                sb.Append(ru ? "    кодек: DDS, " : "    codec: DDS, ").Append(ru ? "формат пикселей: " : "pixel format: ").Append(fourCc)
                  .Append($", {U32(head, 16)}×{U32(head, 12)}, ").Append(ru ? "мипов: " : "mips: ").Append((U32(head, 8) & 0x20000u) != 0 ? U32(head, 28) : 1u).Append('\n');
            }
            else if (head.Length >= 26 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
            {
                sb.Append("    codec: PNG, ").Append($"{BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(16, 4))}×{BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(20, 4))}, ")
                  .Append(ru ? "глубина " : "depth ").Append(head[24]).Append(ru ? " бит, тип цвета " : " bit, color type ").Append(head[25]).Append('\n');
            }
            else if (VtfCodec.IsVtf(head))
            {
                sb.Append(VtfCodec.DescribeHeadLines(head, ru));
            }
            else if (ext.Equals(".tga", StringComparison.OrdinalIgnoreCase) && head.Length >= 18)
            {
                sb.Append("    codec: TGA, ").Append($"{BitConverter.ToUInt16(head, 12)}×{BitConverter.ToUInt16(head, 14)}, ")
                  .Append(ru ? "тип " : "type ").Append(head[2]).Append(head[2] >= 8 ? " (RLE), " : ", ").Append(head[16]).Append(ru ? " бит" : " bit").Append('\n');
            }
            else
            {
                sb.Append(ru ? "    кодек: не похоже на IWI, DDS, PNG или TGA\n" : "    codec: does not look like IWI, DDS, PNG or TGA\n");
            }

            sb.Append(ru ? "    начало файла: " : "    file start: ").Append(hex);
            return sb.ToString();
        }
    }
}