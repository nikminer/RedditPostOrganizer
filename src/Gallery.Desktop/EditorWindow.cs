using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Gallery.Core;

namespace Gallery.Desktop;

public sealed class EditorWindow : Window
{
    private readonly GalleryService service;
    private readonly Post? post;
    private readonly ObservableCollection<MediaItem> images;
    private readonly TextBox title = new(),
        community = new(),
        tags = new(),
        notes = new() { AcceptsReturn = true, Height = 70 },
        when = new();
    private readonly CheckBox nsfw = new() { Content = "NSFW / 18+" },
        scheduled = new() { Content = "Запланировать публикацию" };
    private readonly ComboBox flair = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox mediaList = new() { Height = 245 };
    private readonly TextBlock status = Ui.Text("");

    public EditorWindow(GalleryService service, Post? post = null, DateTime? date = null)
    {
        this.service = service;
        this.post = post;
        Title = post?.Status == "published" ? "Опубликованный пост" : "Редактор галереи";
        Width = 810;
        Height = 840;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var c = post?.Content.DeepCopy() ?? new PostContent();
        title.Text = c.Title;
        title.MaxLength = 300;
        community.Text = c.Community;
        community.Watermark = "Сообщество без r/";
        tags.Text = string.Join(", ", c.Tags);
        notes.Text = c.Notes;
        nsfw.IsChecked = c.Nsfw;
        scheduled.IsChecked = post?.Scheduled.HasValue == true && post.Editable;
        var zone = service.Store.Setting("timezone", "Europe/Moscow");
        when.Text =
            date?.ToString("dd.MM.yyyy HH:mm")
            ?? Clock.Display(post?.Scheduled ?? Clock.Now + 3600, zone);
        images = new(c.Images);
        mediaList.ItemsSource = images;
        mediaList.ItemTemplate = new FuncDataTemplate<MediaItem>(
            (item, _) =>
            {
                var row = Ui.Row();
                if (item is not null && !string.IsNullOrEmpty(item.File))
                {
                    try
                    {
                        row.Children.Add(
                            new Image
                            {
                                Source = new Bitmap(service.Store.MediaPath(item.File)),
                                Width = 86,
                                Height = 60,
                                Stretch = Stretch.UniformToFill,
                            }
                        );
                    }
                    catch { }
                }
                row.Children.Add(Ui.Text(item?.ToString() ?? ""));
                return row;
            }
        );
        mediaList.DoubleTapped += async (_, _) => await Caption();
        LoadFlairs(service.Store.Flairs(c.Community), c.FlairId, c.Flair);
        community.LostFocus += (_, _) =>
        {
            if (community.Text?.Trim() != c.Community)
            {
                c.Community = community.Text?.Trim() ?? "";
                LoadFlairs(service.Store.Flairs(c.Community), null, "");
            }
        };
        var fields = Ui.Column(
            Ui.Text("Галерея", 23),
            Ui.Text("Заголовок"),
            title,
            Ui.Text("Сообщество"),
            community,
            Ui.Row(flair, Ui.Button("Обновить flair", () => _ = RefreshFlairs())),
            nsfw,
            Ui.Text("Изображения · ↑ / ↓ меняют порядок, двойной щелчок редактирует подпись"),
            mediaList,
            Ui.Row(
                Ui.Button("Добавить", () => _ = AddImages()),
                Ui.Button(
                    "Удалить",
                    () =>
                    {
                        if (mediaList.SelectedItem is MediaItem i)
                            images.Remove(i);
                    }
                ),
                Ui.Button("↑", () => Move(-1)),
                Ui.Button("↓", () => Move(1)),
                Ui.Button("Подпись", () => _ = Caption())
            ),
            Ui.Text("Локальные теги через запятую"),
            tags,
            Ui.Text("Приватные заметки"),
            notes,
            scheduled,
            Ui.Row(when, Ui.Text(zone)),
            status
        );
        var save = Ui.Button("Сохранить", () => _ = Save(), true);
        var close = Ui.Button("Закрыть", () => Close(false));
        var dock = new DockPanel { Margin = new Thickness(22) };
        var bottom = Ui.Row(save, Ui.Button("Просмотр изображения", () => _ = Preview()), close);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(
            new ScrollViewer { Content = fields, Margin = new Thickness(0, 0, 0, 16) }
        );
        Content = dock;
        if (post is { Editable: false })
        {
            save.IsVisible = false;
            foreach (var field in fields.Children)
                field.IsEnabled = false;
            mediaList.IsEnabled = true;
            fields.Children.Add(Ui.Text(c.Body));
        }
    }

