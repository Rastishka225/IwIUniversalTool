using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace IwIUniversalTool
{
    internal enum PackResult { Added, Replaced, Skipped }

    /// <summary>Приёмник упаковки: сюда по одной подаются готовые IWI. Пока не вызван Finish, исходный архив не трогается.</summary>
    internal interface IArchiveSink : IDisposable
    {
        /// <summary>true — при замене вернуть в previous прежний файл из архива (до замены).</summary>
        bool CapturePrevious { get; set; }

        PackResult Add(string name, byte[] iwi, out byte[]? previous);
        void Finish();
    }

    internal readonly record struct IpakIndexEntry(uint NameHash, uint DataHash, uint Offset, uint Size);

    internal static class IpakUtil
    {
        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        /// <summary>Обычный CRC32 (как zlib crc32).</summary>
        public static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++) c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        /// <summary>Хеш данных в индексе IPAK: CRC32 с обнулёнными тремя старшими битами.</summary>
        public static uint DataHash(byte[] data) => Crc32(data) & 0x1FFFFFFFu;

        /// <summary>R_HashString(name, 0) из Black Ops 2: hash = 33 * hash ^ (c | 0x20).</summary>
        public static uint NameHash(string name)
        {
            uint h = 0;
            foreach (byte b in Encoding.UTF8.GetBytes(name))
            {
                int c = (sbyte)b; // в C++ char со знаком
                h = unchecked(33u * h) ^ unchecked((uint)(c | 0x20));
            }
            return h;
        }

        public static long AlignUp(long v, long a) => (v + a - 1) / a * a;

        public static uint ReadU32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

        public static void WriteU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }
    }

    /// <summary>
    /// Сборка IPAK по описанию формата (OpenAssetTools):
    /// файл состоит из чанков по 0x8000; первый чанк — заголовок и описания секций, дальше секция данных и секция индекса.
    /// Данные картинки лежат в блоках (заголовок 128 байт: смещение, число команд, 31 команда «размер + сжато/нет»).
    /// </summary>
    internal sealed class IpakBuilder : IDisposable
    {
        public const int Chunk = 0x8000;
        public const int BlockAlign = 0x80;
        public const int BlockHeaderSize = 128;
        public const int PieceSize = 0x8000;       // сколько байт исходного файла приходится на одну команду
        public const int MaxCommands = 31;
        // Игра держит в памяти не больше 8 чанков подряд. Блок ограничен 7 чанками, чтобы при любом старте внутри чанка не выйти за лимит.
        public const int MaxBlockTotal = 0x38000;

        private static readonly byte[] Zero = new byte[0x10000];

        private readonly string _finalPath;
        private readonly string _tempPath;
        private readonly FileStream _fs;
        private readonly int _level; // 0 = максимально, 1 = обычно, 2 = без сжатия
        private readonly List<IpakIndexEntry> _index = new();
        private long _pos;
        private bool _finished;

        public IpakBuilder(string finalPath, int level)
        {
            _finalPath = finalPath;
            _level = level;
            _tempPath = finalPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            _fs = new FileStream(_tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.SequentialScan);
            _fs.SetLength(Chunk);   // первый чанк (заголовок) допишем в конце
            _fs.Position = Chunk;
            _pos = Chunk;
        }

        private void PadTo(long target)
        {
            while (_pos < target)
            {
                int n = (int)Math.Min(Zero.Length, target - _pos);
                _fs.Write(Zero, 0, n);
                _pos += n;
            }
        }

        /// <summary>Записывает данные одной картинки (целиком файл .iwi). Возвращает смещение (от начала секции данных) и размер.</summary>
        public (uint Offset, uint Size) WriteImage(byte[] data)
        {
            PadTo(IpakUtil.AlignUp(_pos, BlockAlign));
            long start = _pos;

            var payloads = new List<byte[]>();
            var packed = new List<bool>();
            var rawLens = new List<int>();

            for (int p = 0; p < data.Length; p += PieceSize)
            {
                int len = Math.Min(PieceSize, data.Length - p);
                var raw = new byte[len];
                Buffer.BlockCopy(data, p, raw, 0, len);

                if (_level != 2)
                {
                    byte[] c = Lzo.Compress(raw, _level == 0);
                    if (c.Length < len)
                    {
                        payloads.Add(c);
                        packed.Add(true);
                        rawLens.Add(len);
                        continue;
                    }
                }
                payloads.Add(raw);
                packed.Add(false);
                rawLens.Add(len);
            }
            if (payloads.Count == 0)
            {
                payloads.Add(Array.Empty<byte>());
                packed.Add(false);
                rawLens.Add(0);
            }

            long rawOffset = 0;
            int i = 0;
            while (i < payloads.Count)
            {
                PadTo(IpakUtil.AlignUp(_pos, BlockAlign));

                int first = i;
                int total = BlockHeaderSize;
                int count = 0;
                while (i < payloads.Count && count < MaxCommands)
                {
                    int plen = payloads[i].Length;
                    if (count > 0 && total + plen > MaxBlockTotal) break;
                    total += plen;
                    count++;
                    i++;
                }

                if (rawOffset > 0xFFFFFF) throw new InvalidDataException("Текстура слишком большая для IPAK (больше 16 МБ).");

                var hdr = new byte[BlockHeaderSize];
                IpakUtil.WriteU32(hdr, 0, (uint)(rawOffset & 0xFFFFFF) | ((uint)count << 24));
                for (int k = 0; k < count; k++)
                {
                    uint cmd = (uint)(payloads[first + k].Length & 0xFFFFFF) | (packed[first + k] ? 1u << 24 : 0u);
                    IpakUtil.WriteU32(hdr, 4 + 4 * k, cmd);
                }
                _fs.Write(hdr, 0, hdr.Length);
                _pos += hdr.Length;

                for (int k = 0; k < count; k++)
                {
                    byte[] pl = payloads[first + k];
                    _fs.Write(pl, 0, pl.Length);
                    _pos += pl.Length;
                    rawOffset += rawLens[first + k];
                }
            }

            long end = IpakUtil.AlignUp(_pos, BlockAlign);
            PadTo(end);
            return ((uint)(start - Chunk), (uint)(end - start));
        }

        public void AddIndex(uint nameHash, uint dataHash, uint offset, uint size)
            => _index.Add(new IpakIndexEntry(nameHash, dataHash, offset, size));

        /// <summary>Дописывает индекс и заголовок, затем заменяет итоговый файл.</summary>
        public void Finish()
        {
            PadTo(IpakUtil.AlignUp(_pos, Chunk));
            long dataSize = _pos - Chunk;

            // индекс отсортирован по ключу «хеш имени, хеш данных»
            var sorted = _index.OrderBy(e => ((ulong)e.NameHash << 32) | e.DataHash).ToList();
            long indexOffset = _pos;
            var idx = new byte[16 * sorted.Count];
            for (int k = 0; k < sorted.Count; k++)
            {
                IpakUtil.WriteU32(idx, 16 * k, sorted[k].DataHash);
                IpakUtil.WriteU32(idx, 16 * k + 4, sorted[k].NameHash);
                IpakUtil.WriteU32(idx, 16 * k + 8, sorted[k].Offset);
                IpakUtil.WriteU32(idx, 16 * k + 12, sorted[k].Size);
            }
            _fs.Write(idx, 0, idx.Length);
            _pos += idx.Length;
            PadTo(IpakUtil.AlignUp(_pos, Chunk));
            long indexSize = _pos - indexOffset;
            long total = _pos;

            int images = sorted.Select(e => e.Offset).Distinct().Count();

            var hdr = new byte[48];
            IpakUtil.WriteU32(hdr, 0, 0x4950414Bu);            // "KAPI"
            IpakUtil.WriteU32(hdr, 4, 0x50000u);               // версия
            IpakUtil.WriteU32(hdr, 8, (uint)total);            // размер файла
            IpakUtil.WriteU32(hdr, 12, 2u);                    // число секций
            IpakUtil.WriteU32(hdr, 16, 2u);                    // секция данных
            IpakUtil.WriteU32(hdr, 20, (uint)Chunk);
            IpakUtil.WriteU32(hdr, 24, (uint)dataSize);
            IpakUtil.WriteU32(hdr, 28, (uint)images);
            IpakUtil.WriteU32(hdr, 32, 1u);                    // секция индекса
            IpakUtil.WriteU32(hdr, 36, (uint)indexOffset);
            IpakUtil.WriteU32(hdr, 40, (uint)indexSize);
            IpakUtil.WriteU32(hdr, 44, (uint)sorted.Count);
            _fs.Position = 0;
            _fs.Write(hdr, 0, hdr.Length);
            _fs.Flush();
            _fs.Dispose();

            File.Move(_tempPath, _finalPath, true);
            _finished = true;
        }

        public void Dispose()
        {
            if (_finished) return;
            _finished = true;
            try { _fs.Dispose(); } catch { }
            try { if (File.Exists(_tempPath)) File.Delete(_tempPath); } catch { }
        }
    }

    /// <summary>Чтение существующего IPAK: нужно, чтобы заменить или добавить картинки, не потеряв остальные.</summary>
    internal sealed class IpakReader : IDisposable
    {
        private readonly FileStream _fs;
        private long _dataOffset;
        public List<IpakIndexEntry> Index { get; } = new();

        private IpakReader(FileStream fs)
        {
            _fs = fs;
        }

        public static IpakReader Open(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            try
            {
                var r = new IpakReader(fs);
                r.ReadHeader();
                return r;
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        private void ReadHeader()
        {
            var head = new byte[16];
            _fs.Position = 0;
            _fs.ReadExactly(head, 0, 16);
            if (IpakUtil.ReadU32(head, 0) != 0x4950414Bu) throw new InvalidDataException("Это не IPAK (нет сигнатуры).");
            uint sections = IpakUtil.ReadU32(head, 12);
            if (sections == 0 || sections > 64) throw new InvalidDataException("Повреждён заголовок IPAK.");

            long indexOffset = -1, dataOffset = -1;
            uint indexCount = 0;
            var sec = new byte[16];
            for (int k = 0; k < sections; k++)
            {
                _fs.Position = 16 + 16 * k;
                _fs.ReadExactly(sec, 0, 16);
                uint type = IpakUtil.ReadU32(sec, 0);
                if (type == 1)
                {
                    indexOffset = IpakUtil.ReadU32(sec, 4);
                    indexCount = IpakUtil.ReadU32(sec, 12);
                }
                else if (type == 2)
                {
                    dataOffset = IpakUtil.ReadU32(sec, 4);
                }
            }
            if (indexOffset < 0 || dataOffset < 0) throw new InvalidDataException("В IPAK нет секции данных или индекса.");
            _dataOffset = dataOffset;

            var buf = new byte[16];
            _fs.Position = indexOffset;
            for (uint k = 0; k < indexCount; k++)
            {
                _fs.ReadExactly(buf, 0, 16);
                Index.Add(new IpakIndexEntry(IpakUtil.ReadU32(buf, 4), IpakUtil.ReadU32(buf, 0), IpakUtil.ReadU32(buf, 8), IpakUtil.ReadU32(buf, 12)));
            }
        }

        /// <summary>Собирает исходный файл картинки из блоков (с распаковкой LZO).</summary>
        public byte[] ReadImage(uint offset, uint size)
        {
            long start = _dataOffset + offset;
            long pos = start;
            using var ms = new MemoryStream();
            var hdr = new byte[IpakBuilder.BlockHeaderSize];
            var decomp = new byte[0x40000];

            while (pos - start < size)
            {
                pos = IpakUtil.AlignUp(pos, IpakBuilder.BlockAlign);
                if (pos - start >= size) break;

                _fs.Position = pos;
                _fs.ReadExactly(hdr, 0, hdr.Length);
                uint word = IpakUtil.ReadU32(hdr, 0);
                long cur = word & 0xFFFFFF;
                int count = (int)(word >> 24);
                if (count < 1 || count > IpakBuilder.MaxCommands) throw new InvalidDataException("Повреждён блок IPAK.");

                long p = pos + IpakBuilder.BlockHeaderSize;
                for (int k = 0; k < count; k++)
                {
                    uint cmd = IpakUtil.ReadU32(hdr, 4 + 4 * k);
                    int csize = (int)(cmd & 0xFFFFFF);
                    int flag = (int)(cmd >> 24);

                    if (flag == 0 || flag == 1)
                    {
                        var chunk = new byte[csize];
                        _fs.Position = p;
                        _fs.ReadExactly(chunk, 0, csize);

                        byte[] piece;
                        int plen;
                        if (flag == 0)
                        {
                            piece = chunk;
                            plen = csize;
                        }
                        else
                        {
                            plen = Lzo.Decompress(chunk, decomp);
                            piece = decomp;
                        }

                        if (ms.Length < cur) ms.SetLength(cur);
                        ms.Position = cur;
                        ms.Write(piece, 0, plen);
                        cur += plen;
                    }
                    // остальные значения (например, 0xCF — пропуск) игра игнорирует, данные просто пропускаем
                    p += csize;
                }
                pos = p;
            }
            return ms.ToArray();
        }

        public void Dispose() => _fs.Dispose();
    }

    /// <summary>Упаковка в IPAK. Если файл уже есть, его содержимое сохраняется, а совпавшие по имени картинки заменяются или пропускаются.</summary>
    internal sealed class IpakSink : IArchiveSink
    {
        private readonly IpakBuilder _builder;
        private readonly IpakReader? _old;
        private readonly Dictionary<uint, List<IpakIndexEntry>> _oldByName = new();
        private readonly HashSet<uint> _replaced = new();
        private readonly bool _replace;

        public bool CapturePrevious { get; set; }

        public IpakSink(string archivePath, int level, bool replaceExisting)
        {
            _replace = replaceExisting;
            if (File.Exists(archivePath))
            {
                try
                {
                    _old = IpakReader.Open(archivePath);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException("Существующий IPAK не удалось прочитать, поэтому он не будет перезаписан: " + ex.Message);
                }
                foreach (var e in _old.Index)
                {
                    if (!_oldByName.TryGetValue(e.NameHash, out var list)) _oldByName[e.NameHash] = list = new List<IpakIndexEntry>();
                    list.Add(e);
                }
            }
            _builder = new IpakBuilder(archivePath, level);
        }

        public PackResult Add(string name, byte[] iwi, out byte[]? previous)
        {
            previous = null;
            uint nameHash = IpakUtil.NameHash(name);

            if (_oldByName.TryGetValue(nameHash, out var olds))
            {
                if (!_replace) return PackResult.Skipped;

                if (CapturePrevious && _old != null)
                {
                    try { previous = _old.ReadImage(olds[0].Offset, olds[0].Size); }
                    catch { previous = null; }
                }

                // Игра ищет картинку по паре «хеш имени + хеш данных» и доверяет хешу из индекса.
                // Поэтому новые данные кладём под прежние хеши — так заменяется и оригинальная текстура.
                var (offset, size) = _builder.WriteImage(iwi);
                foreach (var o in olds) _builder.AddIndex(nameHash, o.DataHash, offset, size);
                _replaced.Add(nameHash);
                return PackResult.Replaced;
            }

            var (off2, size2) = _builder.WriteImage(iwi);
            _builder.AddIndex(nameHash, IpakUtil.DataHash(iwi), off2, size2);
            return PackResult.Added;
        }

        public void Finish()
        {
            if (_old != null)
            {
                // переносим старые картинки, которые не заменялись; общие данные копируем один раз
                var copied = new Dictionary<(uint, uint), (uint Offset, uint Size)>();
                foreach (var e in _old.Index)
                {
                    if (_replaced.Contains(e.NameHash)) continue;
                    var key = (e.Offset, e.Size);
                    if (!copied.TryGetValue(key, out var moved))
                    {
                        byte[] data = _old.ReadImage(e.Offset, e.Size);
                        moved = _builder.WriteImage(data);
                        copied[key] = moved;
                    }
                    _builder.AddIndex(e.NameHash, e.DataHash, moved.Offset, moved.Size);
                }
                _old.Dispose();
            }
            _builder.Finish();
        }

        public void Dispose()
        {
            try { _old?.Dispose(); } catch { }
            _builder.Dispose();
        }
    }

    /// <summary>Упаковка в IWD (обычный ZIP): текстуры лежат в папке images/ — иначе игра их не увидит.</summary>
    /// <summary>
    /// Проверка, стоит ли сжимать запись Deflate. Текстуры (IWI/DDS) уже сжаты DXT,
    /// и Deflate на них даёт файл БОЛЬШЕ исходного — такие записи надо класть без сжатия.
    /// </summary>
    internal static class ZipCompression
    {
        private sealed class Counter : Stream
        {
            public long Count;
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => Count;
            public override long Position { get => Count; set { } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => 0;
            public override long Seek(long offset, SeekOrigin origin) => 0;
            public override void SetLength(long value) { }
            public override void Write(byte[] buffer, int offset, int count) => Count += count;
        }

        /// <summary>Быстрая оценка (Fastest): уменьшится ли запись от Deflate.</summary>
        public static bool DeflateShrinks(byte[] data)
        {
            if (data.Length < 512) return false;
            var counter = new Counter();
            using (var ds = new DeflateStream(counter, CompressionLevel.Fastest, true))
                ds.Write(data, 0, data.Length);
            return counter.Count < data.Length;
        }

        /// <summary>Быстрая оценка (Fastest): сколько байт даст Deflate для этих данных.</summary>
        public static long MeasureDeflate(Stream source)
        {
            var counter = new Counter();
            using (var ds = new DeflateStream(counter, CompressionLevel.Fastest, true))
                source.CopyTo(ds);
            return counter.Count;
        }

        /// <summary>То же для потока (при пересжатии существующего архива).</summary>
        public static bool DeflateShrinks(Stream source, long rawLength)
        {
            if (rawLength < 512) return false;
            return MeasureDeflate(source) < rawLength;
        }
    }

    internal sealed class IwdSink : IArchiveSink
    {
        private readonly string _finalPath;
        private readonly string _tempPath;
        private readonly ZipArchive _zip;
        private readonly CompressionLevel _level;
        private readonly bool _replace;
        private readonly Dictionary<string, ZipArchiveEntry> _existing = new(StringComparer.OrdinalIgnoreCase);
        private bool _done;

        public bool CapturePrevious { get; set; }

        public IwdSink(string archivePath, CompressionLevel level, bool replaceExisting)
        {
            _finalPath = archivePath;
            _level = level;
            _replace = replaceExisting;
            _tempPath = archivePath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";

            bool exists = File.Exists(archivePath);
            FileStream fs;
            if (exists)
            {
                // работаем с копией: если что-то пойдёт не так, исходный IWD останется целым
                File.Copy(archivePath, _tempPath, true);
                fs = new FileStream(_tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            else
            {
                fs = new FileStream(_tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            }

            try
            {
                _zip = new ZipArchive(fs, exists ? ZipArchiveMode.Update : ZipArchiveMode.Create, false);
            }
            catch (Exception ex)
            {
                fs.Dispose();
                try { File.Delete(_tempPath); } catch { }
                throw new InvalidDataException("Существующий IWD не удалось прочитать как ZIP: " + ex.Message);
            }

            if (exists)
            {
                foreach (var e in _zip.Entries.ToList()) _existing[e.FullName] = e;
            }
        }

        public PackResult Add(string name, byte[] iwi, out byte[]? previous)
        {
            previous = null;
            string key = "images/" + name + ".iwi";
            bool had = _existing.TryGetValue(key, out ZipArchiveEntry? old);
            if (had && !_replace) return PackResult.Skipped;
            if (had && old != null)
            {
                if (CapturePrevious)
                {
                    try
                    {
                        using Stream rs = old.Open();
                        using var ms = new MemoryStream();
                        rs.CopyTo(ms);
                        previous = ms.ToArray();
                    }
                    catch { previous = null; }
                }
                old.Delete();
                _existing.Remove(key);
            }

            // Текстуры уже сжаты DXT: Deflate на них только увеличивает архив, поэтому
            // сжимаем запись лишь когда она реально становится меньше.
            CompressionLevel entryLevel = _level != CompressionLevel.NoCompression && ZipCompression.DeflateShrinks(iwi)
                ? _level
                : CompressionLevel.NoCompression;
            ZipArchiveEntry entry = _zip.CreateEntry(key, entryLevel);
            using (Stream s = entry.Open()) s.Write(iwi, 0, iwi.Length);
            _existing[key] = entry;
            return had ? PackResult.Replaced : PackResult.Added;
        }

        public void Finish()
        {
            _zip.Dispose();
            File.Move(_tempPath, _finalPath, true);
            _done = true;
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            try { _zip.Dispose(); } catch { }
            try { if (File.Exists(_tempPath)) File.Delete(_tempPath); } catch { }
        }
    }

    /// <summary>
    /// Пересжатие готового архива другим уровнем (0 = максимум, 1 = обычно, 2 = без сжатия). Содержимое не меняется.
    /// Новый файл пишется рядом во временный и только в конце заменяет исходный: при ошибке или остановке архив остаётся целым.
    /// </summary>
    internal static class ArchiveRecompressor
    {
        /// <summary>Пересжимает архив. true — архив изменён; false — он уже оптимален и оставлен как был.</summary>
        public static bool Recompress(string path, bool ipak, int level, Action<int, int> progress, CancellationToken token)
        {
            if (ipak)
            {
                RecompressIpak(path, level, progress, token);
                return true;
            }
            return RecompressIwd(path, level, progress, token);
        }

        private static bool RecompressIwd(string path, int level, Action<int, int> progress, CancellationToken token)
        {
            CompressionLevel zipLevel = level == 0 ? CompressionLevel.SmallestSize
                                      : level == 2 ? CompressionLevel.NoCompression
                                      : CompressionLevel.Optimal;
            bool allowGrowth = level == 2; // «Без сжатия» — осознанно больший архив
            string temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                using (ZipArchive source = ZipFile.OpenRead(path))
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var target = new ZipArchive(fs, ZipArchiveMode.Create, false))
                {
                    int total = source.Entries.Count;
                    int done = 0;
                    progress(0, total);

                    foreach (ZipArchiveEntry entry in source.Entries)
                    {
                        token.ThrowIfCancellationRequested();
                        bool isFolder = entry.FullName.EndsWith("/", StringComparison.Ordinal);

                        // Сжимаем запись только если Deflate реально её уменьшает: текстуры
                        // уже сжаты DXT, и «сжатие» делало архив больше исходного.
                        CompressionLevel entryLevel = zipLevel;
                        if (!isFolder && zipLevel != CompressionLevel.NoCompression)
                        {
                            bool shrinks;
                            using (Stream probe = entry.Open())
                                shrinks = ZipCompression.DeflateShrinks(probe, entry.Length);
                            if (!shrinks) entryLevel = CompressionLevel.NoCompression;
                        }

                        ZipArchiveEntry copy = isFolder
                            ? target.CreateEntry(entry.FullName)
                            : target.CreateEntry(entry.FullName, entryLevel);
                        try { copy.LastWriteTime = entry.LastWriteTime; } catch { }
                        if (!isFolder)
                        {
                            using Stream input = entry.Open();
                            using Stream output = copy.Open();
                            input.CopyTo(output);
                        }

                        progress(++done, total);
                    }
                }

                long oldSize = new FileInfo(path).Length;
                long newSize = new FileInfo(temp).Length;
                if (!allowGrowth && newSize >= oldSize)
                {
                    // Ничего не выиграли — оставляем оригинал нетронутым.
                    try { File.Delete(temp); } catch { }
                    return false;
                }

                File.Move(temp, path, true);
                return true;
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
        }

        private static void RecompressIpak(string path, int level, Action<int, int> progress, CancellationToken token)
        {
            var reader = IpakReader.Open(path);
            var builder = new IpakBuilder(path, level);
            try
            {
                int total = reader.Index.Count;
                int done = 0;
                progress(0, total);

                // картинки с общими данными копируем один раз
                var copied = new Dictionary<(uint, uint), (uint Offset, uint Size)>();
                foreach (IpakIndexEntry entry in reader.Index)
                {
                    token.ThrowIfCancellationRequested();
                    var key = (entry.Offset, entry.Size);
                    if (!copied.TryGetValue(key, out var moved))
                    {
                        byte[] data = reader.ReadImage(entry.Offset, entry.Size);
                        moved = builder.WriteImage(data);
                        copied[key] = moved;
                    }
                    builder.AddIndex(entry.NameHash, entry.DataHash, moved.Offset, moved.Size);
                    progress(++done, total);
                }

                reader.Dispose(); // исходный файл должен быть закрыт, прежде чем его заменит новый
                builder.Finish();
            }
            finally
            {
                reader.Dispose();
                builder.Dispose();
            }
        }
    }
}