using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VcsTextureCompare.Compare;

/// <summary>
/// 贴图对比视口：默认中间拉杆擦除（左历史 / 右本地），两图共用缩放平移。
/// </summary>
public partial class ImageCompareView : UserControl
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(CompareMode), typeof(ImageCompareView),
        new PropertyMetadata(CompareMode.Wipe, OnVisualChanged));

    public static readonly DependencyProperty WipeRatioProperty = DependencyProperty.Register(
        nameof(WipeRatio), typeof(double), typeof(ImageCompareView),
        new PropertyMetadata(0.5, OnVisualChanged));

    public static readonly DependencyProperty OverlayOpacityProperty = DependencyProperty.Register(
        nameof(OverlayOpacity), typeof(double), typeof(ImageCompareView),
        new PropertyMetadata(0.5, OnVisualChanged));

    public static readonly DependencyProperty LeftLabelProperty = DependencyProperty.Register(
        nameof(LeftLabel), typeof(string), typeof(ImageCompareView),
        new PropertyMetadata("历史", OnLabelChanged));

    public static readonly DependencyProperty RightLabelProperty = DependencyProperty.Register(
        nameof(RightLabel), typeof(string), typeof(ImageCompareView),
        new PropertyMetadata("本地", OnLabelChanged));

    private const double MinScale = 0.02;
    private const double MaxScale = 32;
    private const double HandleHitPadding = 16;
    private const double HandleMagnify = 1.42;
    private const double HandleRest = 44;
    private const double FilmOmega = 28;
    private const double FilmZeta = 0.48;
    /// <summary>拖尾只跟拉杆速度走：约 800px/s 拉到 32%。</summary>
    private const double BetaV = 0.31;
    private const double MaxStrain = 0.42;
    /// <summary>放手后速度按 e^{-kt} 衰减，800px/s 大约还能滑 250px。</summary>
    private const double CoastDamp = 3.2;
    private const double BounceRestitution = 0.68;
    private const double CoastMinV = 28;
    private const double MaxWipeV = 2800;
    /// <summary>按住不动时速度按 e^{-kt} 掉光；约 0.5s 后惯性基本没了。</summary>
    private const double HoldStillDamp = 7;

    private BitmapSource? _left;
    private BitmapSource? _right;
    private BitmapSource? _heatmap;
    private double _contentWidth;
    private double _contentHeight;
    private bool _panning;
    private bool _wiping;
    private bool _wipeSim;
    private bool _layoutFromSim;
    private bool _userTransformed;
    private Point _lastMouse;
    private double _wipeGrabX;
    private double _lineX;
    private double _wipeV;
    private double _impactV;
    private long _lastMoveTick;
    private long _lastMotionTick;
    private double _eps;
    private double _epsV;
    private double _stretchDir = 1;
    private TimeSpan _wipeLast;
    private readonly EventHandler _onWipeRender;
    private readonly DispatcherTimer _blinkTimer;
    private bool _blinkShowLeft = true;

    public ImageCompareView()
    {
        InitializeComponent();
        _onWipeRender = OnWipeRender;
        HandleLensBrush.Visual = ImageLayer;
        Loaded += (_, _) =>
        {
            if (!_userTransformed)
                Fit();
        };
        Unloaded += (_, _) => StopWipeSim(snap: true);
        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _blinkTimer.Tick += (_, _) =>
        {
            _blinkShowLeft = !_blinkShowLeft;
            ApplyModeVisibility();
        };
    }

    public CompareMode Mode
    {
        get => (CompareMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public double WipeRatio
    {
        get => (double)GetValue(WipeRatioProperty);
        set => SetValue(WipeRatioProperty, value);
    }

    public double OverlayOpacity
    {
        get => (double)GetValue(OverlayOpacityProperty);
        set => SetValue(OverlayOpacityProperty, value);
    }

    public string LeftLabel
    {
        get => (string)GetValue(LeftLabelProperty);
        set => SetValue(LeftLabelProperty, value);
    }

    public string RightLabel
    {
        get => (string)GetValue(RightLabelProperty);
        set => SetValue(RightLabelProperty, value);
    }

    public void SetImages(BitmapSource? left, BitmapSource? right, BitmapSource? heatmap)
    {
        _left = left;
        _right = right;
        _heatmap = heatmap;
        LeftImage.Source = left;
        RightImage.Source = right;
        HeatmapImage.Source = heatmap;
        _contentWidth = Math.Max(left?.PixelWidth ?? 0, right?.PixelWidth ?? 0);
        _contentHeight = Math.Max(left?.PixelHeight ?? 0, right?.PixelHeight ?? 0);
        if (_heatmap != null)
        {
            _contentWidth = Math.Max(_contentWidth, _heatmap.PixelWidth);
            _contentHeight = Math.Max(_contentHeight, _heatmap.PixelHeight);
        }
        ContentRoot.Width = Math.Max(_contentWidth, 1);
        ContentRoot.Height = Math.Max(_contentHeight, 1);
        ImageLayer.Width = ContentRoot.Width;
        ImageLayer.Height = ContentRoot.Height;
        SizeImage(LeftImage, left);
        SizeImage(RightImage, right);
        SizeImage(HeatmapImage, heatmap);
        HintText.Visibility = left == null && right == null ? Visibility.Visible : Visibility.Collapsed;
        LeftLabelHost.Visibility = left != null ? Visibility.Visible : Visibility.Collapsed;
        RightLabelHost.Visibility = right != null ? Visibility.Visible : Visibility.Collapsed;
        _userTransformed = false;
        StopWipeSim(snap: false);
        UpdateBlinkTimer();
        ApplyModeVisibility();
        UpdateWipeChrome();
        Dispatcher.InvokeAsync(Fit, DispatcherPriority.Loaded);
    }

    public void SetHeatmap(BitmapSource? heatmap)
    {
        _heatmap = heatmap;
        HeatmapImage.Source = heatmap;
        SizeImage(HeatmapImage, heatmap);
        ApplyModeVisibility();
    }

    public void Fit()
    {
        if (_contentWidth < 1 || _contentHeight < 1)
            return;
        Viewport.UpdateLayout();
        var viewWidth = Viewport.ActualWidth;
        var viewHeight = Viewport.ActualHeight;
        if (viewWidth < 1 || viewHeight < 1)
        {
            Dispatcher.InvokeAsync(Fit, DispatcherPriority.Loaded);
            return;
        }
        const double pad = 24;
        var scale = Math.Min((viewWidth - pad * 2) / _contentWidth, (viewHeight - pad * 2) / _contentHeight);
        scale = Math.Clamp(scale, MinScale, MaxScale);
        ZoomScale.ScaleX = ZoomScale.ScaleY = scale;
        PanOffset.X = (viewWidth - _contentWidth * scale) / 2;
        PanOffset.Y = (viewHeight - _contentHeight * scale) / 2;
        _userTransformed = false;
    }

    public void ResetZoom()
    {
        if (_contentWidth < 1)
            return;
        ZoomScale.ScaleX = ZoomScale.ScaleY = 1;
        PanOffset.X = (Viewport.ActualWidth - _contentWidth) / 2;
        PanOffset.Y = (Viewport.ActualHeight - _contentHeight) / 2;
        _userTransformed = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey
            : e.Key == Key.ImeProcessed ? e.ImeProcessedKey
            : e.Key;
        if (key == Key.F)
        {
            Fit();
            e.Handled = true;
            return;
        }
        if (Mode == CompareMode.Wipe && _left != null && _right != null)
        {
            if (e.Key == Key.Left)
            {
                SetWipeFromContentX(_contentWidth * (WipeRatio - 0.02));
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Right)
            {
                SetWipeFromContentX(_contentWidth * (WipeRatio + 0.02));
                e.Handled = true;
                return;
            }
        }
        base.OnKeyDown(e);
    }

    private static void SizeImage(Image image, BitmapSource? src)
    {
        if (src == null)
        {
            image.Width = 0;
            image.Height = 0;
            return;
        }
        image.Width = src.PixelWidth;
        image.Height = src.PixelHeight;
        Canvas.SetLeft(image, 0);
        Canvas.SetTop(image, 0);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (ImageCompareView)d;
        if (e.Property == WipeRatioProperty && view._layoutFromSim)
        {
            view.UpdateWipeClips();
            return;
        }
        view.UpdateBlinkTimer();
        view.ApplyModeVisibility();
        view.UpdateWipeChrome();
    }

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (ImageCompareView)d;
        if (view.LeftLabelText == null)
            return;
        view.LeftLabelText.Text = string.IsNullOrWhiteSpace(view.LeftLabel) ? "历史" : view.LeftLabel;
        view.RightLabelText.Text = string.IsNullOrWhiteSpace(view.RightLabel) ? "本地" : view.RightLabel;
    }

    private void UpdateBlinkTimer()
    {
        if (Mode == CompareMode.Blink && (_left != null || _right != null))
            _blinkTimer.Start();
        else
            _blinkTimer.Stop();
    }

    private void ApplyModeVisibility()
    {
        var hasPair = _left != null && _right != null;
        switch (Mode)
        {
            case CompareMode.Heatmap:
                // 本地图垫底，热力图只标差异；否则图集空白会被涂成一片黑。
                LeftImage.Visibility = Visibility.Collapsed;
                RightImage.Visibility = _right != null ? Visibility.Visible : Visibility.Collapsed;
                HeatmapImage.Visibility = _heatmap != null ? Visibility.Visible : Visibility.Collapsed;
                HeatmapImage.Opacity = 1;
                RightImage.Opacity = 1;
                LeftImage.Opacity = 1;
                ClearClips();
                SetWipeChromeVisible(false);
                break;
            case CompareMode.Overlay:
                LeftImage.Visibility = _left != null ? Visibility.Visible : Visibility.Collapsed;
                RightImage.Visibility = _right != null ? Visibility.Visible : Visibility.Collapsed;
                HeatmapImage.Visibility = Visibility.Collapsed;
                LeftImage.Opacity = 1;
                RightImage.Opacity = OverlayOpacity;
                ClearClips();
                SetWipeChromeVisible(false);
                break;
            case CompareMode.Blink:
                HeatmapImage.Visibility = Visibility.Collapsed;
                ClearClips();
                LeftImage.Opacity = 1;
                RightImage.Opacity = 1;
                LeftImage.Visibility = _blinkShowLeft && _left != null ? Visibility.Visible : Visibility.Collapsed;
                RightImage.Visibility = !_blinkShowLeft && _right != null ? Visibility.Visible : Visibility.Collapsed;
                SetWipeChromeVisible(false);
                break;
            default:
                HeatmapImage.Visibility = Visibility.Collapsed;
                RightImage.Visibility = _right != null ? Visibility.Visible : Visibility.Collapsed;
                LeftImage.Visibility = _left != null ? Visibility.Visible : Visibility.Collapsed;
                LeftImage.Opacity = 1;
                RightImage.Opacity = 1;
                SetWipeChromeVisible(hasPair);
                UpdateWipeClips();
                break;
        }
    }

    private void SetWipeChromeVisible(bool visible)
    {
        if (!visible)
            StopWipeSim(snap: true);
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        Divider.Visibility = v;
        Handle.Visibility = v;
    }

    private void UpdateWipeChrome()
    {
        if (Mode != CompareMode.Wipe || _contentWidth < 1)
        {
            StopWipeSim(snap: true);
            return;
        }
        if (_wipeSim)
            return;
        var x = _contentWidth * Math.Clamp(WipeRatio, 0, 1);
        _lineX = x;
        LayoutWipe(x);
    }

    /// <summary>
    /// 圆心钉在拉杆上；拖尾由应变决定，停手后表面张力自己收回圆形。
    /// </summary>
    private void LayoutWipe(double lineX)
    {
        Divider.Height = _contentHeight;
        Canvas.SetLeft(Divider, lineX - 1);
        Canvas.SetTop(Divider, 0);
        var r = HandleRest / 2;
        var extra = HandleRest * _eps;
        var along = HandleRest + extra;
        var cross = HandleRest * HandleRest / along;
        Handle.Width = along;
        Handle.Height = cross;
        var dragRight = _stretchDir >= 0;
        var left = dragRight ? lineX + r - along : lineX - r;
        var top = Math.Max(0, _contentHeight / 2 - cross / 2);
        Canvas.SetLeft(Handle, left);
        Canvas.SetTop(Handle, top);
        Handle.Clip = new EllipseGeometry(new Point(along / 2, cross / 2), along / 2, cross / 2);
        var headCx = lineX - left;
        var headMargin = new Thickness(headCx - r, (cross - HandleRest) / 2, 0, 0);
        if (HandleLens != null)
            HandleLens.Margin = headMargin;
        if (HandleGlyphs != null)
            HandleGlyphs.Margin = headMargin;
        if (HandleSheen != null)
            HandleSheen.CornerRadius = new CornerRadius(cross / 2);
        UpdateHandleLens(left + headCx - r, top + (cross - HandleRest) / 2);
        UpdateWipeClips();
    }

    /// <summary>圆柄 VisualBrush 取贴图上一块略小的区域，放大后模糊，做成透镜毛玻璃。</summary>
    private void UpdateHandleLens(double left, double top)
    {
        if (HandleLensBrush == null)
            return;
        var vw = HandleRest / HandleMagnify;
        var vh = HandleRest / HandleMagnify;
        HandleLensBrush.Viewbox = new Rect(
            left + (HandleRest - vw) / 2,
            top + (HandleRest - vh) / 2,
            vw,
            vh);
    }

    private void ClearClips()
    {
        LeftImage.Clip = null;
        RightImage.Clip = null;
    }

    /// <summary>
    /// 拉杆两侧各自只显示一张图。图集带透明通道时，若只裁历史图，
    /// 本地图会从透明空隙透出来，看起来像两份资源叠在一起。
    /// </summary>
    private void UpdateWipeClips()
    {
        if (Mode != CompareMode.Wipe)
        {
            ClearClips();
            return;
        }
        var ratio = Math.Clamp(WipeRatio, 0, 1);
        var x = _contentWidth * ratio;
        if (_left != null)
            LeftImage.Clip = new RectangleGeometry(new Rect(0, 0, x, _contentHeight));
        else
            LeftImage.Clip = null;
        if (_right != null)
            RightImage.Clip = new RectangleGeometry(new Rect(x, 0, Math.Max(0, _contentWidth - x), _contentHeight));
        else
            RightImage.Clip = null;
    }

    private void Viewport_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_contentWidth < 1)
            return;
        var pos = e.GetPosition(Viewport);
        var oldScale = ZoomScale.ScaleX;
        var factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        var newScale = Math.Clamp(oldScale * factor, MinScale, MaxScale);
        var contentX = (pos.X - PanOffset.X) / oldScale;
        var contentY = (pos.Y - PanOffset.Y) / oldScale;
        ZoomScale.ScaleX = ZoomScale.ScaleY = newScale;
        PanOffset.X = pos.X - contentX * newScale;
        PanOffset.Y = pos.Y - contentY * newScale;
        _userTransformed = true;
        e.Handled = true;
    }

    private void Viewport_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (_contentWidth < 1)
            return;
        var contentPos = e.GetPosition(ContentRoot);
        if (Mode == CompareMode.Wipe && _left != null && _right != null && HitWipe(contentPos))
        {
            _wiping = true;
            Viewport.CaptureMouse();
            BeginWipeDrag(contentPos.X);
            e.Handled = true;
            return;
        }
        _panning = true;
        _lastMouse = e.GetPosition(Viewport);
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndDrag();
    }

    private void Viewport_OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_contentWidth < 1)
            return;
        _panning = true;
        _lastMouse = e.GetPosition(Viewport);
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndDrag();
    }

    private void Viewport_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _contentWidth < 1)
            return;
        _panning = true;
        _lastMouse = e.GetPosition(Viewport);
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
            EndDrag();
    }

    private void Viewport_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_wiping)
        {
            SampleWipeVelocity(e.GetPosition(ContentRoot).X);
            SyncWipeLine(_wipeGrabX);
            return;
        }
        if (_panning)
        {
            var pos = e.GetPosition(Viewport);
            PanOffset.X += pos.X - _lastMouse.X;
            PanOffset.Y += pos.Y - _lastMouse.Y;
            _lastMouse = pos;
            _userTransformed = true;
        }
        else if (Mode == CompareMode.Wipe && _left != null && _right != null)
        {
            Viewport.Cursor = HitWipe(e.GetPosition(ContentRoot)) ? Cursors.SizeWE : Cursors.Arrow;
        }
    }

    private void Viewport_OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        EndDrag();
    }

    private void Viewport_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_userTransformed)
            Fit();
    }

    private void EndDrag()
    {
        if (_wiping)
        {
            _wiping = false;
            if (Math.Abs(_impactV) > Math.Abs(_wipeV))
                _wipeV = _impactV;
        }
        _panning = false;
        if (Viewport.IsMouseCaptured)
            Viewport.ReleaseMouseCapture();
    }

    private bool HitWipe(Point contentPos)
    {
        var x = _contentWidth * Math.Clamp(WipeRatio, 0, 1);
        if (Math.Abs(contentPos.X - x) <= HandleHitPadding && contentPos.Y >= 0 && contentPos.Y <= _contentHeight)
            return true;
        var handleLeft = x - Handle.Width / 2;
        var handleTop = _contentHeight / 2 - Handle.Height / 2;
        return new Rect(handleLeft, handleTop, Handle.Width, Handle.Height).Contains(contentPos);
    }

    private void SetWipeFromContentX(double x)
    {
        if (_contentWidth < 1)
            return;
        StopWipeSim(snap: false);
        WipeRatio = Math.Clamp(x / _contentWidth, 0, 1);
        UpdateWipeChrome();
    }

    private void BeginWipeDrag(double x)
    {
        _wipeGrabX = x;
        _wipeV = 0;
        _impactV = 0;
        _eps = 0;
        _epsV = 0;
        _lastMoveTick = 0;
        _lastMotionTick = 0;
        SyncWipeLine(x);
        StartWipeSim();
    }

    /// <summary>分割线与裁剪立刻落到指针，不等圆柄。</summary>
    private void SyncWipeLine(double x)
    {
        if (_contentWidth < 1)
            return;
        _lineX = Math.Clamp(x, 0, _contentWidth);
        _layoutFromSim = true;
        WipeRatio = _lineX / _contentWidth;
        _layoutFromSim = false;
        Divider.Height = _contentHeight;
        Canvas.SetLeft(Divider, _lineX - 1);
        Canvas.SetTop(Divider, 0);
        UpdateWipeClips();
    }

    private void StartWipeSim()
    {
        if (_wipeSim)
            return;
        _wipeSim = true;
        _wipeLast = TimeSpan.Zero;
        CompositionTarget.Rendering += _onWipeRender;
    }

    private void StopWipeSim(bool snap)
    {
        if (_wipeSim)
        {
            CompositionTarget.Rendering -= _onWipeRender;
            _wipeSim = false;
        }
        _wipeV = 0;
        _impactV = 0;
        _eps = 0;
        _epsV = 0;
        if (snap && _contentWidth > 1 && Handle != null)
            LayoutWipe(_contentWidth * Math.Clamp(WipeRatio, 0, 1));
    }

    private void OnWipeRender(object? sender, EventArgs e)
    {
        if (!_wipeSim || _contentWidth < 1)
            return;
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_wipeLast == TimeSpan.Zero)
        {
            _wipeLast = now;
            PushWipePose();
            return;
        }
        var dt = (now - _wipeLast).TotalSeconds;
        _wipeLast = now;
        if (dt <= 0)
            return;
        if (dt > 0.05)
            dt = 0.05;
        var steps = Math.Max(1, (int)Math.Ceiling(dt / 0.008));
        var h = dt / steps;
        if (_wiping)
        {
            _lineX = Math.Clamp(_wipeGrabX, 0, _contentWidth);
            if (Environment.TickCount64 - _lastMotionTick > 50)
            {
                var damp = Math.Exp(-HoldStillDamp * dt);
                _wipeV *= damp;
                _impactV *= damp;
                if (Math.Abs(_wipeV) < CoastMinV)
                    _wipeV = 0;
                if (Math.Abs(_impactV) < CoastMinV)
                    _impactV = 0;
            }
            if (Math.Abs(_wipeV) > 12)
                _stretchDir = Math.Sign(_wipeV);
        }
        for (var i = 0; i < steps; i++)
        {
            if (!_wiping)
                StepCoast(h);
            StepFilm(h);
        }
        if (!_wiping && Math.Abs(_wipeV) < CoastMinV && _eps < 0.004 && Math.Abs(_epsV) < 0.08)
        {
            StopWipeSim(snap: true);
            return;
        }
        PushWipePose();
    }

    /// <summary>用未钳制的指针位移取样速度，出屏后仍以最后一次有效位移为准。</summary>
    private void SampleWipeVelocity(double x)
    {
        var tick = Environment.TickCount64;
        if (_lastMoveTick != 0)
        {
            var moveDt = (tick - _lastMoveTick) / 1000.0;
            if (moveDt > 0.0004 && moveDt < 0.08)
            {
                var inst = Math.Clamp((x - _wipeGrabX) / moveDt, -MaxWipeV, MaxWipeV);
                var pastEdge = x < 0 || x > _contentWidth;
                // 撞边后坐标往往不再涨，不能把撞击速度掺成 0。
                if (Math.Abs(inst) > 12 || !(pastEdge || PointerPinnedOutside()))
                {
                    _wipeV = 0.35 * _wipeV + 0.65 * inst;
                    if (Math.Abs(_wipeV) > 12)
                        _stretchDir = Math.Sign(_wipeV);
                    if (Math.Abs(inst) > 12)
                    {
                        _lastMotionTick = tick;
                        LatchImpact();
                    }
                }
            }
        }
        _wipeGrabX = x;
        _lastMoveTick = tick;
    }

    /// <summary>贴边或指针已越界时锁住撞墙速度，避免钳制把动量卸掉。</summary>
    private void LatchImpact()
    {
        if (_contentWidth < 1)
            return;
        var atLeft = _lineX <= 1 || _wipeGrabX < 0;
        var atRight = _lineX >= _contentWidth - 1 || _wipeGrabX > _contentWidth;
        if (atLeft && _wipeV < 0)
            _impactV = Math.Min(_impactV, _wipeV);
        else if (atRight && _wipeV > 0)
            _impactV = Math.Max(_impactV, _wipeV);
        else if (!atLeft && !atRight)
            _impactV = 0;
    }

    /// <summary>指针已离开视口，或被系统夹在虚拟屏幕边缘。</summary>
    private bool PointerPinnedOutside()
    {
        var vp = Mouse.GetPosition(Viewport);
        if (vp.X < 0 || vp.X > Viewport.ActualWidth || vp.Y < 0 || vp.Y > Viewport.ActualHeight)
            return true;
        Point screen;
        try
        {
            screen = Viewport.PointToScreen(vp);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        var source = PresentationSource.FromVisual(Viewport);
        if (source?.CompositionTarget != null)
            screen = source.CompositionTarget.TransformFromDevice.Transform(screen);
        const double pad = 2;
        var l = SystemParameters.VirtualScreenLeft;
        var t = SystemParameters.VirtualScreenTop;
        var r = l + SystemParameters.VirtualScreenWidth;
        var b = t + SystemParameters.VirtualScreenHeight;
        return screen.X <= l + pad || screen.X >= r - pad - 1
            || screen.Y <= t + pad || screen.Y >= b - pad - 1;
    }

    /// <summary>放手后按释放速度滑行，碰到 0 / 图宽反弹。</summary>
    private void StepCoast(double dt)
    {
        _wipeV *= Math.Exp(-CoastDamp * dt);
        _lineX += _wipeV * dt;
        var w = _contentWidth;
        var bounced = false;
        if (_lineX < 0)
        {
            _lineX = Math.Min(-_lineX, w);
            _wipeV = -_wipeV * BounceRestitution;
            bounced = true;
        }
        else if (_lineX > w)
        {
            _lineX = Math.Max(2 * w - _lineX, 0);
            _wipeV = -_wipeV * BounceRestitution;
            bounced = true;
        }
        if (Math.Abs(_wipeV) > 12)
            _stretchDir = Math.Sign(_wipeV);
        if (!bounced && Math.Abs(_wipeV) < CoastMinV)
            _wipeV = 0;
    }

    private void StepFilm(double dt)
    {
        var w2 = FilmOmega * FilmOmega;
        var drive = Math.Abs(_wipeV) > 28 ? BetaV * Math.Abs(_wipeV) : 0;
        var epsA = -w2 * _eps - 2 * FilmZeta * FilmOmega * _epsV + drive;
        _epsV += epsA * dt;
        _eps += _epsV * dt;
        _eps = Math.Clamp(_eps, 0, MaxStrain);
    }

    private void PushWipePose()
    {
        _layoutFromSim = true;
        WipeRatio = Math.Clamp(_lineX / _contentWidth, 0, 1);
        _layoutFromSim = false;
        LayoutWipe(_lineX);
    }
}
