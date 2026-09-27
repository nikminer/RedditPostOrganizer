using System.Security.Cryptography;
using System.Text;
using Avalonia;

namespace Gallery.Desktop;

internal static class Program
{
    public static string[] Arguments = [];

    [STAThread]
    public static void Main(string[] args)
    {
        Arguments = args;
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture =
            System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture =
            System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        try
        {
            var root =
                Argument("--data-dir")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RedditGallery"
                );
            Directory.CreateDirectory(root);
            if (args.Contains("--smoke-test") && Argument("--data-dir") is null)
                throw new ArgumentException("Smoke test requires an isolated --data-dir");
            var name =
                "Local\\RedditGallery-"
                + Convert.ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToLowerInvariant())
                    )
                )[..24];
            using var mutex = new Mutex(true, name, out var created);
            if (!created)
                return;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            var root =
                Argument("--data-dir")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RedditGallery"
                );
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "startup-error.log"), e.ToString());
            Environment.ExitCode = 1;
        }
    }

    public static string? Argument(string name)
    {
        var index = Array.IndexOf(Arguments, name);
        return index >= 0 && index + 1 < Arguments.Length ? Arguments[index + 1] : null;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
