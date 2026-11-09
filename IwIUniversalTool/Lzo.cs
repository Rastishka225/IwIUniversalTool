using System;
using System.Collections.Generic;
using System.IO;

namespace IwIUniversalTool
{
    /// <summary>
    /// LZO1X: сжатие и распаковка. Нужен для IPAK (Black Ops 2): команды данных в IPAK сжимаются именно LZO.
    /// Формат потока стандартный, поэтому игра (lzo1x_decompress_safe) читает результат без отличий от оригинальных архивов.
    /// Каждый кусок сжимается отдельно (окно до 32 КБ), как это делает сама игра.
    /// </summary>
    internal static class Lzo
    {
        public const int MaxDistance = 0x7FFF;

        // ------------------------------------------------------------------ сжатие

        private readonly struct Seq
        {
            public readonly int Lits;   // сколько литералов перед совпадением
            public readonly int Len;    // длина совпадения
            public readonly int Dist;   // расстояние назад

            public Seq(int lits, int len, int dist)
            {
                Lits = lits;
                Len = len;
                Dist = dist;
            }
        }

        private sealed class ByteBuf
        {
            private byte[] _a;
            private int _n;

            public ByteBuf(int capacity)
            {
                _a = new byte[Math.Max(16, capacity)];
            }

            public int Length => _n;

            public void Add(int b)
            {
                if (_n == _a.Length) Array.Resize(ref _a, _a.Length * 2);
                _a[_n++] = (byte)b;
            }

            public void AddRange(byte[] src, int start, int count)
            {
                while (_n + count > _a.Length) Array.Resize(ref _a, _a.Length * 2);
                Buffer.BlockCopy(src, start, _a, _n, count);
                _n += count;
            }

            public byte[] ToArray()
            {
                var r = new byte[_n];
                Buffer.BlockCopy(_a, 0, r, 0, _n);
                return r;
            }
        }

        private static int Hash3(byte[] d, int i)
        {
            uint v = (uint)(d[i] | (d[i + 1] << 8) | (d[i + 2] << 16));
            return (int)(unchecked(v * 0x9E3779B1u) >> 17); // 15 бит
        }

        private sealed class Matcher
        {
            private readonly byte[] _d;
            private readonly int[] _head;
            private readonly int[]? _prev;
            private readonly bool _fast;
            private readonly int _lastEnd;
            private readonly int _n;

            public Matcher(byte[] data, bool fast)
            {
                _d = data;
                _n = data.Length;
                _fast = fast;
                _head = new int[1 << 15];
                Array.Fill(_head, -1);
                if (!fast)
                {
                    _prev = new int[_n];
                    Array.Fill(_prev, -1);
                }
                _lastEnd = _n - 3;
            }

            public void Insert(int p)
            {
                if (p > _lastEnd) return;
                int h = Hash3(_d, p);
                if (_prev != null) _prev[p] = _head[h];
                _head[h] = p;
            }

            /// <summary>Лучшее совпадение в позиции p. false — выгодного совпадения нет.</summary>
            public bool Best(int p, out int bestLen, out int bestDist)
            {
                bestLen = 0;
                bestDist = 0;
                if (p > _lastEnd) return false;

                int h = Hash3(_d, p);
                int cand = _head[h];
                int limit = Math.Min(_n - p, 0x4000);
                int tries = _fast ? 1 : 48;

                while (cand >= 0 && tries > 0)
                {
                    int dist = p - cand;
                    if (dist > MaxDistance) break;

                    if (bestLen < limit && _d[cand + bestLen] == _d[p + bestLen])
                    {
                        int l = 0;
                        while (l < limit && _d[cand + l] == _d[p + l]) l++;
                        if (l > bestLen)
                        {
                            bestLen = l;
                            bestDist = dist;
                            if (l >= limit) break;
                        }
                    }

                    if (_fast) break;
                    cand = _prev![cand];
                    tries--;
                }

                // совпадение из 3 байт выгодно только при короткой дистанции (2 байта кода вместо 3 литералов)
                if (bestLen < 3 || (bestLen == 3 && bestDist > 0x800))
                {
                    bestLen = 0;
                    bestDist = 0;
                    return false;
                }
                return true;
            }
        }

        private static List<Seq> FindSequences(byte[] data, bool maximum, out int tail)
        {
            int n = data.Length;
            var seqs = new List<Seq>();
            if (n < 8)
            {
                tail = n;
                return seqs;
            }

            bool fast = !maximum;
            var m = new Matcher(data, fast);
            int lastEnd = n - 3;
            int i = 1;
            int litStart = 0;
            m.Insert(0);

            while (i <= lastEnd)
            {
                if (!m.Best(i, out int ml, out int md))
                {
                    m.Insert(i);
                    i++;
                    continue;
                }

                if (!fast && i + 1 <= lastEnd)
                {
                    // ленивое сопоставление: если со следующей позиции совпадение заметно длиннее — берём его
                    m.Insert(i);
                    m.Best(i + 1, out int ml2, out _);
                    if (ml2 > ml + 1)
                    {
                        i++;
                        continue;
                    }

                    seqs.Add(new Seq(i - litStart, ml, md));
                    for (int p = i + 1; p < i + ml; p++) m.Insert(p);
                    i += ml;
                    litStart = i;
                    continue;
                }

                seqs.Add(new Seq(i - litStart, ml, md));
                for (int p = i; p < i + ml; p++) m.Insert(p);
                i += ml;
                litStart = i;
            }

            tail = n - litStart;
            return seqs;
        }

