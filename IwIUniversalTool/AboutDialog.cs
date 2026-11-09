using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace IwIUniversalTool
{
    /// <summary>Окно «О программе»: справка по форматам и авторы. Оформление совпадает с окном настроек.</summary>
    public sealed class AboutDialog : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private readonly Brush _panel, _card, _text, _muted, _border;
        private readonly bool _isRu;

        public AboutDialog(Window owner, bool isDark, bool isRu)
        {
            _isRu = isRu;

            _panel = new SolidColorBrush(isDark ? Color.FromRgb(30, 30, 32) : Color.FromRgb(240, 241, 244));
            _card = new SolidColorBrush(isDark ? Color.FromRgb(40, 40, 43) : Colors.White);
            _text = new SolidColorBrush(isDark ? Color.FromRgb(245, 245, 247) : Color.FromRgb(28, 30, 33));
            _muted = new SolidColorBrush(isDark ? Color.FromRgb(150, 150, 158) : Color.FromRgb(105, 110, 122));
            _border = new SolidColorBrush(isDark ? Color.FromRgb(62, 62, 66) : Color.FromRgb(218, 221, 228));

            Owner = owner;
            Title = isRu ? "О программе" : "About";
            Background = _panel;
            Width = 660;
            Height = Math.Min(640, SystemParameters.WorkArea.Height - 60);
            MinWidth = 560;
            MinHeight = 420;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            UseLayoutRounding = true;

            SourceInitialized += (_, _) =>
            {
                try
                {
                    int dark = isDark ? 1 : 0;
                    DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
                }
                catch { }
            };

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    Close();
                }
            };

            // ---- вкладки
            var tabFormats = BuildFormatsTab();
            var tabAbout = BuildAboutTab();
            tabAbout.Visibility = Visibility.Collapsed;

            var body = new StackPanel { Margin = new Thickness(20, 30, 20, 30) };
            body.Children.Add(tabFormats);
            body.Children.Add(tabAbout);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };

            var accent = new SolidColorBrush(Color.FromRgb(0, 122, 204));
            var panels = new[] { tabFormats, tabAbout };
            var titles = new[]
            {
                isRu ? "Справка по форматам" : "Format guide",
                isRu ? "О программе и авторы" : "About and credits"
            };
            var buttons = new Border[2];

            void Select(int index)
            {
                for (int i = 0; i < 2; i++)
                {
                    bool active = i == index;
                    panels[i].Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                    buttons[i].Background = active ? accent : Brushes.Transparent;
                    var label = (TextBlock)buttons[i].Child;
                    label.Foreground = active ? Brushes.White : _text;
                    label.Opacity = active ? 1.0 : 0.75;
                }
                scroll.ScrollToTop();
            }

            var tabRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 14, 20, 6) };
            for (int i = 0; i < 2; i++)
            {
                int index = i;
                var tab = new Border
                {
                    Child = new TextBlock { Text = titles[i], FontWeight = FontWeights.SemiBold, FontSize = 13 },
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 6, 14, 6),
                    Margin = new Thickness(0, 0, 6, 0),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Transparent
                };
                tab.MouseLeftButtonUp += (_, _) => Select(index);
                buttons[i] = tab;
                tabRow.Children.Add(tab);
            }
            Select(0);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(scroll, 1);
            root.Children.Add(tabRow);
            root.Children.Add(scroll);

            // мягкий переход фона под вкладками и у нижнего края
            var fadeTop = EdgeFade.Top(((SolidColorBrush)_panel).Color, 28);
            var fadeBottom = EdgeFade.Bottom(((SolidColorBrush)_panel).Color, 28);
            Grid.SetRow(fadeTop, 1);
            Grid.SetRow(fadeBottom, 1);
            root.Children.Add(fadeTop);
            root.Children.Add(fadeBottom);
            Content = root;
        }

        // ------------------------------------------------------------------ вкладка 1: форматы

        private StackPanel BuildFormatsTab()
        {
            bool ru = _isRu;
            var panel = new StackPanel();

            panel.Children.Add(FormatCard("PNG", Color.FromRgb(46, 160, 67),
                ru ? "Идеален для хранения исходников и рисования. Максимальное качество со сжатием без потерь (Deflate)."
                   : "Ideal for keeping sources and for painting. Maximum quality with lossless compression (Deflate)."));

            panel.Children.Add(FormatCard("TGA", Color.FromRgb(224, 128, 36),
                ru ? "Классический исходник для мод-тулзов. CoD Asset Manager требует именно TGA 24/32 бита."
                   : "The classic source format for mod tools. CoD Asset Manager needs exactly 24/32-bit TGA."));

            panel.Children.Add(FormatCard("DDS", Color.FromRgb(0, 122, 204),
                ru ? "Конечный игровой формат для видеокарт: быстрая загрузка в видеопамять, мипмапы, сжатие DXT/BC."
                   : "The final in-game format for graphics cards: fast loading into video memory, mipmaps, DXT/BC compression."));

            panel.Children.Add(FormatCard("IWI", Color.FromRgb(156, 74, 214),
                ru ? "Родной закрытый контейнер серии Call of Duty (от CoD2 до BO2)."
                   : "The native closed container of the Call of Duty series (from CoD2 to BO2)."));

            panel.Children.Add(FormatCard("VTF", Color.FromRgb(214, 69, 65),
                ru ? "Текстурный формат движка Valve Source (CS:GO, TF2, Half-Life 2, GMod)."
                   : "The texture format of the Valve Source engine (CS:GO, TF2, Half-Life 2, GMod)."));

            panel.Children.Add(FormatCard("ZIP · 7Z · RAR · IWD", Color.FromRgb(46, 125, 50),
                ru ? "Архивы с текстурами. Программа заглядывает внутрь, показывает файлы в списке и конвертирует их, сам архив не меняется. IWD у Call of Duty — это обычный ZIP. Результат кладётся в подпапку с именем архива."
                   : "Archives with textures. The program looks inside, lists the files and converts them; the archive itself is never changed. A Call of Duty IWD is a plain ZIP. The result goes to a subfolder named after the archive."));

            panel.Children.Add(FormatCard("IPAK", Color.FromRgb(124, 77, 255),
                ru ? "Архив текстур Black Ops 2, заменил IWD. Файл состоит из чанков по 32 КБ; внутри лежат IWI версии 27, сжатые LZO. Игра ищет картинку не по имени, а по паре «хеш имени + CRC данных». Поэтому при замене текстуры в уже существующем IPAK новые данные кладутся под прежние хеши — так заменяется и оригинальная текстура. Новые картинки получают CRC своих данных. Остальное содержимое существующего IPAK сохраняется. Собирается в режиме «Архивы». Стрелка в поле имени показывает архивы из папки выбранной версии игры (папки задаются в настройках или находятся сами) — выберите архив, и упаковка пойдёт прямо в него. Кнопка с иконкой архива и стрелкой справа от поля добавляет текстуры этого архива в основной список файлов (в IPAK имён нет, поэтому они называются по хешу, например 0xA1B2C3D4.iwi)."
                   : "The Black Ops 2 texture archive that replaced IWD. The file is made of 32 KB chunks holding version 27 IWI files compressed with LZO. The game finds an image not by its name but by a pair: the hash of the name plus the CRC of the data. So when a texture is replaced in an existing IPAK, the new data is stored under the old hashes — that is how the original texture gets replaced too. New images get the CRC of their own data. Everything else in an existing IPAK is kept. Built in the \"Archives\" mode. The arrow in the name field lists the archives from the folder of the selected game version (folders are set in the settings or found automatically) — pick one and packing goes straight into it. The button with the archive icon and an arrow next to the field adds the textures of that archive to the main file list (IPAK stores no names, so they are named by hash, for example 0xA1B2C3D4.iwi)."));

            panel.Children.Add(FormatCard("IWD", Color.FromRgb(0, 150, 136),
                ru ? "Архив старых Call of Duty — обычный ZIP. В режиме «Архивы» текстуры кладутся в папку images/ внутри IWD (без неё игра их не увидит): PNG, TGA, DDS и VTF сначала конвертируются в IWI нужной версии, готовые IWI добавляются как есть. Если архив уже есть, файлы с тем же именем заменяются (или пропускаются, если галочка выключена). При замене прежний файл можно сохранить в папку «имя_архива_originals» рядом с архивом как имя_Original.iwi. Сжатие — Deflate. Если имя архива не указано, будет iw_collect.iwd (для IPAK — ipak_collect.ipak)."
                   : "The archive of the older Call of Duty games — a plain ZIP. In the \"Archives\" mode the textures go into the images/ folder inside the IWD (without it the game will not see them): PNG, TGA, DDS and VTF are converted to IWI of the right version first, ready IWI files are added as they are. If the archive already exists, files with the same name are replaced (or skipped when the checkbox is off). When replacing, the previous file can be saved into an \"archive_name_originals\" folder next to the archive as name_Original.iwi. Compression is Deflate. If no archive name is given it will be iw_collect.iwd (ipak_collect.ipak for IPAK)."));

            return panel;
        }

        private Border FormatCard(string name, Color badgeColor, string description)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(badgeColor),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 4, 12, 4),
                MinWidth = 54,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = name,
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };

            var text = new TextBlock
            {
                Text = description,
                Foreground = _text,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(text, 1);
            grid.Children.Add(badge);
            grid.Children.Add(text);

            return new Border
            {
                Background = _card,
                BorderBrush = _border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 10),
                Child = grid
            };
        }

        // ------------------------------------------------------------------ вкладка 2: о программе

        private StackPanel BuildAboutTab()
        {
            bool ru = _isRu;
            var panel = new StackPanel();

            string version = typeof(AboutDialog).Assembly.GetName().Version?.ToString(2) ?? "1.3";

            var header = new StackPanel { Margin = new Thickness(2, 0, 0, 14) };
            header.Children.Add(new TextBlock { Text = "IwI Universal Tool", Foreground = _text, FontSize = 22, FontWeight = FontWeights.Bold });
            header.Children.Add(new TextBlock
            {
                Text = (ru ? "Версия " : "Version ") + version + (ru ? " · конвертер текстур CoD, Source и обычных картинок" : " · texture converter for CoD, Source and regular images"),
                Foreground = _muted,
                FontSize = 12.5,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(header);

            panel.Children.Add(AuthorCard("Rastishka225",
                ru ? "Автор идеи, руководство разработкой, тестирование, поиск багов и развитие проекта."
                   : "Author of the idea, project lead, testing, bug hunting and development of the project."));

            panel.Children.Add(AuthorCard("Kokole",
                ru ? "Автор оригинального базового проекта iwi_dds_fast_converter."
                   : "Author of the original base project iwi_dds_fast_converter."));

            panel.Children.Add(AuthorCard("Gemini",
                ru ? "Реверс-инжиниринг математики вейвлетов CoD4 (2D Inverse Haar), декомпиляция iw3mp.exe, извлечение таблиц Хаффмана, системная архитектура."
                   : "Reverse engineering of the CoD4 wavelet math (2D Inverse Haar), decompiling iw3mp.exe, extracting the Huffman tables, system architecture."));

            panel.Children.Add(AuthorCard("Claude",
                ru ? "Проектирование пользовательского интерфейса WPF, адаптивный дизайн и стилизация компонентов."
                   : "WPF user interface design, adaptive layout and styling of the components."));

            return panel;
        }

        private Border AuthorCard(string name, string role)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = name, Foreground = _text, FontSize = 15, FontWeight = FontWeights.SemiBold });
            stack.Children.Add(new TextBlock
            {
                Text = role,
                Foreground = _muted,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });

            return new Border
            {
                Background = _card,
                BorderBrush = _border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 10),
                Child = stack
            };
        }
    }
}
