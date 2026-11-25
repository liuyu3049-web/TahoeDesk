using System;
using System.Windows;
using TahoeDesk.Core.Config;
using TahoeDesk.UI.Glass;

namespace TahoeDesk.UI.MenuBar;

public partial class AboutPcDialog : Window
{
    public AboutPcDialog(DxgiBackdropSampler? sampler, GlassOpacityPreset preset, bool isDarkMode)
    {
        InitializeComponent();
        GlassBackdrop.OpacityPreset = preset;
        GlassBackdrop.IsDarkMode = isDarkMode;
        if (sampler is not null) GlassBackdrop.AttachSampler(sampler);

        MachineNameText.Text = Environment.MachineName;
        OsVersionText.Text = $"Windows 11 ({Environment.OSVersion.Version.Build})";
        CpuText.Text = $"{Environment.ProcessorCount} 逻辑核心 ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})";
        double ramGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0);
        MemoryText.Text = $"{ramGb:F1} GB";

        SourceInitialized += (_, _) => WindowInteropHelperEx.ExcludeFromCapture(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        Closed += (_, _) => GlassBackdrop.Dispose();
        MouseLeftButtonDown += (_, _) => DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
