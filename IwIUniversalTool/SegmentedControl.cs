using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IwIUniversalTool
{
    /// <summary>
    /// Переключатель-«таблетка»: несколько вариантов в одном скруглённом блоке, выбранный подсвечен синим.
    /// Используется в окне настроек вместо длинных списков радиокнопок.
    /// </summary>
    public sealed class SegmentedControl : Border
    {
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0, 122, 204));
        private static readonly Brush Hover = new SolidColorBrush(Color.FromArgb(0x30, 128, 128, 128));

        private readonly List<Border> _pills = new();
        private readonly Brush _text;
        private int _selected;

        public event Action<int>? SelectionChanged;

        public SegmentedControl(IReadOnlyList<string> labels, int selected, Brush textBrush, Brush trackBrush, IReadOnlyList<string>? tooltips = null)
        {
            _text = textBrush;

            Background = trackBrush;
            CornerRadius = new CornerRadius(9);
            Padding = new Thickness(3);
            HorizontalAlignment = HorizontalAlignment.Left;

            var panel = new WrapPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < labels.Count; i++)
            {
                int index = i;
                var pill = new Border
                {
                    Child = new TextBlock
                    {
                        Text = labels[i],
                        FontSize = 12.5,
                        FontWeight = FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(12, 5, 12, 5),
                    Margin = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Transparent
                };

                if (tooltips != null && i < tooltips.Count && !string.IsNullOrEmpty(tooltips[i]))
                    pill.ToolTip = tooltips[i];

                pill.MouseEnter += (_, _) => { if (index != _selected) pill.Background = Hover; };
                pill.MouseLeave += (_, _) => { if (index != _selected) pill.Background = Brushes.Transparent; };
                pill.MouseLeftButtonUp += (_, _) =>
                {
                    SelectedIndex = index;
                    SelectionChanged?.Invoke(index);
                };

                _pills.Add(pill);
                panel.Children.Add(pill);
            }

            Child = panel;
            SelectedIndex = selected;
        }

        public int SelectedIndex
        {
            get => _selected;
            set
            {
                _selected = Math.Clamp(value, 0, Math.Max(0, _pills.Count - 1));
                Refresh();
            }
        }

        private void Refresh()
        {
            for (int i = 0; i < _pills.Count; i++)
            {
                bool active = i == _selected;
                _pills[i].Background = active ? Accent : Brushes.Transparent;
                var label = (TextBlock)_pills[i].Child;
                label.Foreground = active ? Brushes.White : _text;
                label.Opacity = active ? 1.0 : 0.85;
            }
        }
    }
}
