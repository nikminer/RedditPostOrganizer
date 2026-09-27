namespace RedditPostOrganizer.Core;

public sealed class PublicationService : IAsyncDisposable
{
    public Store Store { get; }
    public RedditApi Api { get; }
    private readonly SemaphoreSlim mutex = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private Task? loop;
    public bool Busy { get; private set; }
    public event Action<string>? Message;
    public event Action? Changed;

    public PublicationService(Store store, RedditApi? api = null)
    {
        Store = store;
        store.MigrateTransport();
        Api = api ?? new(store);
    }

    public void Start()
    {
        Store.Recover();
        loop = Task.Run(Loop);
    }

    public async Task<T> UseApi<T>(Func<RedditApi, Task<T>> action)
    {
        await mutex.WaitAsync();
        Busy = true;
        Changed?.Invoke();
        try
        {
            return await action(Api);
        }
        finally
        {
            Store.Recover();
            Busy = false;
            mutex.Release();
            Changed?.Invoke();
        }
    }

    private async Task Loop()
    {
        var last = Clock.Now;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (await mutex.WaitAsync(0, stop.Token))
                {
                    try
                    {
                        var now = Clock.Now;
                        if (now - last > 60 || now < last - 5)
                            Store.Recover();
                        if (
                            !Store.Setting("paused", true)
                            && string.IsNullOrEmpty(Store.Setting("pause_reason", ""))
                            && !string.IsNullOrEmpty(Store.Setting<string?>("account", null))
                        )
                        {
                            var post = Store.Claim();
                            if (post is not null)
                            {
                                Busy = true;
                                Changed?.Invoke();
                                await Publish(post);
                                Changed?.Invoke();
                            }
                        }
                    }
                    finally
                    {
                        Busy = false;
                        mutex.Release();
                    }
                }
                last = Clock.Now;
                await Task.Delay(1000, stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                Store.Set(
                    "pause_reason",
                    "Внутренняя ошибка очереди. Проверьте историю и перезапустите приложение."
                );
                Message?.Invoke("Очередь приостановлена");
            }
        }
    }

    private async Task Publish(Post post)
    {
        try
        {
            var url = await Api.Send(post, stop.Token);
            Store.Status(post.Id, "published", url: url);
            Message?.Invoke("Галерея опубликована");
        }
        catch (ApiFailure e)
        {
            ApplyFailure(Store, post, e);
            Message?.Invoke(e.Message);
        }
        catch
        {
            Store.Status(
                post.Id,
                "review",
                "Неожиданная ошибка. Проверьте наличие поста на Reddit."
            );
            Message?.Invoke("Результат отправки требует проверки");
        }
    }

    public static void ApplyFailure(Store store, Post post, ApiFailure error)
    {
        switch (error.Kind)
        {
            case "network":
            case "rate":
                store.Defer(
                    post.Id,
                    Math.Max(
                        store.Setting("retry_minutes", 15) * 60,
                        error.Kind == "rate" ? error.Seconds : 0
                    ),
                    error.Message
                );
                break;
            case "auth":
                store.Status(post.Id, "scheduled", error.Message);
                store.Set("pause_reason", error.Message);
                break;
            case "uncertain":
                store.Status(post.Id, "review", error.Message);
                break;
            default:
                store.Status(post.Id, "error", error.Message);
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (loop is not null)
            await loop;
        await mutex.WaitAsync();
        Api.Dispose();
        mutex.Release();
        stop.Dispose();
        mutex.Dispose();
    }
}
