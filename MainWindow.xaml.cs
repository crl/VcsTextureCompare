using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using VcsTextureCompare.Cli;
using VcsTextureCompare.Compare;
using VcsTextureCompare.Svn;
using VcsTextureCompare.Tortoise;

namespace VcsTextureCompare;

/// <summary>
/// 主窗口：左侧 SVN 历史，右侧拉杆对比视口。
/// </summary>
public partial class MainWindow : Window
{
    private readonly StartupArgs _startup;
    private readonly ObservableCollection<SvnLogEntry> _log = new();
    private string? _localPath;
    private LoadedTexture? _leftTexture;
    private LoadedTexture? _rightTexture;
    private PixelDiffMap? _diff;
    private CancellationTokenSource? _loadCts;
    private bool _suppressLogSelection;
    private byte _threshold;

    public MainWindow() : this(new StartupArgs())
    {
    }

    public MainWindow(StartupArgs startup)
    {
        _startup = startup;
        InitializeComponent();
        LogList.ItemsSource = _log;
        if (_startup.DirectCompare)
            HideSidebar();
        SourceInitialized += (_, _) => WinBackdrop.TryApplyAcrylic(this);
        Loaded += async (_, _) => await OnLoadedAsync();
    }

    /// <summary>TortoiseSVN Diff 已带左右两图，只留对比视口。</summary>
    private void HideSidebar()
    {
        SidebarPanel.Visibility = Visibility.Collapsed;
        AppChrome.ColumnDefinitions[0].Width = new GridLength(0);
        AppChrome.ColumnDefinitions[0].MinWidth = 0;
        AppChrome.ColumnDefinitions[1].Width = new GridLength(0);
        MinWidth = 640;
        Width = 980;
    }

    private async Task OnLoadedAsync()
    {
        if (_startup.DirectCompare)
        {
            await LoadPairAsync(_startup.LeftPath, _startup.RightPath, bindLocal: _startup.RightPath);
            return;
        }
        if (!string.IsNullOrWhiteSpace(_startup.RightPath))
            await OpenLocalAsync(_startup.RightPath);
    }