        /// <summary>Литеральный ран n &gt;= 4 в состоянии 0 (в начале потока или после совпадения без хвоста).</summary>
        private static void EmitLiteralRun(ByteBuf o, byte[] data, int start, int n)
        {
            if (n <= 18)
            {
                o.Add(n - 3);
            }
            else
            {
                o.Add(0);
                int t = n - 18;
                while (t > 255)
                {
                    o.Add(0);
                    t -= 255;
                }
                o.Add(t);
            }
            o.AddRange(data, start, n);
        }

        /// <summary>Совпадение длиной &gt;= 3 и s хвостовых литералов (0..3; сами байты дописывает вызывающий).</summary>
        private static void EmitMatch(ByteBuf o, int length, int dist, int s)
        {
            if (dist <= 0x800 && length <= 8)
            {
                int d = dist - 1;
                o.Add(((length - 1) << 5) | ((d & 7) << 2) | s);
                o.Add(d >> 3);
            }
            else if (dist <= 0x4000)
            {
                int d = dist - 1;
                int l = length - 2;
                if (l <= 31)
                {
                    o.Add(32 | l);
                }
                else
                {
                    o.Add(32);
                    l -= 31;
                    while (l > 255)
                    {
                        o.Add(0);
                        l -= 255;
                    }
                    o.Add(l);
                }
                int v = (d << 2) | s;
                o.Add(v & 0xFF);
                o.Add((v >> 8) & 0xFF);
            }
            else
            {
                int d = dist - 0x4000; // окно меньше 0x8000, поэтому d укладывается в 14 бит
                int l = length - 2;
                if (l <= 7)
                {
                    o.Add(16 | l);
                }
                else
                {
                    o.Add(16);
                    l -= 7;
                    while (l > 255)
                    {
                        o.Add(0);
                        l -= 255;
                    }
                    o.Add(l);
                }
                int v = (d << 2) | s;
                o.Add(v & 0xFF);
                o.Add((v >> 8) & 0xFF);
            }
        }

        /// <summary>Сжимает кусок данных (до 32 КБ). maximum = true: перебор вариантов, файл меньше, но медленнее.</summary>
        public static byte[] Compress(byte[] data, bool maximum)
        {
            List<Seq> seqs = FindSequences(data, maximum, out int tail);
            var o = new ByteBuf(data.Length / 2 + 64);
            int pos = 0;
            int cnt = seqs.Count;

            for (int k = 0; k < cnt; k++)
            {
                Seq q = seqs[k];
                if (k == 0)
                {
                    if (q.Lits <= 3)
                    {
                        o.Add(17 + q.Lits);
                        o.AddRange(data, pos, q.Lits);
                    }
                    else
                    {
                        EmitLiteralRun(o, data, pos, q.Lits);
                    }
                }
                else if (q.Lits >= 4)
                {
                    EmitLiteralRun(o, data, pos, q.Lits);
                }
                // до 3 литералов между совпадениями уже записаны как хвост предыдущего совпадения
                pos += q.Lits;

                int next = k + 1 < cnt ? seqs[k + 1].Lits : tail;
                int s = next <= 3 ? next : 0;
                EmitMatch(o, q.Len, q.Dist, s);
                pos += q.Len;
                if (s > 0) o.AddRange(data, pos, s);
            }

            if (cnt == 0)
            {
                if (tail > 0)
                {
                    if (tail <= 3)
                    {
                        o.Add(17 + tail);
                        o.AddRange(data, 0, tail);
                    }
                    else
                    {
                        EmitLiteralRun(o, data, 0, tail);
                    }
                }
            }
            else if (tail >= 4)
            {
                EmitLiteralRun(o, data, pos, tail);
            }

            // маркер конца потока
            o.Add(0x11);
            o.Add(0);
            o.Add(0);
            return o.ToArray();
        }

        // ------------------------------------------------------------------ распаковка

        private const int ModeLoop = 0;
        private const int ModeFirstLiteralRun = 1;
        private const int ModeMatch = 2;
        private const int ModeMatchDone = 3;
        private const int ModeMatchNext = 4;

        private static InvalidDataException Bad(string what) => new InvalidDataException("LZO: " + what);

