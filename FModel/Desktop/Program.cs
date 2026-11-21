using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Styling;
using Avalonia.Headless;
using CUE4Parse_Conversion.Textures;
using Serilog;

namespace FModel.Desktop;

public static class Program
{
    private static bool _headless;
    [STAThread]
    public static int Main(string[] args)
    {
        Directory.CreateDirectory(DesktopSettings.DataDirectory);
        Log.Logger = new LoggerConfiguration().MinimumLevel.Information()
            .WriteTo.Sink(Editor.ImGuiSink.Instance)
            .WriteTo.File(Path.Combine(DesktopSettings.DataDirectory, "Logs", "FModel-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();
        TextureDecoder.UseAssetRipperTextureDecoder = true;
        try
        {
            if (args.FirstOrDefault() == "--self-test") return SmokeTests.Run(args.Skip(1).ToArray()).GetAwaiter().GetResult();
            if (args.FirstOrDefault() == "--validate-game") return GameValidation.Run(args.Skip(1).ToArray()).GetAwaiter().GetResult();
            if (args.FirstOrDefault() == "--snooper-smoke-test") return SnooperSmoke.Run(args.Skip(1).ToArray());
            if (args.FirstOrDefault() == "--snooper-inventory") return SnooperInventory.Run(args.Skip(1).ToArray());
            _headless = args.Contains("--ui-smoke-test") && args.Contains("--headless");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception e) { Log.Fatal(e, "FModel failed"); Console.Error.WriteLine(e); return 1; }
        finally { Log.CloseAndFlush(); }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<DesktopApp>();
        builder = _headless ? builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }) : builder.UsePlatformDetect();
        return builder.With(new Avalonia.Media.FontManagerOptions { DefaultFamilyName = "avares://FModel/Resources/Fonts#Selawik" }).LogToTrace();
    }
}

public sealed class DesktopApp : Application
{
    public override void Initialize()
    {
        Name = "FModel";
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        DesktopFonts.Initialize();
        Resources["FModelUiFont"] = DesktopFonts.Interface;
        Resources["FModelCodeFont"] = DesktopFonts.Code;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var viewerIndex = Array.IndexOf(desktop.Args ?? [], "--viewer-smoke-test");
            desktop.MainWindow = viewerIndex >= 0 ? new ModelWindow(desktop.Args![viewerIndex + 1]) : new MainWindow();
            if (desktop.MainWindow is ModelWindow modelWindow) modelWindow.Viewport.CapturePath = Path.Combine(DesktopSettings.DataDirectory, "viewer-smoke-test.png");
            if (desktop.Args?.Contains("--ui-smoke-test") == true)
            {
                Avalonia.Threading.DispatcherTimer.RunOnce(async () =>
                {
                    try
                    {
                        var window = desktop.MainWindow;
                        if (desktop.Args.ElementAtOrDefault(1) is { } fixtureDirectory && (Directory.Exists(fixtureDirectory) || File.Exists(fixtureDirectory)))
                        {
                            await ((MainWindow)window).PopulateSmokeTest(fixtureDirectory);
                            await Task.Delay(250);
                        }
                        if (!window.IsVisible || window.Bounds.Width < 800) throw new Exception("Main window did not open.");
                        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                        bitmap.Render(window);
                        bitmap.Save(Path.Combine(DesktopSettings.DataDirectory, "ui-smoke-test.png"));
                        Console.WriteLine(desktop.Args.Contains("--headless") ? "PASS: headless Avalonia window rendered with Skia." : "PASS: native window opened and rendered.");
                        desktop.Shutdown();
                    }
                    catch (Exception e) { Console.Error.WriteLine(e); desktop.Shutdown(1); }
                }, TimeSpan.FromSeconds(3));
            }
            if (desktop.Args?.FirstOrDefault() == "--ui-snooper-smoke-test")
            {
                Avalonia.Threading.DispatcherTimer.RunOnce(async () =>
                {
                    try
                    {
                        var args = desktop.Args;
                        await ((MainWindow)desktop.MainWindow).PopulateSnooperSmoke(args[1], args[2], args[3]);
                        Console.WriteLine("PASS: Avalonia browser and original Snooper share the native main-thread event loop.");
                        desktop.Shutdown();
                    }
                    catch (Exception error) { Console.Error.WriteLine(error); desktop.Shutdown(1); }
                }, TimeSpan.FromSeconds(1));
            }
            if (viewerIndex >= 0)
            {
                Avalonia.Threading.DispatcherTimer.RunOnce(() =>
                {
                    var viewer = (ModelWindow)desktop.MainWindow;
                    if (!viewer.Viewport.Rendered || !viewer.Viewport.CapturedGeometry) { Console.Error.WriteLine("FAIL: 3D viewport did not render geometry."); desktop.Shutdown(1); return; }
                    Console.WriteLine("PASS: 3D viewport rendered on " + viewer.Viewport.Renderer);
                    desktop.Shutdown();
                }, TimeSpan.FromSeconds(4));
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
