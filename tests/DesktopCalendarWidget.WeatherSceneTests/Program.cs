using DesktopCalendarWidget;
using DesktopCalendarWidget.Weather;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

internal static class Program
{
    private static int tests;
    private static int failures;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var layer = new Grid { IsHitTestVisible = false, ClipToBounds = true };
        var particles = new Canvas { Opacity = 0.84 };
        var flash = new Rectangle { Stretch = Stretch.Fill, Fill = Brushes.White, Opacity = 0 };
        layer.Children.Add(particles);
        layer.Children.Add(flash);

        var window = new Window
        {
            Title = "WeatherScene integration tests",
            Width = 520,
            Height = 620,
            Content = layer
        };
        var scene = new WeatherScene(layer, particles, flash);
        window.Loaded += async (_, _) =>
        {
            try
            {
                await RunTestsAsync(window, layer, particles, flash, scene);
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL test host: {ex}");
            }
            finally
            {
                scene.Stop();
                window.Close();
            }
        };

        app.Run(window);
        Console.WriteLine($"Tests: {tests}, failures: {failures}");
        return tests == 7 && failures == 0 ? 0 : 1;
    }

    private static async Task RunTestsAsync(Window window, Grid layer,
        Canvas particles, Rectangle flash, WeatherScene scene)
    {
        Require(window.IsVisible && layer.ActualWidth > 1 && layer.ActualHeight > 1,
            "scene window must be shown and laid out");
        Require(layer.Children.Count == 2 && ReferenceEquals(layer.Children[0], particles)
            && ReferenceEquals(layer.Children[1], flash), "scene layer must contain only particles and flash");

        await RunAsync("scene remains transparent with no weather, rain and clear", () =>
        {
            RequireTransparent(layer, particles, "initial scene");
            scene.Apply(WeatherCondition.Rain);
            Require(particles.Children.Count > 0, "rain should create particles");
            RequireTransparent(layer, particles, "rain");
            scene.Apply(WeatherCondition.Clear);
            Require(particles.Children.Count > 0, "clear should create a sun");
            RequireTransparent(layer, particles, "clear");
            return Task.CompletedTask;
        });

        await RunAsync("clear sun opacity keeps moving after multiple cycles", async () =>
        {
            scene.Apply(WeatherCondition.Clear);
            var sun = particles.Children.OfType<Ellipse>().FirstOrDefault()
                ?? throw new InvalidOperationException("clear should create a sun ellipse");
            await RequireMotionAsync("sun opacity on start", () => sun.Opacity, 0.01,
                TimeSpan.FromMilliseconds(590), TimeSpan.FromMilliseconds(830));
            await DelayAsync(TimeSpan.FromSeconds(30.2));
            Require(particles.Children.Count > 0 && ReferenceEquals(particles.Children[0], sun),
                "clear particles should remain active");
            await RequireMotionAsync("sun opacity after 30 seconds", () => sun.Opacity, 0.01,
                TimeSpan.FromMilliseconds(590), TimeSpan.FromMilliseconds(830));
            RequireTransparent(layer, particles, "clear after multiple cycles");
        });

        await RunAsync("drizzle rain keeps moving after multiple cycles", async () =>
        {
            scene.Apply(WeatherCondition.Drizzle);
            Require(particles.Children.Count > 0, "drizzle should create rain");
            var drop = particles.Children[0];
            var transform = drop.RenderTransform as TranslateTransform
                ?? throw new InvalidOperationException("drizzle drop should have a translation");
            await RequireMotionAsync("rain position on start", () => transform.Y, 5,
                TimeSpan.FromMilliseconds(190), TimeSpan.FromMilliseconds(310));
            await DelayAsync(TimeSpan.FromSeconds(30.2));
            Require(particles.Children.Count > 0 && ReferenceEquals(particles.Children[0], drop),
                "drizzle particles should remain active");
            await RequireMotionAsync("rain position after 30 seconds", () => transform.Y, 5,
                TimeSpan.FromMilliseconds(190), TimeSpan.FromMilliseconds(310));
            RequireTransparent(layer, particles, "drizzle after multiple cycles");
        });

        await RunAsync("width-only resize rebuilds particle layout", async () =>
        {
            scene.Apply(WeatherCondition.Drizzle);
            var originalWidth = layer.ActualWidth;
            var originalHeight = layer.ActualHeight;
            Require(particles.Children.Count > 0, "drizzle should create particles before resize");
            var firstParticle = particles.Children[0];

            window.Width -= 140;
            await DelayAsync(TimeSpan.FromMilliseconds(150));
            window.UpdateLayout();
            Require(originalWidth - layer.ActualWidth > 80, "window width did not change enough");
            Require(Math.Abs(originalHeight - layer.ActualHeight) < 2,
                "test requires a width-only layout change");
            scene.RefreshLayout();
            Require(particles.Children.Count > 0, "resize should retain particles");
            Require(!ReferenceEquals(firstParticle, particles.Children[0]),
                "width-only resize must rebuild particle layout");
            RequireTransparent(layer, particles, "resized drizzle");
        });

        await RunAsync("thunderstorm flash is brief and normally transparent", async () =>
        {
            scene.Apply(WeatherCondition.Thunderstorm);
            Require(particles.Children.Count > 0, "thunderstorm should create rain");
            await DelayAsync(TimeSpan.FromSeconds(2.5));
            Require(flash.Opacity < 0.05, "flash should be transparent before the first pulse");
            var peak = 0d;
            var transparentSamples = 0;
            for (var i = 0; i < 70; i++)
            {
                await DelayAsync(TimeSpan.FromMilliseconds(20));
                var opacity = flash.Opacity;
                peak = Math.Max(peak, opacity);
                if (opacity < 0.05) transparentSamples++;
            }
            Require(peak > 0.05, $"expected a brief visible flash, peak={peak}");
            Require(transparentSamples > 40, "flash should be transparent most of the time");
            Require(flash.Opacity < 0.05, "flash should return to transparency after the pulse");
        });

        await RunAsync("stop clears the scene and apply restores it", async () =>
        {
            scene.Stop();
            Require(particles.Children.Count == 0, "stop should clear particles");
            Require(flash.Opacity < 0.01, "stop should clear flash opacity");
            RequireTransparent(layer, particles, "stopped scene");
            await DelayAsync(TimeSpan.FromSeconds(3.2));
            Require(flash.Opacity < 0.01, "stop should cancel the next flash pulse");

            scene.Apply(WeatherCondition.Drizzle);
            Require(particles.Visibility == Visibility.Visible && particles.Children.Count > 0,
                "apply should restore particles");
            RequireTransparent(layer, particles, "restored drizzle");
        });

        await RunAsync("null and Unknown leave no particles or opaque fill", () =>
        {
            foreach (var condition in new WeatherCondition?[] { null, WeatherCondition.Unknown })
            {
                scene.Apply(WeatherCondition.Rain);
                Require(particles.Children.Count > 0, "rain should create particles before clearing");
                scene.Apply(condition);
                Require(particles.Children.Count == 0, $"{condition?.ToString() ?? "null"} should clear particles");
                Require(flash.Opacity < 0.01, "empty scene should not flash");
                RequireTransparent(layer, particles, condition?.ToString() ?? "null");
            }
            return Task.CompletedTask;
        });
    }

    private static void RequireTransparent(Grid layer, Canvas particles, string label)
    {
        Require(layer.Background is null or SolidColorBrush { Color.A: 0 },
            $"{label}: layer must not have an opaque background");
        Require(particles.Background is null or SolidColorBrush { Color.A: 0 },
            $"{label}: particles must not have an opaque background");

        layer.UpdateLayout();
        var width = (int)Math.Ceiling(layer.ActualWidth);
        var height = (int)Math.Ceiling(layer.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(layer);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var transparentPoints = 0;
        for (var row = 0; row < 5; row++)
        {
            for (var column = 0; column < 5; column++)
            {
                var x = width * (2 * column + 1) / 10;
                var y = height * (2 * row + 1) / 10;
                if (pixels[(y * width + x) * 4 + 3] < 16) transparentPoints++;
            }
        }
        Require(transparentPoints >= 15,
            $"{label}: expected transparent bare areas across the scene ({transparentPoints}/25 points)");
    }

    private static async Task RequireMotionAsync(string label, Func<double> value,
        double minimumRange, TimeSpan firstInterval, TimeSpan secondInterval)
    {
        var first = value();
        await DelayAsync(firstInterval);
        var second = value();
        await DelayAsync(secondInterval);
        var third = value();
        var range = Math.Max(first, Math.Max(second, third)) - Math.Min(first, Math.Min(second, third));
        Require(range > minimumRange, $"{label} did not move (range={range})");
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        tests++;
        try
        {
            await test();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            failures++;
            Console.Error.WriteLine($"FAIL {name}: {ex}");
        }
    }

    private static Task DelayAsync(TimeSpan duration)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            completion.TrySetResult(true);
        };
        timer.Start();
        return completion.Task;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
