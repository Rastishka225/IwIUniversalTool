# Linux port plan (Avalonia)

The Windows app is WPF. WPF does not run on Linux, so a full port means a **second UI application** on Avalonia, reusing the texture/archive core.

## What ports directly

- `IwiCodec`, `VtfCodec`, `WaveletCodebook`
- `TextureBitmap` (DDS/IWI/VTF → BGRA)
- `Lzo`, `IpakReader`/`IpakBuilder`, IWD ZIP code (SharpCompress/System.IO.Compression)
- conversion rules and game/version mapping

## What cannot be ported 1:1

- Explorer thumbnails (`IwiThumbs`, HKLM registration) — Windows shell only
- DWM window effects, Win32 dialogs, registry
- Windows-specific DPI/tray/single-instance behaviour

Linux replacements:

- file manager thumbnails — separate Nautilus/KDE plugin (later, optional)
- settings — JSON file in `~/.config/IwIUniversalTool`
- theme — Avalonia Fluent theme
- file dialogs — Avalonia StorageProvider

## Architecture

```
IwIUniversalTool.Core/        # net10.0, no UI, no Windows APIs
  IwiCodec.cs
  VtfCodec.cs
  WaveletCodebook.cs
  TextureBitmap.cs
  Lzo.cs / PackArchives.cs
  ConversionService.cs

IwIUniversalTool.Avalonia/    # net10.0, Avalonia UI
  App.axaml
  MainWindow.axaml
  ViewModels/
  Services/ (file dialogs, settings, preview)
```

The existing WPF project keeps working; it can reference `Core` later or keep linked files.

## Milestones

### M1 — usable converter

- Avalonia window with file list, drag & drop, preview
- IWI → DDS
- VTF → DDS
- DDS → IWI (`--game` equivalent)
- format/size/mipmap info
- dark/light theme

### M2 — archives

- open IWD/IPAK, list entries
- replace entries
- pack IWD/IPAK
- keep originals
- compression level without archive growth

### M3 — parity

- settings, hotkeys, file filters
- batch conversion, progress, cancel
- recent folders, column layout
- error report/log

### M4 — Linux extras

- `.desktop` file / icon
- AppImage or self-contained tarball
- file manager thumbnail integration (optional, separate)

## Image decoding

Preview needs cross-platform decoding:

- DDS/IWI/VTF → BGRA via `TextureBitmap`
- PNG/TGA/BMP via `Magick.NET-Q8-AnyCPU` (Linux native) or ImageSharp
- encode DXT via existing `DdsEncoders` where possible

## Build (target)

```bash
dotnet publish IwIUniversalTool.Avalonia -c Release -r linux-x64 --self-contained true
```

## Status

- [x] cross-platform CLI converter (`IwIUniversalTool.Cli`)
- [~] M1 Avalonia shell: file list + preview + IWI/VTF/DDS conversion (no drag & drop yet)
- [ ] M2 archives
- [ ] M3 parity
- [ ] M4 Linux packaging
