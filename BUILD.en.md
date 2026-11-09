# Building the release `.exe`

[English](BUILD.en.md) | [Русский](BUILD.md)

You need the **.NET 10 SDK** or Visual Studio 2026 with the .NET Desktop workload on Windows x64.

## Visual Studio

1. Open `IwIUniversalTool.slnx`.
2. Select the **Release** configuration.
3. Right-click the project → **Publish** → profile `FolderProfile` → **Publish**.
4. Output: `IwIUniversalTool\bin\Release\net10.0-windows\publish\win-x64\IwIUniversalTool.exe`.

## Command line

```powershell
cd IwIUniversalTool
dotnet publish -c Release -r win-x64
```

Output:

```text
IwIUniversalTool\bin\Release\net10.0-windows\win-x64\publish\IwIUniversalTool.exe
```

## What you get

A single self-contained `.exe`: no .NET installation is required on the target PC. The file is large (tens to hundreds of MB) because it contains .NET and the native ImageMagick libraries.

**Explorer thumbnails** still need the **.NET 10 Desktop Runtime** — the `IwiThumbs` component is loaded by Explorer separately. The app offers the download link when you test thumbnails.

## Settings

`settings.json` is created **next to the `.exe`**. When enabled, the thumbnail component is extracted to:

```text
%LOCALAPPDATA%\IwIUniversalTool\Thumbnails
```

## CoD4 wavelet textures

An external `wavelet_tables.bin` is no longer needed: the tables are generated in code from a compact description (`WaveletCodebook.cs`). No extra file next to the `.exe` is required.