        /// <summary>Распаковывает поток LZO1X в dst. Возвращает длину результата.</summary>
        public static int Decompress(byte[] src, byte[] dst)
        {
            int n = src.Length;
            int ip = 0;
            int op = 0;
            int max = dst.Length;
            if (n == 0) throw Bad("пустой вход");

            int t = src[ip];
            int next = 0;
            int mode = ModeLoop;

            if (t > 17)
            {
                ip++;
                t -= 17;
                if (t < 4)
                {
                    next = t;
                    mode = ModeMatchNext;
                }
                else
                {
                    if (ip + t > n || op + t > max) throw Bad("выход за границы при копировании литералов");
                    Buffer.BlockCopy(src, ip, dst, op, t);
                    ip += t;
                    op += t;
                    mode = ModeFirstLiteralRun;
                }
            }

            while (true)
            {
                switch (mode)
                {
                    case ModeLoop:
                    {
                        if (ip >= n) throw Bad("неожиданный конец данных");
                        t = src[ip++];
                        if (t >= 16)
                        {
                            mode = ModeMatch;
                            break;
                        }
                        if (t == 0)
                        {
                            if (ip >= n) throw Bad("неожиданный конец данных");
                            while (src[ip] == 0)
                            {
                                t += 255;
                                ip++;
                                if (ip >= n) throw Bad("неожиданный конец данных");
                            }
                            t += 15 + src[ip++];
                        }
                        t += 3;
                        if (ip + t > n || op + t > max) throw Bad("выход за границы при копировании литералов");
                        Buffer.BlockCopy(src, ip, dst, op, t);
                        ip += t;
                        op += t;
                        mode = ModeFirstLiteralRun;
                        break;
                    }

                    case ModeFirstLiteralRun:
                    {
                        if (ip >= n) throw Bad("неожиданный конец данных");
                        t = src[ip++];
                        if (t >= 16)
                        {
                            mode = ModeMatch;
                            break;
                        }
                        if (ip >= n) throw Bad("неожиданный конец данных");
                        int dist1 = 0x801 + (t >> 2) + (src[ip++] << 2);
                        op = CopyMatch(dst, op, max, dist1, 3);
                        mode = ModeMatchDone;
                        break;
                    }

                    case ModeMatch:
                    {
                        if (t >= 64)
                        {
                            if (ip >= n) throw Bad("неожиданный конец данных");
                            int dist2 = 1 + ((t >> 2) & 7) + (src[ip++] << 3);
                            int cnt2 = (t >> 5) + 1;
                            op = CopyMatch(dst, op, max, dist2, cnt2);
                        }
                        else if (t >= 32)
                        {
                            int l = t & 31;
                            if (l == 0)
                            {
                                if (ip >= n) throw Bad("неожиданный конец данных");
                                while (src[ip] == 0)
                                {
                                    l += 255;
                                    ip++;
                                    if (ip >= n) throw Bad("неожиданный конец данных");
                                }
                                l += 31 + src[ip++];
                            }
                            if (ip + 2 > n) throw Bad("неожиданный конец данных");
                            int v = src[ip] | (src[ip + 1] << 8);
                            ip += 2;
                            op = CopyMatch(dst, op, max, 1 + (v >> 2), l + 2);
                        }
                        else if (t >= 16)
                        {
                            int l = t & 7;
                            if (l == 0)
                            {
                                if (ip >= n) throw Bad("неожиданный конец данных");
                                while (src[ip] == 0)
                                {
                                    l += 255;
                                    ip++;
                                    if (ip >= n) throw Bad("неожиданный конец данных");
                                }
                                l += 7 + src[ip++];
                            }
                            if (ip + 2 > n) throw Bad("неожиданный конец данных");
                            int v = src[ip] | (src[ip + 1] << 8);
                            ip += 2;
                            int d = ((t & 8) << 11) + (v >> 2);
                            if (d == 0) return op; // конец потока
                            op = CopyMatch(dst, op, max, d + 0x4000, l + 2);
                        }
                        else
                        {
                            // короткое совпадение из 2 байт (после 1..3 литералов)
                            if (ip >= n) throw Bad("неожиданный конец данных");
                            int dist4 = 1 + (t >> 2) + (src[ip++] << 2);
                            op = CopyMatch(dst, op, max, dist4, 2);
                        }
                        mode = ModeMatchDone;
                        break;
                    }

                    case ModeMatchDone:
                    {
                        t = src[ip - 2] & 3;
                        if (t == 0)
                        {
                            mode = ModeLoop;
                        }
                        else
                        {
                            next = t;
                            mode = ModeMatchNext;
                        }
                        break;
                    }

                    default: // ModeMatchNext
                    {
                        if (ip + next > n || op + next > max) throw Bad("выход за границы при копировании литералов");
                        Buffer.BlockCopy(src, ip, dst, op, next);
                        ip += next;
                        op += next;
                        if (ip >= n) throw Bad("неожиданный конец данных");
                        t = src[ip++];
                        mode = ModeMatch;
                        break;
                    }
                }
            }
        }

        private static int CopyMatch(byte[] dst, int op, int max, int dist, int count)
        {
            if (dist <= 0 || dist > op || op + count > max) throw Bad("неверная ссылка назад");
            int srcPos = op - dist;
            for (int j = 0; j < count; j++) dst[op++] = dst[srcPos++];
            return op;
        }
    }
}
