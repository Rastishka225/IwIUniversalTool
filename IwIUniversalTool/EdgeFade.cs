using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IwIUniversalTool
{
    /// <summary>
    /// Мягкий переход фона на краях прокручиваемой области: содержимое плавно уходит под вкладки и под нижнюю кнопку,
    /// а не обрезается жёсткой линией. Накладывается поверх ScrollViewer в той же ячейке Grid и не мешает мыши.
    /// Градиент идёт по плавной кривой (без «ступенек»), у самого края фон сплошной, а сам слой на 2 пикселя заходит за границу
    /// области — так между ним и соседними элементами не остаётся тонкой щели, через которую виден контент.
    /// </summary>
    internal static class EdgeFade
    {
        private const double Overlap = 2;

        /// <summary>Затухание у верхнего края.</summary>
        public static Border Top(Color background, double height = 28, double scrollBarInset = 12) => Create(background, height, scrollBarInset, true);

        /// <summary>Затухание у нижнего края.</summary>
        public static Border Bottom(Color background, double height = 28, double scrollBarInset = 12) => Create(background, height, scrollBarInset, false);

        private static Border Create(Color background, double height, double scrollBarInset, bool top)
        {
            // первые 20% — сплошной фон, дальше плавное затухание (smoothstep)
            var stops = new GradientStopCollection();
            const int steps = 10;
            for (int i = 0; i <= steps; i++)
            {
                double t = i / (double)steps;                 // 0 — край области, 1 — внутренний конец градиента
                double k = Math.Clamp((t - 0.2) / 0.8, 0, 1); // 0 на «полке», затем до 1
                double alpha = 1 - k * k * (3 - 2 * k);       // smoothstep
                byte a = (byte)Math.Round(alpha * 255);
                stops.Add(new GradientStop(Color.FromArgb(a, background.R, background.G, background.B), top ? t : 1 - t));
            }

            var brush = new LinearGradientBrush(stops, new Point(0, 0), new Point(0, 1));
            brush.Freeze();

            return new Border
            {
                Height = height + Overlap,
                VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                // заходим на 2 пикселя за край области; полоса прокрутки остаётся видимой
                Margin = new Thickness(0, top ? -Overlap : 0, scrollBarInset, top ? 0 : -Overlap),
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
                Background = brush
            };
        }
    }
}