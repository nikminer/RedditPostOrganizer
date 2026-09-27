using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RedditPostOrganizer.Core;
using RedditPostOrganizer.Desktop;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(RedditPostOrganizer.Tests.TestAppBuilder))]

namespace RedditPostOrganizer.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class UiTests
{
    [AvaloniaFact]
    public async Task CalendarViewsAndPopupColorsRender()
    {
        using var f = new Fixture();
        await using var service = new PublicationService(f.Store);
        f.Store.Save(f.Content(), Clock.Now + 3600);
        f.Store.ImportPost(
            "abc",
            f.Content(),
            Clock.Now,
            "https://www.reddit.com/r/test/comments/abc/title/"
        );
        var window = new MainWindow(service);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var view = window.FindControl<ComboBox>("ViewMode")!;
        for (int i = 0; i < 4; i++)
        {
            view.SelectedIndex = i;
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<ListBox>("PostList")!.IsVisible == (i == 3));
        }
        view.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        var output = Path.Combine(AppContext.BaseDirectory, "ui-artifacts");
        Directory.CreateDirectory(output);
        window.CaptureRenderedFrame()!.Save(Path.Combine(output, "calendar.png"));
        view.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.IsDropDownOpen);
        var popup = view.GetVisualDescendants()
            .OfType<Avalonia.Controls.Primitives.Popup>()
            .FirstOrDefault();
        if (popup?.Child is Control popupChild)
            SaveControl(popupChild, Path.Combine(output, "dropdown.png"));
        var item = (ComboBoxItem)view.Items[0]!;
        Assert.Equal(Color.Parse("#1D2940"), ((ISolidColorBrush)item.Foreground!).Color);
        view.IsDropDownOpen = false;
        var menu = new ContextMenu();
        var menuItem = new MenuItem { Header = "Создать копию" };
        menu.Items.Add(menuItem);
        window.ContextMenu = menu;
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Color.Parse("#FFFFFF"), ((ISolidColorBrush)menu.Background!).Color);
        Assert.Equal(Color.Parse("#17263E"), ((ISolidColorBrush)menuItem.Foreground!).Color);
        SaveControl(menu, Path.Combine(output, "context-menu.png"));
        menu.Close();
        var editor = new EditorWindow(service, f.Store.Posts().First(p => p.Editable));
        editor.Show(window);
        Dispatcher.UIThread.RunJobs();
        editor.CaptureRenderedFrame()!.Save(Path.Combine(output, "editor.png"));
        editor.Close();
        window.Hide();
    }

    private static void SaveControl(Control control, string path)
    {
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new PixelSize(
                Math.Max(1, (int)control.Bounds.Width),
                Math.Max(1, (int)control.Bounds.Height)
            )
        );
        bitmap.Render(control);
        bitmap.Save(path);
    }
}
