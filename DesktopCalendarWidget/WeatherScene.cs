using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DesktopCalendarWidget.Weather;

namespace DesktopCalendarWidget;

/// <summary>在透明日历窗口上绘制当天天气；不填充窗口底色。</summary>
internal sealed class WeatherScene
{
    private readonly Grid layer;
    private readonly Canvas particles;
    private readonly Rectangle flash;
    private readonly List<Storyboard> running = [];
    private readonly Random random = new(20260923);

    private WeatherCondition? current;
    private double builtForHeight;
    private double builtForWidth;

    public WeatherScene(Grid layer, Canvas particles, Rectangle flash)
    {
        this.layer = layer;
        this.particles = particles;
        this.flash = flash;
        flash.Fill = Brushes.White;
    }

    /// <summary>按当天条件重建粒子；无天气时保持完全透明。</summary>
    public void Apply(WeatherCondition? condition)
    {
        current = condition is null or WeatherCondition.Unknown ? null : condition;
        Rebuild();
    }

    public void RefreshLayout()
    {
        if (Math.Abs(layer.ActualHeight - builtForHeight) < 40 &&
            Math.Abs(layer.ActualWidth - builtForWidth) < 40) return;
        Rebuild();
    }

    public void Stop()
    {
        ClearParticles();
        current = null;
        particles.Visibility = Visibility.Collapsed;
    }

    private double Width => layer.ActualWidth > 1 ? layer.ActualWidth : 640;
    private double Height => layer.ActualHeight > 1 ? layer.ActualHeight : 820;

    private void Rebuild()
    {
        ClearParticles();
        builtForHeight = layer.ActualHeight;
        builtForWidth = layer.ActualWidth;
        particles.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
        if (current is { } condition) Build(condition);
    }

    private void ClearParticles()
    {
        foreach (var storyboard in running) storyboard.Stop(layer);
        running.Clear();
        foreach (UIElement child in particles.Children)
        {
            child.BeginAnimation(UIElement.OpacityProperty, null);
            if (child.RenderTransform is TranslateTransform transform)
            {
                transform.BeginAnimation(TranslateTransform.XProperty, null);
                transform.BeginAnimation(TranslateTransform.YProperty, null);
            }
        }
        particles.Children.Clear();
        flash.BeginAnimation(UIElement.OpacityProperty, null);
        flash.Opacity = 0;
    }

    private void Build(WeatherCondition condition)
    {
        var w = Width;
        var h = Height;
        switch (condition)
        {
            case WeatherCondition.Clear:
                AddSun(w, 0.85);
                break;
            case WeatherCondition.PartlyCloudy:
                AddSun(w, 0.65);
                AddClouds(w, h, 3);
                break;
            case WeatherCondition.Overcast:
                AddClouds(w, h, 5);
                break;
            case WeatherCondition.Fog:
                AddFog(w, h);
                break;
            case WeatherCondition.Drizzle:
                AddRain(w, h, 36, 20, 1.1, 1.6);
                break;
            case WeatherCondition.Rain:
                AddRain(w, h, 54, 32, 0.75, 1.15);
                break;
            case WeatherCondition.HeavyRain:
                AddRain(w, h, 72, 44, 0.5, 0.8);
                break;
            case WeatherCondition.Snow:
                AddSnow(w, h, 38);
                break;
            case WeatherCondition.Thunderstorm:
                AddRain(w, h, 64, 40, 0.55, 0.9);
                AddFlash();
                break;
        }
    }

    private void AddSun(double w, double strength)
    {
        var glow = new Ellipse
        {
            Width = 280,
            Height = 280,
            Fill = new RadialGradientBrush(
                Color.FromArgb(0xF0, 0xFF, 0xB8, 0x45),
                Color.FromArgb(0x00, 0xFF, 0xD9, 0x8A))
        };
        Canvas.SetLeft(glow, w - 190);
        Canvas.SetTop(glow, -55);
        particles.Children.Add(glow);
        Animate(glow, UIElement.OpacityProperty, strength * 0.65, strength, 5.5, 0);
    }

