@echo off
chcp 65001 >nul
rem Собирает компонент миниатюр для проводника в папку Thumbnails (рядом с этим файлом).
rem Этот файл должен лежать в папке проекта: рядом с IwIUniversalTool.csproj и папкой IwiThumbs.
cd /d "%~dp0"
echo Папка: %CD%
echo.

if not exist "IwiCodec.cs" goto no_project
if not exist "IwiThumbs\IwiThumbs.csproj" goto no_folder

dotnet build "IwiThumbs\IwiThumbs.csproj" -c Release -o "Thumbnails"
if errorlevel 1 goto build_failed

echo.
echo Готово. Папка Thumbnails создана рядом с проектом.
echo Теперь в программе: Настройки - Основное - "Миниатюры в проводнике" - Включить.
pause
exit /b 0

:no_project
echo Не найден файл IwiCodec.cs.
echo Положите BuildThumbnails.cmd в папку проекта, где лежит IwIUniversalTool.csproj.
pause
exit /b 1

:no_folder
echo Не найдена папка IwiThumbs с файлом IwiThumbs.csproj.
echo Создайте папку IwiThumbs рядом с этим файлом и положите в неё три файла:
echo IwiThumbs.csproj, TextureBitmap.cs и IwiThumbnailProvider.cs.
pause
exit /b 1

:build_failed
echo.
echo Не удалось собрать компонент миниатюр. Текст ошибки выше.
pause
exit /b 1
