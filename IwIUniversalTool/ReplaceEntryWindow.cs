using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace IwIUniversalTool
{
    /// <summary>Окно замены файла внутри архива: превью «сейчас / после замены» и путь к файлу.</summary>
    internal sealed class ReplaceEntryWindow : Window
    {
        private readonly bool _ru;
        private readonly string _entryName;
        private readonly string _originalPath;
        private readonly TextBox _sourceBox;

        private readonly Brush _text;
        private readonly Brush _muted;
        private readonly Brush _accent;
        private readonly Brush _border;
        private readonly Brush _input;

        private readonly Image _originalImage = new() { Stretch = Stretch.Uniform };
        private readonly Image _replaceImage = new() { Stretch = Stretch.Uniform };
        private readonly TextBlock _originalInfo;
        private readonly TextBlock _replaceInfo;
        private readonly TextBlock _originalName;
        private readonly TextBlock _replaceName;
        private CancellationTokenSource? _previewCts;

        public string ReplacementPath { get; private set; } = "";

        public ReplaceEntryWindow(Window owner, bool ru, string entryName, string originalPath, string current)
        {
            _ru = ru;
            _entryName = entryName;
            _originalPath = originalPath;

            _text = Res(owner, "TextBrush", SystemColors.ControlTextBrush);
            _muted = Res(owner, "TextMutedBrush", _text);
            _accent = Res(owner, "AccentBrush", SystemColors.HighlightBrush);
            _border = Res(owner, "BorderBrush", SystemColors.ControlDarkBrush);
            _input = Res(owner, "InputBgBrush", SystemColors.WindowBrush);

            Owner = owner;
            Title = ru ? "Замена файла в архиве" : "Replace a file in the archive";
            Width = 560;
            MinWidth = 480;
            MinHeight = 420;
            MaxHeight = Math.Max(460, SystemParameters.WorkArea.Height - 100);
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = Res(owner, "WindowBgBrush", SystemColors.WindowBrush);
            Foreground = _text;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;

            var card = new Border
            {
                Margin = new Thickness(12, 12, 12, 0),
                Padding = new Thickness(16),
                CornerRadius = new CornerRadius(10),
                Background = Res(owner, "PanelBgBrush", SystemColors.WindowBrush),
                BorderBrush = _border,
                BorderThickness = new Thickness(1)
            };

            var root = new StackPanel();
            card.Child = root;

            root.Children.Add(Label(ru ? "Файл в архиве" : "File in the archive"));
            root.Children.Add(MakeTextBox(entryName, readOnly: true, margin: new Thickness(0, 2, 0, 10)));

            root.Children.Add(Label(ru ? "Файл замены" : "Replacement file"));

            var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _sourceBox = MakeTextBox(current, readOnly: false, margin: new Thickness(0));
            _sourceBox.TextChanged += (_, _) => ScheduleReplacementPreview();
            row.Children.Add(_sourceBox);

            var browse = MakeButton(ru ? "Обзор…" : "Browse…", accent: false);
            browse.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(browse, 1);
            browse.Click += (_, _) => Browse();
            row.Children.Add(browse);
            root.Children.Add(row);

            _originalInfo = PreviewInfo();
            _replaceInfo = PreviewInfo();
            _originalName = PreviewName();
            _replaceName = PreviewName();

            var previews = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            previews.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            previews.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            previews.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            previews.Children.Add(PreviewColumn(ru ? "Сейчас в архиве" : "Currently in the archive",
                _originalImage, _originalInfo, _originalName));

            var arrow = new TextBlock
            {
                Text = "→",
                FontSize = 18,
                Foreground = _muted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0)
            };
            Grid.SetColumn(arrow, 1);
            previews.Children.Add(arrow);

            var right = PreviewColumn(ru ? "После замены" : "After replacement",
                _replaceImage, _replaceInfo, _replaceName);
            Grid.SetColumn(right, 2);
            previews.Children.Add(right);

            root.Children.Add(previews);

            root.Children.Add(new TextBlock
            {
                Text = ru
                    ? "Пустое поле — замена снимается. Имя в архиве остаётся прежним, меняется только содержимое."
                    : "An empty field removes the replacement. The name in the archive stays the same; only the content changes.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = _muted,
                Margin = new Thickness(0, 12, 0, 0)
            });

            var buttons = new Grid { Margin = new Thickness(12, 10, 12, 12) };
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cancel = MakeButton(ru ? "Отмена" : "Cancel", accent: false);
            cancel.IsCancel = true;
            Grid.SetColumn(cancel, 1);

            var ok = MakeButton("OK", accent: true);
            ok.Margin = new Thickness(8, 0, 0, 0);
            ok.IsDefault = true;
            ok.Click += (_, _) => Accept();
            Grid.SetColumn(ok, 2);

            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);

            var outer = new Grid();
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = card
            };
            outer.Children.Add(scroll);
            Grid.SetRow(buttons, 1);
            outer.Children.Add(buttons);

            Content = outer;

            Closed += (_, _) => { try { _previewCts?.Cancel(); } catch { } };
            LoadOriginalPreview();
            ScheduleReplacementPreview();
        }

        private static Brush Res(Window owner, string key, Brush fallback)
            => (owner.TryFindResource(key) as Brush) ?? fallback;

        private TextBlock Label(string text) => new()
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            Foreground = _text
        };

        private TextBlock PreviewInfo() => new()
        {
            Foreground = _muted,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        };

        private TextBlock PreviewName() => new()
        {
            Foreground = _text,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        };

        private FrameworkElement PreviewColumn(string title, Image image, TextBlock info, TextBlock name)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = _muted,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 4)
            });

            var frame = new Border
            {
                Height = 120,
                CornerRadius = new CornerRadius(8),
                Background = Res(Owner, "CardBgBrush", SystemColors.ControlLightBrush),
                BorderBrush = _border,
                BorderThickness = new Thickness(1),
                Child = image
            };
            stack.Children.Add(frame);
            stack.Children.Add(info);
            stack.Children.Add(name);
            return stack;
        }

        private TextBox MakeTextBox(string text, bool readOnly, Thickness margin) => new()
        {
            Text = text,
            IsReadOnly = readOnly,
            Margin = margin,
            MinHeight = 28,
            Padding = new Thickness(8, 4, 8, 4),
            Background = _input,
            Foreground = _text,
            BorderBrush = _border,
            CaretBrush = _text,
            VerticalContentAlignment = VerticalAlignment.Center,
            Template = RoundedTextBoxTemplate()
        };

        private ControlTemplate RoundedTextBoxTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
            host.SetValue(ScrollViewer.VerticalAlignmentProperty, VerticalAlignment.Center);
            host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            border.AppendChild(host);

            var template = new ControlTemplate(typeof(TextBox)) { VisualTree = border };
            var focus = new Trigger { Property = TextBox.IsKeyboardFocusWithinProperty, Value = true };
            focus.Setters.Add(new Setter(Control.BorderBrushProperty, _accent));
            template.Triggers.Add(focus);
            return template;
        }

        private Button MakeButton(string text, bool accent)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(14, 5, 14, 5),
                MinHeight = 30,
                Cursor = Cursors.Hand,
                FontSize = 13,
                Background = accent ? _accent : _input,
                Foreground = _text,
                BorderBrush = _border,
                Template = RoundedButtonTemplate()
            };
            return b;
        }

        private static ControlTemplate RoundedButtonTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }

        private async void LoadOriginalPreview()
        {
            if (string.IsNullOrEmpty(_originalPath)) return;
            try
            {
                var loaded = await Task.Run(() => TextureLoader.Load(_originalPath, _ru));
                _originalImage.Source = loaded.Image;
                _originalInfo.Text = $"{loaded.Info.Width}×{loaded.Info.Height} · {loaded.Info.Compression}";
                _originalName.Text = Path.GetFileName(_originalPath);
            }
            catch
            {
                _originalInfo.Text = _ru ? "нет превью" : "no preview";
                _originalName.Text = _entryName;
            }
        }

        private void ScheduleReplacementPreview()
        {
            _previewCts?.Cancel();
            string text = _sourceBox.Text.Trim();
            if (text.Length == 0 || !File.Exists(text))
            {
                _replaceImage.Source = null;
                _replaceInfo.Text = text.Length == 0
                    ? (_ru ? "замена не выбрана" : "no replacement chosen")
                    : (_ru ? "файл не найден" : "file not found");
                _replaceName.Text = Path.GetFileNameWithoutExtension(_entryName) + ".iwi";
                return;
            }

            var cts = new CancellationTokenSource();
            _previewCts = cts;
            CancellationToken token = cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(300, token);
                    var loaded = TextureLoader.Load(text, _ru);
                    token.ThrowIfCancellationRequested();
                    Dispatcher.Invoke(() =>
                    {
                        _replaceImage.Source = loaded.Image;
                        _replaceInfo.Text = $"{loaded.Info.Width}×{loaded.Info.Height} · {loaded.Info.Compression}";
                        _replaceName.Text = Path.GetFileNameWithoutExtension(_entryName) + ".iwi";
                    });
                }
                catch (OperationCanceledException) { }
                catch
                {
                    Dispatcher.Invoke(() =>
                    {
                        _replaceImage.Source = null;
                        _replaceInfo.Text = _ru ? "не удалось прочитать" : "could not read";
                    });
                }
            }, token);
        }

        private void Browse()
        {
            var dlg = new OpenFileDialog
            {
                Title = _ru ? "Файл замены" : "Replacement file",
                Filter = (_ru ? "Изображения" : "Images") + "|*.iwi;*.dds;*.png;*.tga;*.vtf;*.bmp;*.jpg;*.jpeg|*.*|*.*"
            };
            if (dlg.ShowDialog(this) == true) _sourceBox.Text = dlg.FileName;
        }

        private void Accept()
        {
            string text = _sourceBox.Text.Trim();
            if (text.Length > 0 && !File.Exists(text))
            {
                MessageBox.Show(this,
                    (_ru ? "Файл не найден: " : "File not found: ") + text,
                    _ru ? "Проверьте путь" : "Check the path",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ReplacementPath = text;
            DialogResult = true;
        }
    }
}