    private async Task Preview()
    {
        var item = mediaList.SelectedItem as MediaItem ?? images.FirstOrDefault();
        if (item is null || string.IsNullOrEmpty(item.File))
        {
            status.Text = "Сначала скачайте или выберите изображение";
            return;
        }
        try
        {
            using var bitmap = new Bitmap(service.Store.MediaPath(item.File));
            var viewer = new Window
            {
                Title = string.IsNullOrEmpty(item.Caption) ? "Изображение" : item.Caption,
                Width = 1000,
                Height = 750,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(16),
                },
            };
            await viewer.ShowDialog(this);
        }
        catch
        {
            status.Text = "Не удалось открыть изображение";
        }
    }

    private void LoadFlairs(List<Flair> list, string? id, string text)
    {
        var choices = new List<Flair> { new("", "") };
        choices.AddRange(list);
        var selected = choices.FirstOrDefault(f =>
            !string.IsNullOrEmpty(id) ? f.Id == id : f.Text == text
        );
        if (selected is null && !string.IsNullOrEmpty(text))
        {
            selected = new(id ?? "", text);
            choices.Add(selected);
        }
        flair.ItemsSource = choices;
        flair.SelectedItem = selected ?? choices[0];
    }

    private async Task RefreshFlairs()
    {
        try
        {
            status.Text = "Получаю метки через API…";
            var name = (community.Text ?? "").Trim().Replace("r/", "");
            var chosen = flair.SelectedItem as Flair;
            var values = await service.UseApi(a => a.GetFlairs(name));
            LoadFlairs(values, chosen?.Id, chosen?.Text ?? "");
            status.Text = "Метки обновлены";
        }
        catch (Exception e)
        {
            status.Text = e.Message;
        }
    }

    private async Task AddImages()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new()
                {
                    AllowMultiple = true,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Изображения")
                        {
                            Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp"],
                        },
                    ],
                }
            );
            if (images.Count + files.Count > 20)
                throw new InvalidOperationException("Не больше 20 изображений");
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (path is not null)
                    images.Add(service.Store.ImportFile(path));
            }
        }
        catch (Exception e)
        {
            status.Text = e.Message;
        }
    }

    private void Move(int direction)
    {
        var i = mediaList.SelectedIndex;
        if (i < 0 || i + direction < 0 || i + direction >= images.Count)
            return;
        images.Move(i, i + direction);
        mediaList.SelectedIndex = i + direction;
    }

    private async Task Caption()
    {
        if (mediaList.SelectedItem is not MediaItem image)
            return;
        var value = await Ui.Ask(this, "Подпись", "До 180 символов", image.Caption);
        if (value is null)
            return;
        if (value.Length > 180)
        {
            status.Text = "Подпись длиннее 180 символов";
            return;
        }
        var index = images.IndexOf(image);
        images[index] = image with { Caption = value };
        mediaList.SelectedIndex = index;
    }

    private async Task Save()
    {
        try
        {
            var f = flair.SelectedItem as Flair;
            var content = new PostContent
            {
                Title = title.Text?.Trim() ?? "",
                Community = (community.Text ?? "").Trim().Replace("r/", ""),
                Flair = f?.Text ?? "",
                FlairId = string.IsNullOrEmpty(f?.Id) ? null : f.Id,
                Nsfw = nsfw.IsChecked == true,
                Tags = (tags.Text ?? "")
                    .Split(',')
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .Distinct()
                    .ToList(),
                Notes = notes.Text ?? "",
                Images = images.ToList(),
            };
            double? time =
                scheduled.IsChecked == true
                    ? await Ui.ParseTime(
                        this,
                        when.Text ?? "",
                        service.Store.Setting("timezone", "Europe/Moscow")
                    )
                    : null;
            if (time <= Clock.Now)
                throw new InvalidOperationException("Укажите время в будущем");
            service.Store.Save(content, time, post?.Id);
            Close(true);
        }
        catch (Exception e)
        {
            status.Text = e.Message;
        }
    }
}
