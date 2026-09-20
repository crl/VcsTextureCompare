using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VcsTextureCompare;

/// <summary>
/// 肥皂泡运动：质心弹簧带惯性过冲，膜应变由加速度驱动、表面张力拉回；面积守恒，停稳后写入本地值。
/// </summary>
internal sealed class BubbleMotion
{
    /// <summary>位置弹簧（px/s² per px），过冲来自欠阻尼而不是写死位移。</summary>
    private const double KPos = 180;
    private const double CPos = 15;
    private const double OmegaFilm = 22;
    private const double ZetaFilm = 0.42;
    /// <summary>应变对加速度的耦合；稳态 ε ≈ β a / ω²，再钳到 MaxStrain。</summary>
    private const double Beta = 0.007;
    private const double MaxStrain = 0.12;
    private const double SettlePos = 0.4;
    private const double SettleVel = 10;
    private const double SettleStrain = 0.004;
    private const double SettleStrainVel = 0.1;

    private static readonly Dictionary<FrameworkElement, BubbleMotion> Runs = new();

    private readonly EventHandler _onRender;
    private TranslateTransform _transform = null!;
    private FrameworkElement _bubble = null!;
    private bool _alongX;
    private double _sTarget;
    private double _restAlong;
    private double _restCross;
    private double _s;
    private double _v;
    private double _eps;
    private double _epsV;
    private TimeSpan _lastTime;
    private bool _hooked;

    private BubbleMotion() => _onRender = OnRender;

    /// <summary>把气泡送到目标槽。中途改选保留速度和形变。</summary>
    public static void Go(
        TranslateTransform transform,
        FrameworkElement bubble,
        bool alongX,
        double toOrigin,
        double toAlong,
        double toCross,
        bool animate)
    {
        toAlong = Math.Max(1, toAlong);
        toCross = Math.Max(1, toCross);
        if (!Runs.TryGetValue(bubble, out var run))
        {
            run = new BubbleMotion();
            Runs[bubble] = run;
        }
        run._transform = transform;
        run._bubble = bubble;
        run._alongX = alongX;
        run._restAlong = toAlong;
        run._restCross = toCross;
        run._sTarget = toOrigin + toAlong * 0.5;
        if (!animate)
        {
            run.Snap();
            return;
        }
        if (!run._hooked)
            run.CaptureFromVisual();
        if (Math.Abs(run._s - run._sTarget) < 1 && Math.Abs(run._v) < 1 && Math.Abs(run._eps) < 0.002)
        {
            run.Snap();
            return;
        }
        run.EnsureTick();
    }

    /// <summary>窗口关闭时停掉所有积分，避免 Rendering 泄漏。</summary>
    public static void StopAll()
    {
        foreach (var run in Runs.Values.ToList())
            run.Snap();
    }

    private void CaptureFromVisual()
    {
        ClearAnims();
        var origin = _alongX ? _transform.X : _transform.Y;
        var along = ReadSize(_bubble, _alongX, alongAxis: true, _restAlong);
        _s = origin + along * 0.5;
        _v = 0;
        _eps = Math.Clamp(along / _restAlong - 1, -MaxStrain, MaxStrain);
        _epsV = 0;
        _lastTime = TimeSpan.Zero;
    }

    private void EnsureTick()
    {
        if (_hooked)
            return;
        _hooked = true;
        _lastTime = TimeSpan.Zero;
        CompositionTarget.Rendering += _onRender;
    }

    private void OnRender(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_lastTime == TimeSpan.Zero)
        {
            _lastTime = now;
            ApplyPose();
            return;
        }
        var dt = (now - _lastTime).TotalSeconds;
        _lastTime = now;
        if (dt <= 0)
            return;
        if (dt > 0.05)
            dt = 0.05;
        var steps = Math.Max(1, (int)Math.Ceiling(dt / 0.008));
        var h = dt / steps;
        for (var i = 0; i < steps; i++)
            Step(h);

        if (Math.Abs(_s - _sTarget) < SettlePos
            && Math.Abs(_v) < SettleVel
            && Math.Abs(_eps) < SettleStrain
            && Math.Abs(_epsV) < SettleStrainVel)
        {
            Snap();
            return;
        }
        ApplyPose();
    }

    private void Step(double dt)
    {
        var a = KPos * (_sTarget - _s) - CPos * _v;
        _v += a * dt;
        _s += _v * dt;
        var w2 = OmegaFilm * OmegaFilm;
        var epsA = -w2 * _eps - 2 * ZetaFilm * OmegaFilm * _epsV + Beta * a;
        _epsV += epsA * dt;
        _eps += _epsV * dt;
        if (_eps > MaxStrain)
        {
            _eps = MaxStrain;
            if (_epsV > 0)
                _epsV = 0;
        }
        else if (_eps < -MaxStrain)
        {
            _eps = -MaxStrain;
            if (_epsV < 0)
                _epsV = 0;
        }
    }

    private void ApplyPose()
    {
        var along = _restAlong * (1 + _eps);
        var cross = _restAlong * _restCross / along;
        var restOrigin = _s - _restAlong * 0.5;
        var travel = Math.Abs(_v) > 4 ? _v : _sTarget - _s;
        var origin = travel >= 0
            ? restOrigin
            : restOrigin + _restAlong - along;
        var crossOff = (_restCross - cross) * 0.5;
        Write(origin, crossOff, along, cross);
    }

    private void Snap()
    {
        if (_hooked)
        {
            CompositionTarget.Rendering -= _onRender;
            _hooked = false;
        }
        ClearAnims();
        var origin = _sTarget - _restAlong * 0.5;
        Write(origin, 0, _restAlong, _restCross);
        _s = _sTarget;
        _v = 0;
        _eps = 0;
        _epsV = 0;
        Runs.Remove(_bubble);
    }

    private void Write(double origin, double crossOff, double along, double cross)
    {
        if (_alongX)
        {
            _transform.X = origin;
            _transform.Y = crossOff;
            _bubble.Width = along;
            _bubble.Height = cross;
        }
        else
        {
            _transform.Y = origin;
            _transform.X = crossOff;
            _bubble.Height = along;
            _bubble.Width = cross;
        }
    }

    private void ClearAnims()
    {
        _transform.BeginAnimation(TranslateTransform.XProperty, null);
        _transform.BeginAnimation(TranslateTransform.YProperty, null);
        _bubble.BeginAnimation(FrameworkElement.WidthProperty, null);
        _bubble.BeginAnimation(FrameworkElement.HeightProperty, null);
    }

    private static double ReadSize(FrameworkElement bubble, bool alongX, bool alongAxis, double fallback)
    {
        var horizontal = alongX == alongAxis;
        var v = horizontal ? bubble.Width : bubble.Height;
        if (!double.IsNaN(v) && v > 1)
            return v;
        var actual = horizontal ? bubble.ActualWidth : bubble.ActualHeight;
        return actual > 1 ? actual : fallback;
    }
}
