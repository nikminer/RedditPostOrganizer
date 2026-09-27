using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Gallery.Core;

namespace Gallery.Desktop;

public sealed partial class MainWindow : Window
{
    private static readonly DataFormat<string> PostFormat =
        DataFormat.CreateStringApplicationFormat("gallery-post");
    private readonly GalleryService service;
    private DateTime anchor = DateTime.Today;
    private bool locked,
        exiting,
        rendering;
    private string? selected;
    private readonly Dictionary<string, Bitmap> thumbnails = [];

    private T C<T>(string name)
        where T : Control => this.FindControl<T>(name)!;

    private string Zone => service.Store.Setting("timezone", "Europe/Moscow");

    public MainWindow(GalleryService service)
    {
        this.service = service;
        AvaloniaXamlLoader.Load(this);
        locked = service.Store.HasPassword;
        anchor = Clock.Local(Clock.Now, Zone).Date;
        Click("NewButton", () => Edit(null));
        Click("EditButton", () => EditSelected());
        Click(
            "CopyButton",
            async () =>
            {
                var p = One();
                if (p is not null)
                {
                    var id = service.Store.Copy(p.Id, 1)[0];
                    await Edit(service.Store.Get(id));
                }
            }
        );
        Click("CopiesButton", Copies);
        Click("BatchButton", Batch);
        Click("ConnectButton", Connect);
        Click("ImportButton", Import);
        Click("SettingsButton", Settings);
        Click("PauseButton", Pause);
        Click(
            "HistoryButton",
            async () =>
            {
                var p = One();
                if (p is not null)
                    await Ui.Info(this, p.Url + "\n" + service.Store.History(p.Id, Zone));
            }
        );
        Click("ResolveButton", Resolve);
        Click("PreviousButton", () => Navigate(-1));
        Click("NextButton", () => Navigate(1));
        Click(
            "TodayButton",
            () =>
            {
                anchor = Clock.Local(Clock.Now, Zone).Date;
                Refresh();
                return Task.CompletedTask;
            }
        );
        Click(
            "UnlockButton",
            () =>
            {
                if (service.Store.CheckPassword(C<TextBox>("LockPassword").Text ?? ""))
                {
                    locked = false;
                    C<TextBox>("LockPassword").Text = "";
                    SetLock();
                }
                else
                    Notice("Неверный пароль");
                return Task.CompletedTask;
            }
        );
        foreach (var name in new[] { "Search", "Community", "TagFilter" })
            C<TextBox>(name).TextChanged += (_, _) => Refresh();
        C<ComboBox>("ViewMode").SelectionChanged += (_, _) => Refresh();
        var statuses = new List<KeyValuePair<string, string>> { new("", "Все статусы") };
        statuses.AddRange(Post.States);
        var filter = C<ComboBox>("StatusFilter");
        filter.ItemsSource = statuses;
        filter.ItemTemplate = new FuncDataTemplate<KeyValuePair<string, string>>(
            (x, _) => Ui.Text(x.Value)
        );
        filter.SelectedIndex = 0;
        filter.SelectionChanged += (_, _) => Refresh();
        C<CalendarDatePicker>("AnchorDate").SelectedDateChanged += (_, _) =>
        {
            if (!rendering && C<CalendarDatePicker>("AnchorDate").SelectedDate is { } date)
            {
                anchor = date;
                Refresh();
            }
        };
        var list = C<ListBox>("PostList");
        list.ItemTemplate = new FuncDataTemplate<Post>((p, _) => ListCard(p!));
        list.DoubleTapped += async (_, _) => await Safe(() => EditSelected());
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is Post p)
                selected = p.Id;
        };
        list.ContextMenu = PostMenu();
        var extra = new ContextMenu();
        AddMenu(
            extra,
            "Проверить API-подключение",
            async () =>
            {
                var name = await service.UseApi(a => a.Check());
                service.Store.Set("pause_reason", "");
                Notice("API-подключение u/" + name + " проверено");
            }
        );
        AddMenu(
            extra,
            "Отключить Reddit",
            async () =>
            {
                service.Store.Set("paused", true);
                Notice(await service.UseApi(a => a.Disconnect()));
            }
        );
        AddMenu(extra, "Резервная копия", Backup);
        AddMenu(extra, "Восстановить из архива", Restore);
        AddMenu(
            extra,
            "Папка данных",
            () =>
            {
                OpenExternal(service.Store.Root);
                return Task.CompletedTask;
            }
        );
        AddMenu(
            extra,
            "Заблокировать",
            () =>
            {
                if (!service.Store.HasPassword)
                    throw new InvalidOperationException("Сначала задайте пароль в настройках");
                locked = true;
                SetLock();
                return Task.CompletedTask;
            }
        );
        AddMenu(extra, "Выйти", Quit);
        C<Button>("MoreButton").Click += (_, _) => extra.Open(C<Button>("MoreButton"));
        service.Message += text =>
            Dispatcher.UIThread.Post(() =>
                Notice(locked ? "Есть обновление очереди. Разблокируйте приложение." : text)
            );
        service.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Closing += (_, e) =>
        {
            if (!exiting)
            {
                e.Cancel = true;
                if (service.Store.HasPassword)
                {
                    locked = true;
                    SetLock();
                }
                Hide();
            }
        };
        SetLock();
        Refresh();
    }

    private void SetLock()
    {
        C<Border>("LockScreen").IsVisible = locked;
        C<Grid>("Workspace").IsVisible = !locked;
    }

    private void Click(string name, Func<Task> action) =>
        C<Button>(name).Click += async (_, _) => await Safe(action);

    private async Task Safe(Func<Task> action)
    {
        try
        {
            await action();
            Refresh();
        }
        catch (Exception e)
        {
            await Ui.Info(
                this,
                e is ApiFailure or InvalidOperationException or ArgumentException
                    ? e.Message
                    : "Не удалось выполнить действие: " + e.GetType().Name
            );
        }
    }

    private void Notice(string text) => C<TextBlock>("NoticeText").Text = text;

    public static void OpenExternal(string value) =>
        Process.Start(new ProcessStartInfo(value) { UseShellExecute = true });

    private void AddMenu(ContextMenu menu, string label, Func<Task> action)
    {
        var item = new MenuItem { Header = label };
        item.Click += async (_, _) => await Safe(action);
        menu.Items.Add(item);
    }

    private ContextMenu PostMenu()
    {
        var menu = new ContextMenu();
        AddMenu(menu, "Открыть карточку", () => EditSelected());
        AddMenu(
            menu,
            "Создать копию",
            async () =>
            {
                var p = One();
                if (p is not null)
                    await Edit(service.Store.Get(service.Store.Copy(p.Id, 1)[0]));
            }
        );
        AddMenu(
            menu,
            "Открыть на Reddit",
            () =>
            {
                var p = One();
                if (p is not null && RedditApi.ValidPostUrl(p.Url))
                    OpenExternal(p.Url);
                return Task.CompletedTask;
            }
        );
        return menu;
    }

    private List<Post> Selected()
    {
        if (C<ComboBox>("ViewMode").SelectedIndex == 3)
            return C<ListBox>("PostList").SelectedItems?.Cast<Post>().ToList() ?? [];
        return selected is null ? [] : [service.Store.Get(selected)];
    }

    private Post? One()
    {
        var items = Selected();
        if (items.Count != 1)
        {
            Notice("Выберите один пост в календаре или списке");
            return null;
        }
        return items[0];
    }

    private Task EditSelected()
    {
        var p = One();
        return p is null ? Task.CompletedTask : Edit(p);
    }

    private async Task Edit(Post? post)
    {
        var editor = new EditorWindow(service, post);
        await editor.ShowDialog<bool>(this);
        Refresh();
    }

    private Control? Thumbnail(Post p)
    {
        var file = p.Content.Images.FirstOrDefault(i => !string.IsNullOrEmpty(i.File))?.File;
        if (file is null)
            return null;
        try
        {
            if (!thumbnails.TryGetValue(file, out var bitmap))
            {
                using var stream = File.OpenRead(service.Store.MediaPath(file));
                bitmap = Bitmap.DecodeToWidth(stream, 96);
                thumbnails[file] = bitmap;
            }
            return new Image
            {
                Source = bitmap,
                Width = 45,
                Height = 40,
                Stretch = Stretch.UniformToFill,
            };
        }
        catch
        {
            return null;
        }
    }

    private Control ListCard(Post p)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("55,*,180,175,160"),
            Margin = new Thickness(6),
        };
        var image = Thumbnail(p);
        if (image is not null)
            grid.Children.Add(image);
        var title = Ui.Column(
            Ui.Text(p.Content.Title, 15),
            Ui.Text(
                (p.Content.Nsfw ? "18+ · " : "")
                    + "r/"
                    + p.Content.Community
                    + " · "
                    + (p.Content.Source == "reddit" ? "Импорт из Reddit" : "Локальная галерея"),
                12,
                "#65758D"
            )
        );
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        var time = Ui.Text(Clock.Display(p.Scheduled, Zone));
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);
        var state = Ui.Text(p.StatusLabel);
        Grid.SetColumn(state, 3);
        grid.Children.Add(state);
        var tags = Ui.Text(string.Join(", ", p.Content.Tags));
        Grid.SetColumn(tags, 4);
        grid.Children.Add(tags);
        ToolTip.SetTip(grid, string.IsNullOrEmpty(p.Error) ? p.Content.Title : p.Error);
        return grid;
    }

    private void Refresh()
    {
        if (rendering)
            return;
        rendering = true;
        try
        {
            var all = service.Store.Posts();
            C<TextBlock>("Summary").Text =
                $"{all.Count(p => p.Status == "scheduled")} в очереди · {all.Count(p => p.Content.Source == "reddit")} импортировано · {Zone} · "
                + (
                    service.Store.Setting<string?>("account", null) is { } account
                        ? "u/" + account
                        : "API не подключён"
                )
                + (service.Busy ? " · выполняется операция" : "")
                + "  "
                + service.Store.Setting("pause_reason", "");
            C<Button>("PauseButton").Content = service.Store.Setting("paused", true)
                ? "▶ Запустить очередь"
                : "Ⅱ Пауза";
            C<CalendarDatePicker>("AnchorDate").SelectedDate = anchor;
            var search = C<TextBox>("Search").Text ?? "";
            var community = C<TextBox>("Community").Text ?? "";
            var tag = C<TextBox>("TagFilter").Text ?? "";
            var state = C<ComboBox>("StatusFilter").SelectedItem is KeyValuePair<string, string> s
                ? s.Key
                : "";
            var filtered = all.Where(p =>
                    (p.Content.Title + " " + p.Content.Notes).Contains(
                        search,
                        StringComparison.OrdinalIgnoreCase
                    )
                    && p.Content.Community.Contains(
                        community.Replace("r/", ""),
                        StringComparison.OrdinalIgnoreCase
                    )
                    && string.Join(",", p.Content.Tags)
                        .Contains(tag, StringComparison.OrdinalIgnoreCase)
                    && (state.Length == 0 || p.Status == state)
                )
                .ToList();
            var mode = C<ComboBox>("ViewMode").SelectedIndex;
            C<ListBox>("PostList").IsVisible = mode == 3;
            C<ScrollViewer>("CalendarScroll").IsVisible = mode != 3;
            if (mode == 3)
            {
                var keys =
                    C<ListBox>("PostList").SelectedItems?.Cast<Post>().Select(p => p.Id).ToHashSet()
                    ?? [];
                C<ListBox>("PostList").ItemsSource = filtered;
                foreach (var p in filtered.Where(p => keys.Contains(p.Id)))
                    C<ListBox>("PostList").SelectedItems?.Add(p);
            }
            else
                RenderCalendar(filtered, mode);
        }
        finally
        {
            rendering = false;
        }
    }

    private void RenderCalendar(List<Post> posts, int mode)
    {
        var grid = C<Grid>("CalendarGrid");
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        var month = mode == 0;
        var cols = mode == 2 ? 1 : 7;
        for (int i = 0; i < cols; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var start = month ? new DateTime(anchor.Year, anchor.Month, 1) : anchor;
        start = mode == 2 ? start : start.AddDays(-((int)start.DayOfWeek + 6) % 7);
        for (var c = 0; c < cols; c++)
        {
            var label = Ui.Text(
                month
                    ? new[] { "ПН", "ВТ", "СР", "ЧТ", "ПТ", "СБ", "ВС" }[c]
                    : start.AddDays(c).ToString("ddd dd.MM", CultureInfo.GetCultureInfo("ru-RU")),
                13,
                "#536783"
            );
            label.Margin = new Thickness(10);
            Grid.SetColumn(label, c);
            grid.Children.Add(label);
        }
        var rows = month ? 6 : 24;
        for (var r = 0; r < rows; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < cols; c++)
            {
                var day = start.AddDays(month ? r * 7 + c : c);
                var hour = month ? (int?)null : r;
                var stack = new StackPanel { Spacing = 5 };
                stack.Children.Add(
                    Ui.Text(
                        month
                            ? day.Day
                                + (
                                    day.Date == Clock.Local(Clock.Now, Zone).Date
                                        ? " · сегодня"
                                        : ""
                                )
                            : $"{r:00}:00",
                        12,
                        day.Month == anchor.Month ? "#3B5989" : "#8796AF"
                    )
                );
                var cell = new Border
                {
                    Child = stack,
                    MinHeight = month ? 100 : 85,
                    Padding = new Thickness(8),
                    Margin = new Thickness(2),
                    Background = Brushes.White,
                    BorderBrush = Brush.Parse("#DCE3EF"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                };
                Grid.SetColumn(cell, c);
                Grid.SetRow(cell, r + 1);
                grid.Children.Add(cell);
                DragDrop.SetAllowDrop(cell, true);
                cell.AddHandler(
                    DragDrop.DragOverEvent,
                    (_, e) =>
                    {
                        e.DragEffects = e.DataTransfer.Contains(PostFormat)
                            ? DragDropEffects.Move
                            : DragDropEffects.None;
                        e.Handled = true;
                    }
                );
                cell.AddHandler(
                    DragDrop.DropEvent,
                    async (_, e) =>
                    {
                        if (e.DataTransfer.TryGetValue(PostFormat) is string id)
                            await Safe(() => Move(id, day, hour));
                    }
                );
                foreach (
                    var p in posts.Where(p =>
                        p.Scheduled.HasValue
                        && Clock.Local(p.Scheduled.Value, Zone).Date == day.Date
                        && (hour is null || Clock.Local(p.Scheduled.Value, Zone).Hour == hour)
                    )
                )
                {
                    var text = Ui.Text(
                        $"{Clock.Local(p.Scheduled!.Value, Zone):HH:mm} {(p.Content.Nsfw ? "18+ · " : "")}{p.Content.Title}",
                        12
                    );
                    text.MaxLines = 2;
                    text.TextTrimming = TextTrimming.CharacterEllipsis;
                    var card = new Border
                    {
                        Child = text,
                        Padding = new Thickness(7),
                        CornerRadius = new CornerRadius(4),
                        Background = Brush.Parse(
                            selected == p.Id ? "#BFD3FF"
                            : p.Status == "published" ? "#E1F1EB"
                            : p.Status is "error" or "review" ? "#FCE9E4"
                            : "#E9EEFF"
                        ),
                    };
                    ToolTip.SetTip(
                        card,
                        p.Content.Title + "\nr/" + p.Content.Community + " · " + p.StatusLabel
                    );
                    card.ContextMenu = PostMenu();
                    Point? down = null;
                    card.PointerPressed += (_, e) =>
                    {
                        selected = p.Id;
                        down = e.GetPosition(card);
                    };
                    card.PointerMoved += async (_, e) =>
                    {
                        if (
                            down is { } origin
                            && e.GetCurrentPoint(card).Properties.IsLeftButtonPressed
                            && Math.Abs(e.GetPosition(card).X - origin.X)
                                + Math.Abs(e.GetPosition(card).Y - origin.Y)
                                > 6
                            && p.Editable
                        )
                        {
                            down = null;
                            var data = new DataTransfer();
                            data.Add(DataTransferItem.Create(PostFormat, p.Id));
                            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
                        }
                    };
                    card.DoubleTapped += async (_, _) => await Safe(() => Edit(p));
                    stack.Children.Add(card);
                }
            }
        }
    }

    private Task Navigate(int offset)
    {
        anchor = C<ComboBox>("ViewMode").SelectedIndex switch
        {
            0 => anchor.AddMonths(offset),
            1 => anchor.AddDays(offset * 7),
            _ => anchor.AddDays(offset),
        };
        Refresh();
        return Task.CompletedTask;
    }

    private Task Move(string id, DateTime day, int? hour)
    {
        var post = service.Store.Get(id);
        if (!post.Editable)
            throw new InvalidOperationException("Опубликованный пост нельзя перемещать");
        var old = Clock.Local(post.Scheduled!.Value, Zone);
        var time = new DateTime(day.Year, day.Month, day.Day, hour ?? old.Hour, old.Minute, 0);
        var choices = Clock.Candidates(time, Zone);
        if (choices.Length != 1)
            throw new InvalidOperationException("Уточните время перевода часов через редактор");
        if (choices[0] <= Clock.Now)
            throw new InvalidOperationException("Выберите время в будущем");
        service.Store.Batch([id], _ => { }, choices[0]);
        Dispatcher.UIThread.Post(Refresh);
        return Task.CompletedTask;
    }

    private Task Pause()
    {
        var paused = service.Store.Setting("paused", true);
        if (paused)
        {
            if (string.IsNullOrEmpty(service.Store.Setting<string?>("account", null)))
                throw new InvalidOperationException("Сначала подключите Reddit API");
            if (service.Store.Setting("pause_reason", "").Length > 0)
                throw new InvalidOperationException("Сначала восстановите API-подключение");
            if (service.Busy)
                throw new InvalidOperationException("Дождитесь окончания операции");
            service.Store.Recover();
        }
        service.Store.Set("paused", !paused);
        return Task.CompletedTask;
    }

    private async Task Copies()
    {
        var p = One();
        if (p is null)
            return;
        var dialog = new Window
        {
            Title = "Несколько копий",
            Width = 520,
            Height = 530,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var count = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 500,
            Value = 3,
        };
        var interval = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 525600,
            Value = 120,
        };
        var start = new TextBox { Text = Clock.Display(Clock.Now + 3600, Zone) };
        var schedule = new CheckBox { Content = "Назначить время" };
        var preview = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            Height = 175,
        };
        var status = Ui.Text("");
        var confirm = Ui.Button("Создать копии", () => _ = Create(), true);
        void Preview()
        {
            var n = (int)(count.Value ?? 1);
            preview.Text = string.Join(
                "\n",
                Enumerable
                    .Range(0, n)
                    .Select(i =>
                        $"{i + 1}. "
                        + (
                            schedule.IsChecked == true
                            && DateTime.TryParseExact(
                                start.Text,
                                "dd.MM.yyyy HH:mm",
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.None,
                                out var d
                            )
                                ? d.AddMinutes(i * (double)(interval.Value ?? 120))
                                    .ToString("dd.MM.yyyy HH:mm")
                                : "Черновик"
                        )
                    )
            );
        }
        async Task Create()
        {
            try
            {
                double? when =
                    schedule.IsChecked == true
                        ? await Ui.ParseTime(dialog, start.Text ?? "", Zone)
                        : null;
                if (when <= Clock.Now)
                    throw new InvalidOperationException("Выберите время в будущем");
                service.Store.Copy(
                    p.Id,
                    (int)(count.Value ?? 1),
                    when,
                    (int)(interval.Value ?? 120)
                );
                dialog.Close();
            }
            catch (Exception e)
            {
                status.Text = e.Message;
            }
        }
        count.ValueChanged += (_, _) => Preview();
        interval.ValueChanged += (_, _) => Preview();
        schedule.IsCheckedChanged += (_, _) => Preview();
        start.TextChanged += (_, _) => Preview();
        dialog.Content = new Border
        {
            Padding = new Thickness(24),
            Child = Ui.Column(
                Ui.Text("Количество"),
                count,
                schedule,
                start,
                Ui.Text("Интервал, минуты"),
                interval,
                preview,
                status,
                confirm
            ),
        };
        Preview();
        await dialog.ShowDialog(this);
    }

    private async Task Batch()
    {
        var posts = Selected();
        if (posts.Count == 0)
            throw new InvalidOperationException("Выберите посты в списке (Ctrl/Shift)");
        var w = new Window
        {
            Title = "Массовые действия",
            Width = 550,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var options = new ComboBox
        {
            ItemsSource = new[]
            {
                "Сообщество",
                "Локальные теги",
                "NSFW: включить",
                "NSFW: выключить",
                "Снять с расписания",
                "Назначить время",
            },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var value = new TextBox { Watermark = "Значение / дата дд.мм.гггг чч:мм" };
        var interval = new NumericUpDown
        {
            Value = 120,
            Minimum = 1,
            Maximum = 525600,
        };
        var status = Ui.Text("");
        w.Content = new Border
        {
            Padding = new Thickness(24),
            Child = Ui.Column(
                Ui.Text($"Выбрано: {posts.Count}"),
                options,
                value,
                Ui.Text("Интервал для расписания, минуты"),
                interval,
                status,
                Ui.Button("Применить", () => _ = Apply(), true)
            ),
        };
        async Task Apply()
        {
            try
            {
                var ids = posts.Select(p => p.Id).ToList();
                var input = value.Text ?? "";
                switch (options.SelectedIndex)
                {
                    case 0:
                        service.Store.Batch(
                            ids,
                            c =>
                            {
                                c.Community = input.Trim().Replace("r/", "");
                                c.Flair = "";
                                c.FlairId = null;
                            }
                        );
                        break;
                    case 1:
                        service.Store.Batch(
                            ids,
                            c =>
                                c.Tags = input
                                    .Split(',')
                                    .Select(x => x.Trim())
                                    .Where(x => x.Length > 0)
                                    .Distinct()
                                    .ToList()
                        );
                        break;
                    case 2:
                    case 3:
                        service.Store.Batch(ids, c => c.Nsfw = options.SelectedIndex == 2);
                        break;
                    case 4:
                        service.Store.Batch(ids, _ => { }, unschedule: true);
                        break;
                    case 5:
                        var time = await Ui.ParseTime(w, input, Zone);
                        if (time <= Clock.Now)
                            throw new InvalidOperationException("Выберите будущее время");
                        if (
                            !await Ui.Confirm(
                                w,
                                string.Join(
                                    "\n",
                                    Enumerable
                                        .Range(0, Math.Min(posts.Count, 12))
                                        .Select(i =>
                                            Clock.Display(
                                                time + i * (double)(interval.Value ?? 120) * 60,
                                                Zone
                                            )
                                        )
                                )
                            )
                        )
                            return;
                        service.Store.Batch(ids, _ => { }, time, (int)(interval.Value ?? 120));
                        break;
                }
                w.Close();
            }
            catch (Exception e)
            {
                status.Text = e.Message;
            }
        }
        await w.ShowDialog(this);
    }

    private async Task Connect()
    {
        var w = new Window
        {
            Title = "Подключение Reddit API",
            Width = 640,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        using var cancel = new CancellationTokenSource();
        var clientId = new TextBox
        {
            Text = service.Store.Setting("api_client_id", ""),
            Watermark = "Client ID",
        };
        var redirect = new TextBox { Text = RedditApi.RedirectUri, IsReadOnly = true };
        var status = Ui.Text(
            "Нужен одобренный API-доступ и приложение типа installed app. Пароль и Client Secret вводить не нужно."
        );
        Button button = null!;
        button = Ui.Button("Войти через Reddit", () => _ = Login(), true);
        w.Content = new Border
        {
            Padding = new Thickness(24),
            Child = Ui.Column(
                status,
                Ui.Text("Client ID"),
                clientId,
                Ui.Text("Redirect URI (укажите при регистрации)"),
                redirect,
                Ui.Row(
                    Ui.Button(
                        "Приложения Reddit",
                        () => OpenExternal("https://www.reddit.com/prefs/apps")
                    ),
                    Ui.Button(
                        "Как получить доступ",
                        () =>
                            OpenExternal(
                                "https://support.reddithelp.com/hc/en-us/articles/42728983564564-Responsible-Builder-Policy"
                            )
                    )
                ),
                button
            ),
        };
        w.Closing += (_, _) => cancel.Cancel();
        async Task Login()
        {
            button.IsEnabled = false;
            try
            {
                service.Store.Set("paused", true);
                status.Text = "Разрешите доступ в обычном браузере. Ожидаю возврата из Reddit…";
                var name = await service.UseApi(a =>
                    a.Connect((clientId.Text ?? "").Trim(), OpenExternal, cancel.Token)
                );
                service.Store.Set("pause_reason", "");
                Notice("Подключён u/" + name + ". Проверьте расписание и запустите очередь.");
                w.Close();
            }
            catch (Exception e)
            {
                status.Text = e.Message;
            }
            finally
            {
                button.IsEnabled = true;
            }
        }
        await w.ShowDialog(this);
    }

    private async Task Import()
    {
        if (string.IsNullOrEmpty(service.Store.Setting<string?>("account", null)))
            throw new InvalidOperationException("Сначала подключите Reddit API");
        var w = new Window
        {
            Title = "Мои опубликованные посты",
            Width = 620,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        using var cancel = new CancellationTokenSource();
        var download = new CheckBox
        {
            Content = "Скачать изображения и галереи на этот ПК",
            IsChecked = true,
        };
        var status = Ui.Text(
            "Импортируются ваши посты из API. Они появятся в календаре на датах публикации. Reddit может ограничивать глубину доступной истории."
        );
        Button start = null!,
            close = null!;
        start = Ui.Button("Начать синхронизацию", () => _ = Sync(), true);
        close = Ui.Button("Закрыть", () => w.Close());
        w.Content = new Border
        {
            Padding = new Thickness(24),
            Child = Ui.Column(
                Ui.Text("История вашего аккаунта", 23),
                status,
                download,
                Ui.Row(start, close)
            ),
        };
        w.Closing += (_, _) => cancel.Cancel();
        async Task Sync()
        {
            start.IsEnabled = false;
            download.IsEnabled = false;
            close.Content = "Остановить и закрыть";
            try
            {
                var result = await service.UseApi(a =>
                    a.ImportMine(
                        download.IsChecked == true,
                        text => Dispatcher.UIThread.Post(() => status.Text = text),
                        cancel.Token
                    )
                );
                status.Text =
                    $"Получено: {result.Received} постов. Скачано: {result.Downloaded} изображений. Ошибок медиа: {result.MediaErrors}.\n"
                    + (
                        result.Cancelled
                            ? "Остановлено. Уже полученные записи сохранены."
                            : "Сохранены все записи, отданные этой выдачей API. Полноту всей истории Reddit не гарантирует."
                    );
                Notice(status.Text);
                Refresh();
            }
            catch (Exception e)
            {
                status.Text = e.Message;
                Notice("Импорт остановлен: " + e.Message);
            }
            finally
            {
                start.IsEnabled = true;
                download.IsEnabled = true;
                close.Content = "Закрыть";
            }
        }
        await w.ShowDialog(this);
    }

    private async Task Resolve()
    {
        var p = One();
        if (p is null)
            return;
        if (p.Status != "review")
            throw new InvalidOperationException("Выберите пост со статусом «Требуется проверка»");
        var link = await Ui.Ask(
            this,
            "Результат отправки",
            "Ссылка на опубликованный пост. Если проверили профиль и поста нет — оставьте пустым."
        );
        if (link is null)
            return;
        if (link.Length > 0)
        {
            if (!RedditApi.ValidPostUrl(link))
                throw new InvalidOperationException("Укажите полную https-ссылку на пост Reddit");
            service.Store.Status(p.Id, "published", "Подтверждено пользователем", link);
        }
        else if (
            await Ui.Confirm(
                this,
                "Вы проверили Reddit и уверены, что этот пост не был опубликован?"
            )
        )
        {
            service.Store.Status(p.Id, "draft");
            service.Store.Batch([p.Id], _ => { }, unschedule: true);
        }
    }

    private async Task Backup()
    {
        var path = await Ui.SaveFile(
            this,
            "Резервная копия",
            $"Reddit-Post-Organizer-{DateTime.Now:yyyy-MM-dd-HHmmss}.zip"
        );
        if (path is null)
            return;
        if (File.Exists(path))
            File.Delete(path);
        await Task.Run(() => service.Store.Backup(path));
        Notice("Резервная копия сохранена без OAuth-токенов");
    }

    private async Task Restore()
    {
        var path = await Ui.OpenFile(this, "Восстановить", "zip");
        if (
            path is null
            || !await Ui.Confirm(
                this,
                "Текущая база будет заменена, её резервная копия сохранится в папке данных. Продолжить?"
            )
        )
            return;
        service.Store.Set("paused", true);
        await service.UseApi(async _ =>
        {
            await Task.Run(() =>
            {
                service.Store.Backup(
                    Path.Combine(
                        service.Store.Root,
                        $"before-restore-{DateTime.Now:yyyyMMddHHmmss}.zip"
                    )
                );
                service.Store.Restore(path);
            });
            return true;
        });
        Notice("Данные восстановлены. Подключите аккаунт заново.");
    }

    private async Task Settings()
    {
        var w = new Window
        {
            Title = "Настройки",
            Width = 630,
            Height = 580,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var zone = new ComboBox
        {
            ItemsSource = TimeZoneInfo
                .GetSystemTimeZones()
                .Select(z => z.Id)
                .Prepend(Zone)
                .Distinct()
                .ToList(),
            SelectedItem = Zone,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var startup = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 1440,
            Value = service.Store.Setting("startup_minutes", 5),
        };
        var retry = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 1440,
            Value = service.Store.Setting("retry_minutes", 15),
        };
        var auto = new CheckBox
        {
            Content = "Автозапуск после входа в Windows",
            IsChecked = service.Store.Setting("autostart", false),
        };
        var password = new TextBox
        {
            PasswordChar = '●',
            Watermark = "Новый пароль (оставьте пустым, чтобы не менять)",
        };
        var repeat = new TextBox { PasswordChar = '●', Watermark = "Повторите пароль" };
        var status = Ui.Text(
            "Смена часового пояса меняет отображение, сохраняя момент публикации. Пароль блокирует интерфейс, но не шифрует медиа."
        );
        w.Content = new Border
        {
            Padding = new Thickness(24),
            Child = Ui.Column(
                Ui.Text("Часовой пояс"),
                zone,
                Ui.Text("После запуска / сна, минуты"),
                startup,
                Ui.Text("Повтор при недоступности Reddit, минуты"),
                retry,
                auto,
                password,
                repeat,
                status,
                Ui.Button("Сохранить", () => Save(), true)
            ),
        };
        void Save()
        {
            try
            {
                if (!string.IsNullOrEmpty(password.Text))
                {
                    if (password.Text.Length < 8 || password.Text != repeat.Text)
                        throw new InvalidOperationException(
                            "Пароли должны совпадать и содержать минимум 8 символов"
                        );
                    service.Store.SetPassword(password.Text);
                }
                if (OperatingSystem.IsWindows())
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run"
                    );
                    if (auto.IsChecked == true)
                        key.SetValue(
                            "RedditGallery",
                            "\""
                                + Environment.ProcessPath
                                + "\" --tray --data-dir \""
                                + service.Store.Root
                                + "\""
                        );
                    else
                        key.DeleteValue("RedditGallery", false);
                }
                service.Store.Set("autostart", auto.IsChecked == true);
                service.Store.Set("timezone", zone.SelectedItem?.ToString() ?? Zone);
                service.Store.Set("startup_minutes", (int)(startup.Value ?? 5));
                service.Store.Set("retry_minutes", (int)(retry.Value ?? 15));
                w.Close();
            }
            catch (Exception e)
            {
                status.Text = e.Message;
            }
        }
        await w.ShowDialog(this);
    }

    public async Task Quit()
    {
        if (locked)
        {
            Show();
            Activate();
            return;
        }
        Notice("Завершаю операции…");
        await service.DisposeAsync();
        exiting = true;
        foreach (var image in thumbnails.Values)
            image.Dispose();
        (
            Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
        )?.Shutdown();
    }
}
