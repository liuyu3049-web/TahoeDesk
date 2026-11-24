using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GlassShell.Core.Config;
using GlassShell.Core.Discovery;
using GlassShell.Core.Search;
using GlassShell.Core.Shell;
using GlassShell.UI.Glass;

namespace GlassShell.UI.Spotlight;

public partial class SpotlightWindow : Window
{
    private readonly SpotlightSearchEngine _engine = new();
    private readonly IconExtractor _iconExtractor = new();
    private readonly Dictionary<string, BitmapSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<InstalledAppEntry> _apps = [];
    private IReadOnlyList<IndexedFileEntry> _files = [];
    private IReadOnlyList<SpotlightResult> _results = [];
    private readonly List<Border> _rowVisuals = [];
    private int _selectedIndex;
    private bool _isDarkMode = true;

    public SpotlightWindow(DxgiBackdropSampler sampler, GlassSettings settings, bool isDarkMode)
    {
        InitializeComponent();
        GlassBackdrop.AttachSampler(sampler);
        ApplySettings(settings, isDarkMode);

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowInteropHelperEx.ExcludeFromCapture(hwnd);
        };
    }

    public event Action<string>? SystemCommandRequested;

    public void UpdateIndex(IReadOnlyList<InstalledAppEntry> apps, IReadOnlyList<IndexedFileEntry> files)
    {
        _apps = apps;
        _files = files;
    }

    public void ApplySettings(GlassSettings settings, bool isDarkMode)
    {
        _isDarkMode = isDarkMode;
        GlassBackdrop.OpacityPreset = settings.OpacityPreset;
        GlassBackdrop.IsDarkMode = isDarkMode;

        Resources["SpotFgBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0xF6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xF0, 0x12, 0x13, 0x18));
        Resources["SpotSubtleBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x94, 0x12, 0x13, 0x18));
        Resources["SpotDividerBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x20, 0x00, 0x00, 0x00));
        Resources["SpotBadgeBgBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        Resources["SpotSelectedBrush"] = new SolidColorBrush(Color.FromArgb(0xE0, 0x0A, 0x84, 0xFF));
    }

    public void ToggleOnMonitor(MonitorContext monitor)
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Left = (monitor.Bounds.Left / monitor.DpiScale) + (monitor.WidthDip - Width) * 0.5;
        Top = (monitor.Bounds.Top / monitor.DpiScale) + Math.Max(72, monitor.HeightDip * 0.20);

        QueryBox.Text = string.Empty;
        PlaceholderText.Visibility = Visibility.Visible;
        ResultsPane.Visibility = Visibility.Collapsed;
        _results = [];
        _selectedIndex = 0;

        Show();
        Activate();
        QueryBox.Focus();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (IsVisible) Hide();
    }

    private void OnQueryTextChanged(object sender, TextChangedEventArgs e)
    {
        string q = QueryBox.Text;
        PlaceholderText.Visibility = string.IsNullOrEmpty(q) ? Visibility.Visible : Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(q))
        {
            ResultsPane.Visibility = Visibility.Collapsed;
            _results = [];
            _rowVisuals.Clear();
            return;
        }

        _results = _engine.Search(q, _apps, _files, maxResults: 12);
        _selectedIndex = 0;
        RenderResults();
    }

    private void RenderResults()
    {
        ResultsListPanel.Children.Clear();
        _rowVisuals.Clear();

        if (_results.Count == 0)
        {
            ResultsPane.Visibility = Visibility.Collapsed;
            return;
        }

        ResultsPane.Visibility = Visibility.Visible;

        for (int i = 0; i < _results.Count; i++)
        {
            int idx = i;
            var item = _results[i];
            var row = BuildResultRow(item, idx);
            _rowVisuals.Add(row);
            ResultsListPanel.Children.Add(row);
        }

        UpdateSelectionVisuals();
    }

    private Border BuildResultRow(SpotlightResult item, int index)
    {
        var dock = new DockPanel { LastChildFill = true };
        var icon = BuildResultIcon(item, size: 22);
        DockPanel.SetDock(icon, System.Windows.Controls.Dock.Left);
        dock.Children.Add(icon);

        var kindTag = new TextBlock
        {
            Text = KindLabel(item.Kind),
            FontSize = 10.5,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            Foreground = Resources["SpotSubtleBrush"] as Brush ?? Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 2, 0)
        };
        DockPanel.SetDock(kindTag, System.Windows.Controls.Dock.Right);
        dock.Children.Add(kindTag);

        var title = new TextBlock
        {
            Text = item.Title,
            FontSize = 13.5,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            Foreground = Resources["SpotFgBrush"] as Brush ?? Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        dock.Children.Add(title);

        var row = new Border
        {
            Height = 36,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 1, 0, 1),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Tag = (title, kindTag),
            Child = dock
        };

        row.MouseEnter += (_, _) =>
        {
            _selectedIndex = index;
            UpdateSelectionVisuals();
        };
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _selectedIndex = index;
            ExecuteSelected(revealInExplorer: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        };

        return row;
    }

    private void UpdateSelectionVisuals()
    {
        var selBrush = Resources["SpotSelectedBrush"] as Brush ?? Brushes.DodgerBlue;
        var fgBrush = Resources["SpotFgBrush"] as Brush ?? Brushes.White;
        var subBrush = Resources["SpotSubtleBrush"] as Brush ?? Brushes.Gray;

        for (int i = 0; i < _rowVisuals.Count; i++)
        {
            bool selected = i == _selectedIndex;
            var row = _rowVisuals[i];
            row.Background = selected ? selBrush : Brushes.Transparent;
            if (row.Tag is ValueTuple<TextBlock, TextBlock> texts)
            {
                texts.Item1.Foreground = selected ? Brushes.White : fgBrush;
                texts.Item2.Foreground = selected ? new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)) : subBrush;
            }
        }

        if (_selectedIndex >= 0 && _selectedIndex < _results.Count)
        {
            var cur = _results[_selectedIndex];
            PreviewIconHost.Content = BuildResultIcon(cur, size: cur.Kind == SpotlightResultKind.Calculator ? 48 : 64);
            PreviewTitle.FontSize = cur.Kind == SpotlightResultKind.Calculator ? 34 : 20;
            PreviewTitle.Text = cur.Title;
            PreviewSubtitle.Text = cur.Subtitle;
            PreviewHint.Text = cur.Kind switch
            {
                SpotlightResultKind.Calculator => "Enter 复制计算结果到剪贴板",
                SpotlightResultKind.SystemCommand => "Enter 立即执行系统命令",
                SpotlightResultKind.WebSearch => "Enter 使用默认浏览器搜索",
                _ => "Enter 打开 · Ctrl+Enter 在资源管理器中显示"
            };
        }
    }

    private UIElement BuildResultIcon(SpotlightResult item, double size)
    {
        if (item.Kind == SpotlightResultKind.WebSearch)
        {
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size * 0.24),
                Background = new LinearGradientBrush(Color.FromRgb(0x32, 0xAD, 0xE6), Color.FromRgb(0x0A, 0x84, 0xFF), 90),
                Child = new TextBlock
                {
                    Text = "⌕",
                    FontSize = size * 0.56,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        if (item.Kind == SpotlightResultKind.Calculator)
        {
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size * 0.24),
                Background = new LinearGradientBrush(Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0xFF, 0x6B, 0x00), 90),
                Child = new TextBlock
                {
                    Text = "=",
                    FontSize = size * 0.56,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        if (item.Kind == SpotlightResultKind.SystemCommand)
        {
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size * 0.24),
                Background = new LinearGradientBrush(Color.FromRgb(0x5E, 0x5C, 0xE6), Color.FromRgb(0x0A, 0x84, 0xFF), 90),
                Child = new TextBlock
                {
                    Text = "⌘",
                    FontSize = size * 0.50,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        string? path = item.IconPath ?? item.Payload;
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!_iconCache.TryGetValue(path, out var bmp))
            {
                var raw = _iconExtractor.ExtractHighResIcon(path, 96);
                if (raw is not null)
                {
                    bmp = BitmapSource.Create(raw.Width, raw.Height, 96, 96, PixelFormats.Bgra32, null, raw.BgraPixels, raw.Width * 4);
                    bmp.Freeze();
                }
                _iconCache[path] = bmp;
            }

            if (bmp is not null)
            {
                var img = new Image
                {
                    Source = bmp,
                    Width = size,
                    Height = size,
                    Stretch = Stretch.Uniform
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                return img;
            }
        }

        return new Ellipse
        {
            Width = size * 0.85,
            Height = size * 0.85,
            Fill = new SolidColorBrush(_isDarkMode ? Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x44, 0x00, 0x00, 0x00))
        };
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        if (_results.Count == 0) return;

        if (e.Key == Key.Down)
        {
            _selectedIndex = (_selectedIndex + 1) % _results.Count;
            UpdateSelectionVisuals();
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            _selectedIndex = (_selectedIndex - 1 + _results.Count) % _results.Count;
            UpdateSelectionVisuals();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Return)
        {
            ExecuteSelected(revealInExplorer: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
            e.Handled = true;
        }
    }

    private void ExecuteSelected(bool revealInExplorer)
    {
        if (_selectedIndex < 0 || _selectedIndex >= _results.Count) return;
        var item = _results[_selectedIndex];
        Hide();

        switch (item.Kind)
        {
            case SpotlightResultKind.Calculator:
                try { Clipboard.SetDataObject(item.Payload, true); } catch { }
                return;
            case SpotlightResultKind.SystemCommand:
                SystemCommandRequested?.Invoke(item.Payload);
                return;
            case SpotlightResultKind.WebSearch:
                try
                {
                    string url = $"https://www.bing.com/search?q={Uri.EscapeDataString(item.Payload)}";
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch { }
                return;
            case SpotlightResultKind.Application:
            case SpotlightResultKind.File:
                try
                {
                    if (revealInExplorer && (File.Exists(item.Payload) || Directory.Exists(item.Payload)))
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Payload}\"") { UseShellExecute = true });
                    }
                    else if (item.Payload.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", item.Payload) { UseShellExecute = true });
                    }
                    else
                    {
                        string? workDir = System.IO.Path.GetDirectoryName(item.Payload);
                        Process.Start(new ProcessStartInfo(item.Payload)
                        {
                            UseShellExecute = true,
                            WorkingDirectory = !string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir) ? workDir : string.Empty
                        });
                    }
                }
                catch { }
                return;
        }
    }

    private static string KindLabel(SpotlightResultKind kind) => kind switch
    {
        SpotlightResultKind.Calculator => "计算",
        SpotlightResultKind.Application => "应用",
        SpotlightResultKind.SystemCommand => "命令",
        SpotlightResultKind.File => "文件",
        SpotlightResultKind.WebSearch => "网页",
        _ => string.Empty
    };
}
