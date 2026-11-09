# Как собрать релизный `.exe`

[English](BUILD.en.md) | [Русский](BUILD.md)

Нужен **.NET 10 SDK** или Visual Studio 2026 с рабочей нагрузкой .NET Desktop на Windows x64.

## Через Visual Studio

1. Открой `IwIUniversalTool.slnx`.
2. Выбери конфигурацию **Release**.
3. Правой кнопкой по проекту → **Опубликовать** → профиль `FolderProfile` → **Опубликовать**.
4. Готовый файл: `IwIUniversalTool\bin\Release\net10.0-windows\publish\win-x64\IwIUniversalTool.exe`.

## Через командную строку

```powershell
cd IwIUniversalTool
dotnet publish -c Release -r win-x64
```

Готовый файл:

```text
IwIUniversalTool\bin\Release\net10.0-windows\win-x64\publish\IwIUniversalTool.exe
```

## Что получается

Один самодостаточный `.exe`: .NET на компьютере пользователя ставить не нужно. Файл большой (десятки–сотни МБ), потому что внутри .NET и нативные библиотеки ImageMagick.

Для **миниатюр в Проводнике** всё же нужен **.NET 10 Desktop Runtime** — компонент `IwiThumbs` загружается Проводником отдельно. Программа сама предложит скачать его при проверке миниатюр.

## Настройки

`settings.json` создаётся **рядом с `.exe`**. Компонент миниатюр при включении распаковывается в:

```text
%LOCALAPPDATA%\IwIUniversalTool\Thumbnails
```

## Wavelet-текстуры CoD4

Внешний `wavelet_tables.bin` больше не нужен: таблицы собираются в коде из компактного описания (`WaveletCodebook.cs`). Отдельный файл рядом с `.exe` не требуется.
