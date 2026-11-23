using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GlassShell.Core.Config;
using GlassShell.Core.MenuEngine;
using GlassShell.UI.Glass;

namespace GlassShell.UI.MenuBar;

public sealed record PopupMenuEntry(
    string Title,
    string? ShortcutText = null,
    bool IsEnabled = true,
    bool IsChecked = false,
    bool IsSeparator = false,
    Action? OnClick = null,
    IReadOnlyList<PopupMenuEntry>? Children = null);

public partial class LiquidGlassMenuPopup : Popup
{
    private static readonly SolidColorBrush HoverPillBrush = new(Color.FromRgb(0x0A, 0x84, 0xFF));
    private static readonly SolidColorBrush DarkTextBrush = new(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush LightTextBrush = new(Color.FromArgb(0xEC, 0x16, 0x16, 0x1A));
    private static readonly SolidColorBrush DarkSubtleBrush = new(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush LightSubtleBrush = new(Color.FromArgb(0x88, 0x16, 0x16, 0x1A));

    private DxgiBackdropSampler? _sampler;
    private LiquidGlassMenuPopup? _openSubmenu;

    public LiquidGlassMenuPopup()
    {
        InitializeComponent();
        Closed += (_, _) => CloseSubmenu();
    }

    public void ConfigureVisuals(DxgiBackdropSampler? sampler, GlassOpacityPreset preset, bool isDarkMode)
    {
        _sampler = sampler;
        GlassBackdrop.OpacityPreset = preset;
        GlassBackdrop.IsDarkMode = isDarkMode;
        if (sampler is not null)
        {
            GlassBackdrop.AttachSampler(sampler);
        }
    }

    public void Populate(IReadOnlyList<PopupMenuEntry> entries, bool isDarkMode)
    {
        CloseSubmenu();
        ItemsHost.Children.Clear();

        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                ItemsHost.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(8, 5, 8, 5),
                    Background = new SolidColorBrush(isDarkMode
                        ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)
                        : Color.FromArgb(0x22, 0x00, 0x00, 0x00))
                });
                continue;
            }

            ItemsHost.Children.Add(BuildMenuRow(entry, isDarkMode));
        }
    }

    public static IReadOnlyList<PopupMenuEntry> FromMenuNodes(
        IReadOnlyList<MenuNode> nodes,
        Action<MenuNode> onNodeInvoked)
    {
        var list = new List<PopupMenuEntry>(nodes.Count);
        foreach (var node in nodes)
        {
            if (node.IsSeparator)
            {
                list.Add(new PopupMenuEntry(string.Empty, IsSeparator: true));
                continue;
            }

            IReadOnlyList<PopupMenuEntry>? sub = node.Children.Count > 0
                ? FromMenuNodes(node.Children, onNodeInvoked)
                : null;

            list.Add(new PopupMenuEntry(
                Title: node.Title,
                ShortcutText: node.ShortcutText,
                IsEnabled: node.IsEnabled,
                IsChecked: node.IsChecked,
                IsSeparator: false,
                OnClick: sub is null ? () => onNodeInvoked(node) : null,
                Children: sub));
        }

        return list;
    }

    private Border BuildMenuRow(PopupMenuEntry entry, bool isDarkMode)
    {
        var grid = new Grid { MinHeight = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var primaryBrush = isDarkMode ? DarkTextBrush : LightTextBrush;
        var subtleBrush = isDarkMode ? DarkSubtleBrush : LightSubtleBrush;

        if (entry.IsChecked)
        {
            var check = new TextBlock
            {
                Text = "✓",
                FontSize = 11,
                Foreground = primaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(check, 0);
            grid.Children.Add(check);
        }

        var titleBlock = new TextBlock
        {
            Text = entry.Title,
            FontSize = 13,
            Foreground = primaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 20, 0)
        };
        Grid.SetColumn(titleBlock, 1);
        grid.Children.Add(titleBlock);

        TextBlock? trailingBlock = null;
        if (entry.Children is { Count: > 0 })
        {
            trailingBlock = new TextBlock
            {
                Text = "›",
                FontSize = 14,
                Foreground = subtleBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else if (!string.IsNullOrWhiteSpace(entry.ShortcutText))
        {
            trailingBlock = new TextBlock
            {
                Text = entry.ShortcutText,
                FontSize = 12,
                Foreground = subtleBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        if (trailingBlock is not null)
        {
            Grid.SetColumn(trailingBlock, 2);
            grid.Children.Add(trailingBlock);
        }

        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 10, 3),
            Background = Brushes.Transparent,
            Opacity = entry.IsEnabled ? 1.0 : 0.42,
            Child = grid
        };

        if (entry.IsEnabled)
        {
            row.MouseEnter += (_, _) =>
            {
                row.Background = HoverPillBrush;
                titleBlock.Foreground = Brushes.White;
                if (trailingBlock is not null) trailingBlock.Foreground = Brushes.White;

                if (entry.Children is { Count: > 0 })
                {
                    OpenSubmenu(row, entry.Children, isDarkMode);
                }
                else
                {
                    CloseSubmenu();
                }
            };

            row.MouseLeave += (_, _) =>
            {
                row.Background = Brushes.Transparent;
                titleBlock.Foreground = primaryBrush;
                if (trailingBlock is not null) trailingBlock.Foreground = subtleBrush;
            };

            row.MouseLeftButtonUp += (_, e) =>
            {
                if (entry.OnClick is null) return;
                e.Handled = true;
                IsOpen = false;
                entry.OnClick();
            };
        }

        return row;
    }

    private void OpenSubmenu(UIElement anchor, IReadOnlyList<PopupMenuEntry> children, bool isDarkMode)
    {
        CloseSubmenu();
        _openSubmenu = new LiquidGlassMenuPopup
        {
            Placement = PlacementMode.Right,
            PlacementTarget = anchor,
            HorizontalOffset = -6,
            VerticalOffset = -12
        };
        _openSubmenu.ConfigureVisuals(_sampler, GlassBackdrop.OpacityPreset, isDarkMode);
        _openSubmenu.Populate(children, isDarkMode);
        _openSubmenu.IsOpen = true;
    }

    private void CloseSubmenu()
    {
        if (_openSubmenu is not null)
        {
            _openSubmenu.IsOpen = false;
            _openSubmenu = null;
        }
    }
}