    private async void OpenButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择贴图",
            Filter = "贴图|*.png;*.jpg;*.jpeg;*.tga;*.bmp;*.gif;*.webp|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) == true)
            await OpenLocalAsync(dlg.FileName);
    }

    private async void Window_OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return;
        var path = files.FirstOrDefault(ImageLoader.IsSupported) ?? files[0];
        await OpenLocalAsync(path);
    }

    private void Window_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void CompareBase_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_localPath))
        {
            SetError("请先打开一张本地贴图。");
            return;
        }
        await CompareRevisionAsync("BASE");
        SelectLogByRevision(SvnLogEntry.BaseRevision);
    }

    private async void RefreshLog_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_localPath))
        {
            SetError("请先打开一张本地贴图。");
            return;
        }
        await ReloadLogAsync(_localPath);
    }

    private async void LogList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() => MoveLogSelectThumb(IsLoaded), DispatcherPriority.Loaded);
        if (_suppressLogSelection)
            return;
        if (LogList.SelectedItem is not SvnLogEntry entry || string.IsNullOrEmpty(_localPath))
            return;
        var rev = entry.IsBase ? "BASE" : entry.Revision.ToString();
        await CompareRevisionAsync(rev);
    }

    private void LogList_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        MoveLogSelectThumb(animate: false);
    }

    private void Mode_OnChecked(object sender, RoutedEventArgs e)
    {
        // IsChecked 在 InitializeComponent 里就会触发，此时后面的 RadioButton 还没建出来。
        MoveModeSegmentThumb(animate: IsLoaded);
        if (!IsLoaded || CompareView == null || OverlayPanel == null)
            return;
        if (ModeSide?.IsChecked == true)
            CompareView.Mode = CompareMode.SideBySide;
        else if (ModeHeatmap?.IsChecked == true)
            CompareView.Mode = CompareMode.Heatmap;
        else if (ModeOverlay?.IsChecked == true)
            CompareView.Mode = CompareMode.Overlay;
        else if (ModeBlink?.IsChecked == true)
            CompareView.Mode = CompareMode.Blink;
        else
            CompareView.Mode = CompareMode.Wipe;
        OverlayPanel.Visibility = CompareView.Mode == CompareMode.Overlay ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ModeSegmentHost_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        MoveModeSegmentThumb(animate: false);
    }

    private void OverlaySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CompareView != null)
            CompareView.OverlayOpacity = e.NewValue;
    }

    private void ThresholdSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _threshold = (byte)Math.Clamp(Math.Round(e.NewValue), 0, 255);
        if (ThresholdText != null)
            ThresholdText.Text = _threshold.ToString();
        RefreshDiffVisuals();
    }

    /// <summary>F：居中适配整张图，与顶栏「适配」相同。</summary>
    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey
            : e.Key == Key.ImeProcessed ? e.ImeProcessedKey
            : e.Key;
        if (key != Key.F)
            return;
        FitToView();
        e.Handled = true;
    }

    private void FitToView()
    {
        if (FitModeFit != null)
            FitModeFit.IsChecked = true;
        CompareView.Fit();
    }

    private void FitSegment_OnChecked(object sender, RoutedEventArgs e)
    {
        MoveFitSegmentThumb(animate: IsLoaded);
        if (!IsLoaded)
            return;
        if (FitModeActual?.IsChecked == true)
            CompareView.ResetZoom();
        else
            CompareView.Fit();
    }

    private void FitSegmentHost_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        MoveFitSegmentThumb(animate: false);
    }

    /// <summary>iOS 分段控件：玻璃滑块跟着手指选项平移。</summary>
    private void MoveFitSegmentThumb(bool animate)
    {
        if (FitSegmentThumb == null || FitSegmentHost == null || FitSegmentTransform == null)
            return;
        var slot = Math.Max(0, FitSegmentHost.ActualWidth / 2);
        var toX = FitModeActual?.IsChecked == true ? slot : 0;
        BubbleMotion.Go(FitSegmentTransform, FitSegmentThumb, alongX: true,
            toX, slot, FitSegmentHost.ActualHeight, animate);
    }

    private void MoveModeSegmentThumb(bool animate)
    {
        if (ModeSegmentThumb == null || ModeSegmentHost == null || ModeSegmentTransform == null)
            return;
        var slot = Math.Max(0, ModeSegmentHost.ActualWidth / 5);
        var index = ModeSide?.IsChecked == true ? 1
            : ModeHeatmap?.IsChecked == true ? 2
            : ModeOverlay?.IsChecked == true ? 3
            : ModeBlink?.IsChecked == true ? 4
            : 0;
        BubbleMotion.Go(ModeSegmentTransform, ModeSegmentThumb, alongX: true,
            slot * index, slot, ModeSegmentHost.ActualHeight, animate);
    }

    private void MoveLogSelectThumb(bool animate)
    {
        if (LogSelectThumb == null || LogSelectHost == null || LogSelectTransform == null || LogList == null)
            return;
        if (LogList.SelectedItem == null
            || LogList.ItemContainerGenerator.ContainerFromItem(LogList.SelectedItem) is not ListViewItem item)
        {
            LogSelectThumb.Visibility = Visibility.Collapsed;
            return;
        }
        var pos = item.TranslatePoint(new Point(0, 0), LogSelectHost);
        LogSelectThumb.Visibility = Visibility.Visible;
        BubbleMotion.Go(LogSelectTransform, LogSelectThumb, alongX: false,
            pos.Y, item.ActualHeight, LogSelectHost.ActualWidth, animate);
    }

    private void Register_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "VcsTextureCompare.exe");
            var msg = DiffToolRegistrar.Register(exe);
            SetError(null);
            MessageBox.Show(this, msg, "SVN 贴图对比", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    private void Window_OnClosed(object? sender, EventArgs e)
    {
        _loadCts?.Cancel();
        BubbleMotion.StopAll();
        SvnClient.CleanupTempFiles();
    }

    /// <summary>独立模式：右侧为工作副本文件，左侧拉 SVN 历史。</summary>
    private async Task OpenLocalAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            SetError("文件不存在。");
            return;
        }
        if (!ImageLoader.IsSupported(path))
        {
            SetError("不支持的图片格式。可用 PNG / JPG / TGA / BMP / WEBP / GIF。");
            return;
        }
        CompareView.LeftLabel = "历史";
        CompareView.RightLabel = "本地";
        _localPath = path;
        PathBox.Text = path;
        Title = "SVN 贴图对比 - " + Path.GetFileName(path);
        SetError(null);
        await LoadRightAsync(path);
        if (_startup.FromTortoise || IsSvnTempPath(path))
            return;
        await ReloadLogAsync(path);
        await CompareRevisionAsync("BASE");
        SelectLogByRevision(SvnLogEntry.BaseRevision);
    }

    /// <summary>Tortoise 传入的左右文件；新增/删除时允许一侧为空。</summary>
    private async Task LoadPairAsync(string? leftPath, string? rightPath, string? bindLocal)
    {
        SetError(null);
        var display = StartupArgs.IsUsable(rightPath) ? rightPath
            : StartupArgs.IsUsable(leftPath) ? leftPath
            : bindLocal;
        _localPath = display != null && File.Exists(display) ? Path.GetFullPath(display) : display;
        PathBox.Text = _localPath ?? "";
        Title = "SVN 贴图对比 - " + (string.IsNullOrEmpty(_localPath) ? "Diff" : Path.GetFileName(_localPath));
        var cts = ReplaceCts();
        SetBusy(true, "正在解码贴图…");
        try
        {
            _leftTexture = await TryLoadTextureAsync(leftPath, cts.Token);
            _rightTexture = await TryLoadTextureAsync(rightPath, cts.Token);
            if (_leftTexture == null && _rightTexture == null)
            {
                SetError("左右两侧都没有可解码的贴图。");
                return;
            }
            CompareView.LeftLabel = _leftTexture == null
                ? "无（新增）"
                : string.IsNullOrWhiteSpace(_startup.LeftTitle) ? "历史" : _startup.LeftTitle;
            CompareView.RightLabel = _rightTexture == null
                ? "无（删除）"
                : string.IsNullOrWhiteSpace(_startup.RightTitle) ? "本地" : _startup.RightTitle;
            if (_leftTexture == null)
                CompareView.WipeRatio = 0;
            else if (_rightTexture == null)
                CompareView.WipeRatio = 1;
            RebuildDiff();
            PushImages();
            UpdateFileInfo();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
        if (_leftTexture != null || _rightTexture != null)
        {
            if (_localPath != null && !_startup.FromTortoise && !IsSvnTempPath(_localPath))
                await ReloadLogAsync(_localPath);
        }
    }

    private static async Task<LoadedTexture?> TryLoadTextureAsync(string? path, CancellationToken ct)
    {
        if (!StartupArgs.IsUsable(path) || !ImageLoader.IsSupported(path!))
            return null;
        try
        {
            return await ImageLoader.LoadAsync(path!, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Tortoise 抽出的临时文件，例如 icon.png.svn002.tmp.png。</summary>
    private static bool IsSvnTempPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        var name = Path.GetFileName(path);
        var i = name.IndexOf(".svn", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && name.IndexOf(".tmp.", i, StringComparison.OrdinalIgnoreCase) > i;
    }

    private async Task LoadRightAsync(string path)
    {
        var cts = ReplaceCts();
        SetBusy(true, "正在解码贴图…");
        try
        {
            _rightTexture = await ImageLoader.LoadAsync(path, cts.Token);
            _leftTexture = null;
            _diff = null;
            PushImages();
            UpdateFileInfo();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReloadLogAsync(string path)
    {
        _suppressLogSelection = true;
        _log.Clear();
        _log.Add(SvnLogEntry.CreateBase());
        try
        {
            if (!SvnClient.IsAvailable)
            {
                SetError("未找到 svn 命令，请确认已安装 SVN 命令行并加入 PATH（TortoiseSVN 需勾选 command line tools）。");
                return;
            }
            SetBusy(true, "正在读取 SVN 日志…");
            var entries = await SvnClient.GetLogAsync(path);
            foreach (var entry in entries)
                _log.Add(entry);
            try
            {
                var info = await SvnClient.GetInfoAsync(path);
                if (info != null)
                    UpdateFileInfo(info);
            }
            catch
            {
                // info 失败不影响日志
            }
            if (ErrorText.Text.StartsWith("未找到 svn", StringComparison.Ordinal))
                SetError(null);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
        finally
        {
            SetBusy(false);
            _suppressLogSelection = false;
        }
    }

    private async Task CompareRevisionAsync(string revision)
    {
        if (string.IsNullOrEmpty(_localPath))
            return;
        if (!SvnClient.IsAvailable)
        {
            SetError("未找到 svn 命令，请确认已安装 SVN 命令行并加入 PATH（TortoiseSVN 需勾选 command line tools）。");
            return;
        }
        var cts = ReplaceCts();
        SetBusy(true, "正在取出历史版本 r" + revision + "…");
        try
        {
            var temp = await SvnClient.CatToTempAsync(_localPath, revision, cts.Token);
            _leftTexture = await ImageLoader.LoadAsync(temp, cts.Token);
            CompareView.LeftLabel = revision == "BASE" ? "历史 BASE" : "历史 r" + revision;
            RebuildDiff();
            PushImages();
            UpdateFileInfo();
            SetError(null);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // 新增文件没有 BASE，保留已加载的本地图。
            SetError("无法获取历史版本：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RebuildDiff()
    {
        _diff = _leftTexture != null && _rightTexture != null
            ? PixelDiffMap.Compute(_leftTexture, _rightTexture)
            : null;
        RefreshDiffVisuals();
    }

    private void RefreshDiffVisuals()
    {
        if (StatsText == null || CompareView == null)
            return;
        if (_diff == null)
        {
            StatsText.Text = _rightTexture == null ? "尚未对比" : "仅有本地图，等待历史版本";
            CompareView.SetHeatmap(null);
            return;
        }
        var stats = _diff.GetStats(_threshold);
        var size = stats.SizeMismatch
            ? $"尺寸不一致 {stats.LeftWidth}×{stats.LeftHeight} vs {stats.RightWidth}×{stats.RightHeight}"
            : $"{stats.RightWidth}×{stats.RightHeight}";
        StatsText.Text = $"差异 {stats.ChangedPercent:0.##}%（{stats.ChangedPixels:N0}/{stats.TotalPixels:N0}）　最大通道差 {stats.MaxChannelDelta}　{size}";
        try
        {
            CompareView.SetHeatmap(_diff.BuildHeatmap(_threshold));
        }
        catch (Exception ex)
        {
            SetError("生成热力图失败：" + ex.Message);
        }
    }

    private void PushImages()
    {
        CompareView.SetImages(_leftTexture?.Bitmap, _rightTexture?.Bitmap, null);
        if (_diff != null)
            CompareView.SetHeatmap(_diff.BuildHeatmap(_threshold));
    }

    private void UpdateFileInfo(SvnInfo? info = null)
    {
        var parts = new List<string>();
        if (_rightTexture != null)
        {
            parts.Add($"本地 {_rightTexture.Width}×{_rightTexture.Height} {_rightTexture.Format} {ImageLoader.FormatBytes(_rightTexture.FileSize)}");
        }
        if (_leftTexture != null)
        {
            parts.Add($"历史 {_leftTexture.Width}×{_leftTexture.Height} {_leftTexture.Format} {ImageLoader.FormatBytes(_leftTexture.FileSize)}");
        }
        if (info != null)
            parts.Add($"工作副本 r{info.Revision}　最后提交 r{info.CommitRevision}");
        FileInfoText.Text = parts.Count == 0
            ? "打开工作副本中的贴图，或从 TortoiseSVN Diff 拉起。"
            : string.Join("\n", parts);
    }

    private void SelectLogByRevision(long revision)
    {
        _suppressLogSelection = true;
        try
        {
            var match = _log.FirstOrDefault(x => x.Revision == revision);
            if (match != null)
                LogList.SelectedItem = match;
        }
        finally
        {
            _suppressLogSelection = false;
        }
    }

    private CancellationTokenSource ReplaceCts()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        return _loadCts;
    }

    private void SetBusy(bool busy, string? text = null)
    {
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (text != null)
            BusyText.Text = text;
        if (busy)
            _ = Dispatcher.InvokeAsync(UpdateBusyLens, DispatcherPriority.Loaded);
    }

    /// <summary>忙碌卡片取样背后界面并放大模糊，做出透镜扭曲。</summary>
    private void UpdateBusyLens()
    {
        if (BusyLensBrush == null || AppChrome == null || BusyCard == null)
            return;
        BusyCard.UpdateLayout();
        if (BusyCard.ActualWidth < 1 || BusyCard.ActualHeight < 1)
            return;
        BusyLensBrush.Visual = AppChrome;
        const double magnify = 1.28;
        var center = BusyCard.TranslatePoint(
            new Point(BusyCard.ActualWidth / 2, BusyCard.ActualHeight / 2), AppChrome);
        var w = BusyCard.ActualWidth / magnify;
        var h = BusyCard.ActualHeight / magnify;
        BusyLensBrush.Viewbox = new Rect(center.X - w / 2, center.Y - h / 2, w, h);
    }

    private void SetError(string? message)
    {
        ErrorText.Text = message ?? "";
    }
}
