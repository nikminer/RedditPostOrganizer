using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Gallery.Core;

namespace Gallery.Desktop;

public sealed partial class App : Application
{
    private GalleryService? service;
    private TrayIcon? tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var root =
                Program.Argument("--data-dir")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RedditGallery"
                );
            service = new(new Store(root));
            var window = new MainWindow(service);
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var menu = new NativeMenu();
            var open = new NativeMenuItem("Открыть Reddit Post Organizer");
            open.Click += (_, _) =>
            {
                window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();
            };
            var exit = new NativeMenuItem("Выйти");
            exit.Click += async (_, _) => await window.Quit();
            menu.Add(open);
            menu.Add(exit);
            using var bitmap = new RenderTargetBitmap(new PixelSize(32, 32));
            using (var drawing = bitmap.CreateDrawingContext())
            {
                drawing.FillRectangle(Brush.Parse("#3559D8"), new Rect(0, 0, 32, 32));
                drawing.FillRectangle(Brushes.White, new Rect(8, 8, 16, 4));
                drawing.FillRectangle(Brushes.White, new Rect(8, 12, 4, 12));
                drawing.FillRectangle(Brushes.White, new Rect(8, 20, 16, 4));
            }
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            stream.Position = 0;
            tray = new()
            {
                Icon = new WindowIcon(stream),
                ToolTipText = "Reddit Post Organizer",
                Menu = menu,
                IsVisible = true,
            };
            TrayIcon.SetIcons(this, new TrayIcons { tray });
            window.Opened += (_, _) =>
            {
                if (Program.Arguments.Contains("--tray"))
                    window.Hide();
            };
            desktop.Exit += (_, _) => tray.Dispose();
            if (Program.Arguments.Contains("--smoke-test"))
            {
                Avalonia.Threading.DispatcherTimer.RunOnce(
                    async () =>
                    {
                        File.WriteAllText(
                            Path.Combine(root, "smoke-test.json"),
                            Json.Write(
                                new
                                {
                                    success = true,
                                    framework = "Avalonia",
                                    version = "3.0.0",
                                    posts = service.Store.Posts().Count,
                                }
                            )
                        );
                        await window.Quit();
                    },
                    TimeSpan.FromSeconds(3)
                );
            }
            else
                service.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