    private void AddClouds(double w, double h, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var scale = 0.8 + random.NextDouble() * 0.7;
            var cloud = new Ellipse
            {
                Width = 190 * scale,
                Height = 54 * scale,
                Fill = new SolidColorBrush(i % 2 == 0
                    ? Color.FromArgb(0xB0, 0x78, 0x8C, 0x9A)
                    : Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)),
                Effect = new BlurEffect { Radius = 16 }
            };
            Canvas.SetTop(cloud, h * (0.08 + 0.16 * i) + random.Next(0, 30));
            particles.Children.Add(cloud);
            Animate(cloud, TranslateTransform.XProperty, -cloud.Width - 20, w + 40,
                24 + random.Next(0, 18), random.Next(0, 35));
        }
    }

    private void AddFog(double w, double h)
    {
        for (var i = 0; i < 4; i++)
        {
            var band = new Rectangle
            {
                Width = w * 1.2,
                Height = 45 + random.Next(0, 45),
                Fill = new SolidColorBrush(i % 2 == 0
                    ? Color.FromArgb(0x7A, 0x91, 0xA6, 0xAA)
                    : Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)),
                Effect = new BlurEffect { Radius = 20 }
            };
            Canvas.SetTop(band, h * (0.2 + 0.18 * i));
            particles.Children.Add(band);
            Animate(band, TranslateTransform.XProperty, -band.Width * 0.2, w * 0.2,
                32 + random.Next(0, 18), random.Next(0, 35));
        }
    }

    private void AddRain(double w, double h, int count, double length, double minSeconds, double maxSeconds)
    {
        for (var i = 0; i < count; i++)
        {
            var drop = new Rectangle
            {
                Width = 2.2,
                Height = length * (0.8 + random.NextDouble() * 0.4),
                Fill = new SolidColorBrush(i % 2 == 0
                    ? Color.FromArgb(0xD8, 0x3C, 0x86, 0xB0)
                    : Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)),
                RadiusX = 1,
                RadiusY = 1
            };
            Canvas.SetLeft(drop, random.NextDouble() * w);
            particles.Children.Add(drop);
            var seconds = minSeconds + random.NextDouble() * (maxSeconds - minSeconds);
            Animate(drop, TranslateTransform.YProperty, -drop.Height, h + 10, seconds, random.NextDouble() * seconds);
        }
    }

    private void AddSnow(double w, double h, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var size = 4 + random.NextDouble() * 4;
            var flake = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)),
                Stroke = new SolidColorBrush(Color.FromArgb(0xB0, 0x77, 0x9A, 0xB2)),
                StrokeThickness = 0.7
            };
            Canvas.SetLeft(flake, random.NextDouble() * w);
            particles.Children.Add(flake);
            var seconds = 5 + random.NextDouble() * 4;
            Animate(flake, TranslateTransform.YProperty, -size, h + 10, seconds, random.NextDouble() * seconds);
            Animate(flake, TranslateTransform.XProperty, -22, 22, 2.5 + random.NextDouble() * 2, 0, autoReverse: true);
        }
    }

    /// <summary>雷暴：9 秒循环内两次短闪光，其余时间完全透明。</summary>
    private void AddFlash()
    {
        var frames = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
            Duration = TimeSpan.FromSeconds(9)
        };
        AddFrame(frames, 0, 0);
        AddFrame(frames, 0, 3.0);
        AddFrame(frames, 0.38, 3.04);
        AddFrame(frames, 0, 3.16);
        AddFrame(frames, 0, 5.6);
        AddFrame(frames, 0.48, 5.64);
        AddFrame(frames, 0, 5.78);
        AddFrame(frames, 0, 9);
        flash.BeginAnimation(UIElement.OpacityProperty, frames);
    }

    private static void AddFrame(DoubleAnimationUsingKeyFrames frames, double value, double seconds) =>
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds))));

    private void Animate(
        UIElement element,
        DependencyProperty property,
        double from,
        double to,
        double seconds,
        double seekSeconds,
        bool autoReverse = false)
    {
        if (element.RenderTransform is not TranslateTransform) element.RenderTransform = new TranslateTransform();
        var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = autoReverse
        };
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, new PropertyPath(PathOf(property)));
        storyboard.Begin(layer, true);
        if (seekSeconds > 0) storyboard.Seek(layer, TimeSpan.FromSeconds(seekSeconds), TimeSeekOrigin.BeginTime);
        running.Add(storyboard);
    }

    private static string PathOf(DependencyProperty property)
    {
        if (property == TranslateTransform.XProperty) return "(UIElement.RenderTransform).(TranslateTransform.X)";
        if (property == TranslateTransform.YProperty) return "(UIElement.RenderTransform).(TranslateTransform.Y)";
        if (property == UIElement.OpacityProperty) return "Opacity";
        throw new ArgumentOutOfRangeException(nameof(property), property, "unsupported animated property");
    }
}
