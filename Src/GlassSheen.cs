using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace VcsTextureCompare;

/// <summary>肥皂膜：透光填充、干涉色、Fresnel 边缘；太阳是上半球一颗小镜面点。</summary>
public sealed class GlassSheen : Border
{
    private const double DaySeconds = 30;
    private static TimeSpan? s_clock;
    private static double s_elapsed;

    private readonly Border _fill;
    private readonly Border _film;
    private readonly Border _env;
    private readonly Ellipse _fringeC;
    private readonly Ellipse _fringeM;
    private readonly Ellipse _spec;
    private readonly Ellipse _fillLight;
    private readonly Border _rim;
    private readonly EventHandler _onRender;
    private readonly RotateTransform _specRot = new();
    private readonly RotateTransform _fringeCRot = new();
    private readonly RotateTransform _fringeMRot = new();
    private readonly RotateTransform _capRot = new();
    private double _angle;

    public GlassSheen()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        _onRender = OnRender;
        _fill = Layer(new SolidColorBrush(Color.FromArgb(0x12, 255, 255, 255)));
        _film = Layer(FilmBrush());
        _env = Layer(EnvBrush());
        _fringeC = Speck(FringeBrush(Color.FromArgb(0x3A, 0x70, 0xF0, 0xFF)));
        _fringeM = Speck(FringeBrush(Color.FromArgb(0x30, 0xFF, 0x70, 0xD0)));
        _spec = Speck(SpecBrush());
        _fillLight = Speck(SkyCapBrush());
        BindRot(_spec, _specRot);
        BindRot(_fringeC, _fringeCRot);
        BindRot(_fringeM, _fringeMRot);
        BindRot(_fillLight, _capRot);
        _rim = new Border
        {
            IsHitTestVisible = false,
            Background = Brushes.Transparent,
            BorderBrush = RimBrush(),
            BorderThickness = new Thickness(1.35)
        };
        Child = new Grid
        {
            IsHitTestVisible = false,
            Children =
            {
                _fill, _film, _env,
                new Grid { IsHitTestVisible = false, Children = { _fringeC, _fringeM, _spec, _fillLight } },
                _rim
            }
        };
        SizeChanged += (_, _) =>
        {
            ApplyClip();
            LayoutLights();
        };
        Loaded += (_, _) =>
        {
            ApplyClip();
            LayoutLights();
            StartFlow();
            CompositionTarget.Rendering += _onRender;
        };
        Unloaded += (_, _) => CompositionTarget.Rendering -= _onRender;
        ApplyRadius();
    }

    /// <summary>干涉色绕膜慢转；太阳位置由共享时钟驱动，不在这里转。</summary>
    private void StartFlow()
    {
        if (_film.Background is RadialGradientBrush film)
        {
            var filmSpin = new RotateTransform(0, 0.5, 0.5);
            film.RelativeTransform = filmSpin;
            filmSpin.BeginAnimation(RotateTransform.AngleProperty, Spin(12));
            film.BeginAnimation(
                RadialGradientBrush.GradientOriginProperty,
                OrbitPoint(new Point(0.5, 0.5), 0.18, 7.2));
        }
        if (_rim.BorderBrush is LinearGradientBrush rim)
        {
            var rimSpin = new RotateTransform(0, 0.5, 0.5);
            rim.RelativeTransform = rimSpin;
            rimSpin.BeginAnimation(RotateTransform.AngleProperty, Spin(9.5));
        }
    }

    private void OnRender(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        s_clock ??= now - TimeSpan.FromSeconds(DaySeconds * 0.25);
        s_elapsed = (now - s_clock.Value).TotalSeconds;
        LayoutLights();
    }

    /// <summary>整天一条连续弧：白天在上半球，夜里从底下绕回，避免 0/1 处瞬移。</summary>
    private static void SampleSun(out double u, out double v, out double intensity, out double elev, out double angleDeg)
    {
        var t = s_elapsed / DaySeconds;
        t -= Math.Floor(t);
        if (t < 0)
            t += 1;
        var theta = (t - 0.25) * 2 * Math.PI;
        u = 0.5 - 0.18 * Math.Cos(theta);
        v = 0.18 - 0.07 * Math.Sin(theta);
        intensity = Math.Max(0, Math.Sin(theta));
        elev = intensity;
        var du = 0.18 * Math.Sin(theta);
        var dv = -0.07 * Math.Cos(theta);
        angleDeg = Math.Atan2(dv, du) * 180 / Math.PI;
    }

    private void LayoutLights()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 2 || h < 2)
            return;
        SampleSun(out var u, out var v, out var intensity, out var elev, out var angle);
        angle = UnwrapAngle(_angle, angle);
        _angle = angle;
        var m = Math.Min(w, h);
        var grazing = 1 - elev;
        var spec = Math.Max(4, m * 0.10);
        var specStretch = 1 + 1.35 * grazing;
        Place(_spec, w * u, h * v, spec * specStretch, spec / specStretch);
        _specRot.Angle = angle;
        var fringeStretch = specStretch * 1.35;
        var rad = angle * Math.PI / 180;
        var px = Math.Cos(rad) * m * 0.016;
        var py = Math.Sin(rad) * m * 0.016;
        Place(_fringeC, w * u - px, h * v - py, spec * fringeStretch, spec / fringeStretch);
        Place(_fringeM, w * u + px, h * v + py, spec * fringeStretch * 0.92, spec / (fringeStretch * 0.92));
        _fringeCRot.Angle = angle;
        _fringeMRot.Angle = angle;
        var capU = 0.5 + (u - 0.5) * 0.45;
        var capW = Math.Max(w * (0.48 + 0.28 * grazing), 16);
        var capH = Math.Max(h * (0.18 + 0.12 * elev), 7);
        Place(_fillLight, w * capU, Math.Max(capH * 0.55, h * (0.20 + 0.04 * grazing)), capW, capH);
        _capRot.Angle = (u - 0.5) * 42;
        _spec.Opacity = intensity;
        _fringeC.Opacity = intensity * 0.85;
        _fringeM.Opacity = intensity * 0.85;
        _env.Opacity = 1;
        if (_env.Background is RadialGradientBrush env)
        {
            env.GradientOrigin = new Point(0.5 + (u - 0.5) * 0.22, 0.18 + 0.06 * grazing);
            env.Center = new Point(0.5 + (u - 0.5) * 0.12, 0.14);
            env.RadiusX = 0.55 + 0.28 * grazing;
            env.RadiusY = 0.26 + 0.16 * elev;
        }
    }

    private static void Place(Ellipse speck, double cx, double cy, double width, double height)
    {
        speck.Width = width;
        speck.Height = height;
        speck.Margin = new Thickness(cx - width / 2, cy - height / 2, 0, 0);
    }

    private static double UnwrapAngle(double prev, double next)
    {
        var d = next - prev;
        while (d > 180)
            d -= 360;
        while (d < -180)
            d += 360;
        return prev + d;
    }

    private static DoubleAnimation Spin(double seconds) => new(0, 360, TimeSpan.FromSeconds(seconds))
    {
        RepeatBehavior = RepeatBehavior.Forever
    };

    /// <summary>点绕圆心慢转一圈，驱动边缘色相环流。</summary>
    private static PointAnimationUsingKeyFrames OrbitPoint(Point center, double radius, double seconds)
    {
        var anim = new PointAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(seconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        for (var i = 0; i <= 8; i++)
        {
            var t = i / 8.0;
            var rad = t * Math.PI * 2;
            anim.KeyFrames.Add(new LinearPointKeyFrame(
                new Point(center.X + Math.Cos(rad) * radius, center.Y + Math.Sin(rad) * radius),
                KeyTime.FromPercent(t)));
        }
        return anim;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == CornerRadiusProperty)
            ApplyRadius();
    }

    private void ApplyRadius()
    {
        if (_fill == null)
            return;
        var radius = CornerRadius;
        _fill.CornerRadius = radius;
        _film.CornerRadius = radius;
        _env.CornerRadius = radius;
        _rim.CornerRadius = radius;
        ApplyClip();
    }

    /// <summary>按圆角裁，膜和底同一轮廓，高光也不画出泡外。</summary>
    private void ApplyClip()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 1 || h < 1)
        {
            Clip = null;
            return;
        }
        var r = Math.Min(CornerRadius.TopLeft, Math.Min(w, h) * 0.5);
        Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    private static Border Layer(Brush background) => new()
    {
        IsHitTestVisible = false,
        Background = background
    };

    private static Ellipse Speck(Brush fill) => new()
    {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false,
        Fill = fill
    };

    private static void BindRot(Ellipse speck, RotateTransform rot)
    {
        speck.RenderTransformOrigin = new Point(0.5, 0.5);
        speck.RenderTransform = rot;
    }

    /// <summary>天光倒影：顶上一条扁白帽，肥皂泡照片里最常见的那块。</summary>
    private static RadialGradientBrush EnvBrush() => new()
    {
        GradientOrigin = new Point(0.5, 0.04),
        Center = new Point(0.5, 0.0),
        RadiusX = 0.72,
        RadiusY = 0.42,
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x38, 255, 255, 255), 0),
            new GradientStop(Color.FromArgb(0x14, 230, 245, 255), 0.45),
            new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 1)
        }
    };

    /// <summary>太阳倒影：针尖白热，边缘很快掉光。</summary>
    private static RadialGradientBrush SpecBrush() => new()
    {
        GradientOrigin = new Point(0.42, 0.36),
        Center = new Point(0.5, 0.48),
        RadiusX = 0.38,
        RadiusY = 0.40,
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0xFF, 255, 255, 255), 0),
            new GradientStop(Color.FromArgb(0xC0, 255, 255, 255), 0.12),
            new GradientStop(Color.FromArgb(0x40, 255, 255, 255), 0.38),
            new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 1)
        }
    };

    /// <summary>薄膜色散：高光两侧一点青/品红。</summary>
    private static RadialGradientBrush FringeBrush(Color tint) => new()
    {
        GradientOrigin = new Point(0.5, 0.45),
        Center = new Point(0.5, 0.5),
        RadiusX = 0.7,
        RadiusY = 0.7,
        GradientStops =
        {
            new GradientStop(tint, 0),
            new GradientStop(Color.FromArgb(0x00, tint.R, tint.G, tint.B), 1)
        }
    };

    /// <summary>顶上天光窗倒影，扁椭圆贴在上缘。</summary>
    private static RadialGradientBrush SkyCapBrush() => new()
    {
        GradientOrigin = new Point(0.5, 0.25),
        Center = new Point(0.5, 0.35),
        RadiusX = 0.72,
        RadiusY = 0.85,
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x55, 255, 255, 255), 0),
            new GradientStop(Color.FromArgb(0x22, 255, 255, 255), 0.45),
            new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 1)
        }
    };

    /// <summary>中心透明，往边缘走青、绿、蓝、紫，像薄膜干涉。</summary>
    private static RadialGradientBrush FilmBrush() => new()
    {
        GradientOrigin = new Point(0.32, 0.18),
        Center = new Point(0.4, 0.3),
        RadiusX = 0.9,
        RadiusY = 0.95,
        GradientStops =
        {
            new GradientStop(Colors.Transparent, 0),
            new GradientStop(Color.FromArgb(0x10, 0x80, 0xF0, 0xFF), 0.35),
            new GradientStop(Color.FromArgb(0x38, 0x40, 0xE8, 0x80), 0.62),
            new GradientStop(Color.FromArgb(0x66, 0x30, 0x90, 0xFF), 0.82),
            new GradientStop(Color.FromArgb(0x88, 0xC0, 0x40, 0xFF), 0.94),
            new GradientStop(Color.FromArgb(0x55, 0xFF, 0x70, 0xC8), 1)
        }
    };

    /// <summary>掠射 Fresnel：轮廓更亮，带一点冷白。</summary>
    private static LinearGradientBrush RimBrush() => new()
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 1),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0xEE, 0xF4, 0xFC, 0xFF), 0),
            new GradientStop(Color.FromArgb(0xCC, 0x80, 0xF0, 0xFF), 0.4),
            new GradientStop(Color.FromArgb(0xDD, 0xFF, 0x80, 0xD8), 1)
        }
    };
}
