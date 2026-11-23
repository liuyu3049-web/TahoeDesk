using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GlassShell.Core.Config;
using GlassShell.Core.MenuEngine;
using GlassShell.UI.Glass;

namespace GlassShell.UI.MenuBar;

public partial class CalendarPopup : Popup
{
    private DateTime _displayMonth = new(DateTime.Now.Year, DateTime.Now.Month, 1);
    private readonly MenuCommandDispatcher _dispatcher = new();
    private readonly OutsideClickDismissHook _dismissHook;

    public CalendarPopup()
    {
        InitializeComponent();
        _dismissHook = new OutsideClickDismissHook(
            CollectOpenHwnds,
            () => Dispatcher.BeginInvoke(() => IsOpen = false));

        Opened += (_, _) =>
        {
            WindowInteropHelperEx.ExcludeVisualFromCapture(Child);
            _displayMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
            RenderMonth();
            _dismissHook.Start();
        };
        Closed += (_, _) => _dismissHook.Stop();
    }

    public nint OwnerAnchorHwnd { get; set; }

    private System.Collections.Generic.IEnumerable<nint> CollectOpenHwnds()
    {
        if (OwnerAnchorHwnd != 0) yield return OwnerAnchorHwnd;
        if (Child is not null && PresentationSource.FromVisual(Child) is System.Windows.Interop.HwndSource src && src.Handle != 0)
        {
            yield return src.Handle;
        }
    }

    public void ConfigureVisuals(DxgiBackdropSampler? sampler, GlassOpacityPreset preset, bool isDarkMode)
    {
        GlassBackdrop.OpacityPreset = preset;
        GlassBackdrop.IsDarkMode = isDarkMode;
        if (sampler is not null)
        {
            GlassBackdrop.AttachSampler(sampler);
        }
    }

    private void RenderMonth()
    {
        MonthTitleText.Text = $"{_displayMonth.Year}年{_displayMonth.Month}月";
        DaysGrid.Children.Clear();

        DateTime today = DateTime.Today;
        int startOffset = (int)_displayMonth.DayOfWeek;
        DateTime cursor = _displayMonth.AddDays(-startOffset);

        for (int i = 0; i < 42; i++)
        {
            bool isCurrentMonth = cursor.Month == _displayMonth.Month;
            bool isToday = cursor.Date == today;

            var cell = new Border
            {
                Width = 28,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = isToday ? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)) : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = cursor.Day.ToString(),
                    FontSize = 12,
                    FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = isToday
                        ? Brushes.White
                        : new SolidColorBrush(isCurrentMonth
                            ? Color.FromArgb(0xEC, 0xFF, 0xFF, 0xFF)
                            : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            DaysGrid.Children.Add(cell);
            cursor = cursor.AddDays(1);
        }
    }

    private void OnPrevMonthClick(object sender, RoutedEventArgs e)
    {
        _displayMonth = _displayMonth.AddMonths(-1);
        RenderMonth();
    }

    private void OnNextMonthClick(object sender, RoutedEventArgs e)
    {
        _displayMonth = _displayMonth.AddMonths(1);
        RenderMonth();
    }

    private void OnTodayClick(object sender, RoutedEventArgs e)
    {
        _displayMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
        RenderMonth();
    }

    private void OnOpenNotificationCenterClick(object sender, RoutedEventArgs e)
    {
        IsOpen = false;
        _dispatcher.Dispatch(0, new MenuInvokeAction(MenuActionKind.KeyboardShortcut, Shortcut: "Win+N"));
    }
}
