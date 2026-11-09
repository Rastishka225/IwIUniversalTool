using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IwIUniversalTool;

internal static class Program
{
    private static readonly (string Name, int Index, string Version)[] Games =
    {
        ("cod2", 6, "v5"),
        ("cod4", 5, "v6"),
        ("mw1",  5, "v6"),
        ("waw",  4, "v6"),
        ("mw2",  3, "v8"),
        ("mw3",  1, "v8"),
        ("bo1",  2, "v13"),
        ("bo2",  0, "v27")
    };

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        string command = args[0].ToLowerInvariant();
        if (command == "info")
        {
            if (args.Length < 2) throw new ArgumentException("usage: iwi-tool info <file>");
            return Info(args[1]);
        }

        if (command == "convert")
        {
            return Convert(args.Skip(1).ToArray());
        }

        throw new ArgumentException($"unknown command '{args[0]}'. Try --help.");
    }

    private static int Info(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        Console.WriteLine($"{Path.GetFileName(path)}: {data.Length:N0} bytes");
        Console.WriteLine(IwiCodec.DescribeHead(data, data.Length, ext, false));
        return 0;
    }

    private static int Convert(string[] args)
    {
        var positional = new List<string>();
        string? output = null;
        int game = -1;
        bool mips = true;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--game":
                    if (++i >= args.Length) throw new ArgumentException("--game needs a value (cod2, cod4, waw, mw2, mw3, bo1, bo2)");
                    game = ParseGame(args[i]);
                    break;
                case "-o":
                case "--output":
                    if (++i >= args.Length) throw new ArgumentException("--output needs a path");
                    output = args[i];
                    break;
                case "--no-mips":
                    mips = false;
                    break;
                default:
                    if (a.StartsWith('-')) throw new ArgumentException($"unknown option '{a}'");
                    positional.Add(a);
                    break;
            }
        }

        if (positional.Count == 0) throw new ArgumentException("usage: iwi-tool convert <input> [-o output] [--game <name>] [--no-mips]");

        string input = positional[0];
        string inExt = Path.GetExtension(input).ToLowerInvariant();
        byte[] data = File.ReadAllBytes(input);

        switch (inExt)
        {
            case ".iwi":
            {
                byte[] dds = IwiCodec.IwiToDds(data);
                string outPath = output ?? Path.ChangeExtension(input, ".dds");
                File.WriteAllBytes(outPath, dds);
                Console.WriteLine($"{input} -> {outPath} ({dds.Length:N0} bytes, DDS)");
                return 0;
            }

            case ".vtf":
            {
                byte[] dds = VtfCodec.VtfToDds(data);
                string outPath = output ?? Path.ChangeExtension(input, ".dds");
                File.WriteAllBytes(outPath, dds);
                Console.WriteLine($"{input} -> {outPath} ({dds.Length:N0} bytes, DDS)");
                return 0;
            }

            case ".dds":
            {
                if (game < 0)
                    throw new ArgumentException("DDS -> IWI needs --game (cod2, cod4, waw, mw2, mw3, bo1, bo2)");

                var result = IwiCodec.TryDdsToIwi(data, game, mips, true, out byte[] iwi);
                if (result != IwiBuildResult.Ok)
                    throw new InvalidDataException($"conversion failed: {result}. Supported DDS formats: DXT1/DXT3/DXT5, BC5 (ATI2), uncompressed BGRA/BGR.");

                string outPath = output ?? Path.ChangeExtension(input, ".iwi");
                File.WriteAllBytes(outPath, iwi);
                Console.WriteLine($"{input} -> {outPath} ({iwi.Length:N0} bytes, IWI {GameVersion(game)}, game {GameName(game)})");
                return 0;
            }

            default:
                throw new ArgumentException("supported input: .iwi, .dds, .vtf");
        }
    }

    private static int ParseGame(string name)
    {
        string key = name.Trim().ToLowerInvariant();
        foreach (var g in Games)
        {
            if (g.Name == key) return g.Index;
        }
        throw new ArgumentException($"unknown game '{name}'. Use: {string.Join(", ", Games.Select(g => g.Name).Distinct())}");
    }

    private static string GameName(int index)
        => Games.FirstOrDefault(g => g.Index == index).Name ?? index.ToString();

    private static string GameVersion(int index)
        => Games.FirstOrDefault(g => g.Index == index).Version ?? "?";

    private static void PrintHelp()
    {
        Console.WriteLine("""
            iwi-tool — cross-platform IWI/DDS/VTF converter (no GUI)

            Usage:
              iwi-tool info <file>
                  Show container/format/size/mipmap information.

              iwi-tool convert <input> [-o output] [--game <name>] [--no-mips]
                  .iwi -> .dds
                  .vtf -> .dds
                  .dds -> .iwi   (--game is required)

            Games:
              cod2  -> IWI v5
              cod4  -> IWI v6   (alias: mw1)
              waw   -> IWI v6
              mw2   -> IWI v8
              mw3   -> IWI v8
              bo1   -> IWI v13
              bo2   -> IWI v27

            Examples:
              iwi-tool info image.iwi
              iwi-tool convert image.iwi
              iwi-tool convert image.dds --game bo2
              iwi-tool convert image.dds --game mw3 -o out.iwi
            """);
    }
}
