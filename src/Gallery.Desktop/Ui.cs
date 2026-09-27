using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Gallery.Core;

namespace Gallery.Desktop;

public static class Ui
{
    public static TextBlock Text(string text, double size = 14, string color = "#1D2940") =>
        new()
        {
            Text = text,
            FontSize = size,
            Foreground = Brush.Parse(color),
            TextWrapping = TextWrapping.Wrap,
        };

    public static Button Button(string text, Action action, bool primary = false)
    {
        var b = new Button { Content = text };
        if (primary)
            b.Classes.Add("primary");
        b.Click += (_, _) => action();
        return b;
    }

    public static StackPanel Row(params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in children)
            p.Children.Add(c);
        return p;
    }

    public static StackPanel Column(params Control[] children)
    {
        var p = new StackPanel { Spacing = 10 };
        foreach (var c in children)
            p.Children.Add(c);
        return p;
    }

    public static async Task<bool> Confirm(Window owner, string message)
    {
        var w = new Window
        {
            Title = "Gallery",
            Width = 500,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        w.Content = new Border
        {
            Padding = new Thickness(24),
            Child = Column(
                Text(message),
                Row(Button("Да", () => w.Close(true), true), Button("Отмена", () => w.Close(false)))
            ),
        };
        return await w.ShowDialog<bool>(owner);
    }

    public static async Task Info(Window owner, string message)
    {
        var w = new Window
        {
            Title = "Gallery",
            Width = 640,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        w.Content = new Border
        {
            Padding = new Thickness(24),
            Child = Column(
                new TextBox
                {
                    Text = message,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    Height = 235,
                },
                Button("Закрыть", () => w.Close())
            ),
        };
        await w.ShowDialog(owner);
    }

    public static async Task<string?> Ask(
        Window owner,
        string title,
        string prompt,
        string value = "",
        bool password = false
    )
    {
        var w = new Window
        {
            Title = title,
            Width = 540,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var input = new TextBox
        {
            Text = value,
            Watermark = prompt,
            PasswordChar = password ? '●' : '\0',
        };
        w.Content = new Border
        {
            Padding = new Thickness(24),
            Child = Column(
                Text(prompt),
                input,
                Row(
                    Button("OK", () => w.Close(input.Text ?? ""), true),
                    Button("Отмена", () => w.Close(null))
                )
            ),
        };
        return await w.ShowDialog<string?>(owner);
    }

    public static async Task<double> ParseTime(Window owner, string value, string zone)
    {
        if (
            !DateTime.TryParseExact(
                value,
                "dd.MM.yyyy HH:mm",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var local
            )
        )
            throw new InvalidOperationException("Введите дату и время: дд.мм.гггг чч:мм");
        var choices = Clock.Candidates(local, zone);
        if (choices.Length == 0)
            throw new InvalidOperationException("Такого времени нет из-за перевода часов");
        if (choices.Length == 2)
        {
            var second = await Confirm(
                owner,
                "Время встречается дважды при переводе часов. Выбрать второй, более поздний вариант? Нет — первый вариант."
            );
            return choices[second ? 1 : 0];
        }
        return choices[0];
    }

    public static async Task<string?> OpenFile(Window owner, string title, string extension)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(
            new()
            {
                Title = title,
                FileTypeFilter =
                [
                    new FilePickerFileType(extension) { Patterns = ["*." + extension] },
                ],
            }
        );
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task<string?> SaveFile(Window owner, string title, string name)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(
            new()
            {
                Title = title,
                SuggestedFileName = name,
                DefaultExtension = "zip",
                FileTypeChoices = [new FilePickerFileType("ZIP") { Patterns = ["*.zip"] }],
            }
        );
        return file?.TryGetLocalPath();
    }
}
