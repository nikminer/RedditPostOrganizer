using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RedditPostOrganizer.Core;

public record TokenRecord
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string Account { get; set; } = "";
    public double ExpiresAt { get; set; }
}

public interface ITokenVault
{
    TokenRecord? Load();
    void Save(TokenRecord value);
    void Clear();
}

public sealed class TokenVault(string root) : ITokenVault
{
    public string PathName => Path.Combine(root, "oauth-token.bin");

    public TokenRecord? Load()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        if (!File.Exists(PathName))
            return null;
        try
        {
            return Json.Read<TokenRecord>(
                Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(
                        File.ReadAllBytes(PathName),
                        null,
                        DataProtectionScope.CurrentUser
                    )
                )
            );
        }
        catch
        {
            throw new ApiFailure(
                "Не удалось прочитать OAuth-сессию Windows. Подключите Reddit заново.",
                "auth"
            );
        }
    }

    public void Save(TokenRecord value)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        var bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(Json.Write(value)),
            null,
            DataProtectionScope.CurrentUser
        );
        File.WriteAllBytes(PathName + ".tmp", bytes);
        File.Move(PathName + ".tmp", PathName, true);
    }

    public void Clear() => File.Delete(PathName);
}

public sealed class RedditApi : IDisposable
{
    public const string RedirectUri = "http://127.0.0.1:8765/callback";
    public const string Scopes = "identity read submit flair history";
    private const string UserAgent =
        "windows:reddit-post-organizer:v3.0.0 (personal gallery scheduler)";
    private readonly Store store;
    private readonly HttpClient client;
    private readonly HttpClient media;
    private readonly ITokenVault vault;
    private double rateUntil;

    public RedditApi(
        Store store,
        HttpClient? client = null,
        HttpClient? media = null,
        ITokenVault? vault = null
    )
    {
        this.store = store;
        this.client =
            client
            ?? new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            {
                Timeout = TimeSpan.FromSeconds(90),
            };
        this.media =
            media
            ?? new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            {
                Timeout = TimeSpan.FromSeconds(120),
            };
        this.vault = vault ?? new TokenVault(store.Root);
    }

    public void Dispose()
    {
        client.Dispose();
        media.Dispose();
    }

    private static string Text(JsonNode? node, string key, string fallback = "") =>
        node?[key]?.GetValue<string>() ?? fallback;

    private static bool Flag(JsonNode? node, string key) => node?[key]?.GetValue<bool>() ?? false;

    public static bool ValidPostUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == "https"
        && (u.Host is "reddit.com" or "www.reddit.com")
        && Regex.IsMatch(u.AbsolutePath, "^/r/[^/]+/comments/[a-z0-9]+(?:/|$)");

    public static string ValidateClient(string id) =>
        Regex.IsMatch(id, "^[A-Za-z0-9_-]{5,128}$")
            ? id
            : throw new ApiFailure("Укажите Client ID приложения типа installed app");

    private static string Community(string name) =>
        Regex.IsMatch(name, "^[A-Za-z0-9_]{2,32}$")
            ? name
            : throw new ApiFailure("Некорректное имя сообщества");

    private static FormUrlEncodedContent Form(params (string, string)[] values) =>
        new(values.Select(v => new KeyValuePair<string, string>(v.Item1, v.Item2)));

    private static int Retry(HttpResponseMessage response) =>
        Math.Max(
            1,
            (int)
                Math.Ceiling(
                    response.Headers.RetryAfter?.Delta?.TotalSeconds
                        ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)?.TotalSeconds
                        ?? 900
                )
        );

    public static void HandleErrors(JsonNode? errors)
    {
        if (errors is not JsonArray array || array.Count == 0)
            return;
        var codes = array.Select(x => x?[0]?.GetValue<string>() ?? "").ToArray();
        if (codes.Contains("RATELIMIT"))
        {
            var text = string.Join(" ", array.Select(x => x?[1]?.GetValue<string>()));
            var m = Regex.Match(text, "(\\d+)\\s*(second|minute|hour)", RegexOptions.IgnoreCase);
            var seconds = m.Success
                ? int.Parse(m.Groups[1].Value)
                    * (
                        m.Groups[2].Value.ToLowerInvariant() switch
                        {
                            "hour" => 3600,
                            "minute" => 60,
                            _ => 1,
                        }
                    )
                    + 1
                : 900;
            throw new ApiFailure(
                "Reddit просит подождать перед следующей публикацией",
                "rate",
                seconds
            );
        }
        if (codes.Any(c => c is "USER_REQUIRED" or "OAUTH2_INVALID_TOKEN"))
            throw new ApiFailure("Reddit требует повторного входа", "auth");
        throw new ApiFailure(
            "Reddit отклонил запрос: "
                + string.Join(", ", codes.Where(c => Regex.IsMatch(c, "^[A-Z0-9_]{1,80}$")))
        );
    }

    private async Task<TokenRecord> Exchange(
        string id,
        HttpContent form,
        string refresh = "",
        CancellationToken ct = default
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.reddit.com/api/v1/access_token"
        )
        {
            Content = form,
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes(id + ":"))
        );
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new ApiFailure("Нет связи с сервером авторизации Reddit", "network");
        }
        using (response)
        {
            if ((int)response.StatusCode == 429)
                throw new ApiFailure("Слишком много запросов", "rate", Retry(response));
            if ((int)response.StatusCode >= 500)
                throw new ApiFailure("Сервер авторизации временно недоступен", "network");
            if (!response.IsSuccessStatusCode)
                throw new ApiFailure(
                    "Reddit не разрешил вход. Проверьте одобрение API, Client ID, тип installed app и Redirect URI.",
                    "auth"
                );
            var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var access = Text(data, "access_token");
            var granted = Text(data, "scope").Split(' ').ToHashSet();
            if (access.Length == 0 || Scopes.Split(' ').Any(s => !granted.Contains(s)))
                throw new ApiFailure(
                    "Reddit не выдал нужные разрешения. Повторите подключение.",
                    "auth"
                );
            return new()
            {
                AccessToken = access,
                RefreshToken = Text(data, "refresh_token", refresh),
                ClientId = id,
                ExpiresAt = Clock.Now + (data?["expires_in"]?.GetValue<double>() ?? 3600),
            };
        }
    }

    private async Task<string> Access(CancellationToken ct)
    {
        var token = vault.Load();
        if (token is null || token.ClientId != store.Setting("api_client_id", ""))
            throw new ApiFailure("Сначала подключите Reddit API", "auth");
        if (token.ExpiresAt <= Clock.Now + 60)
        {
            var updated = await Exchange(
                token.ClientId,
                Form(("grant_type", "refresh_token"), ("refresh_token", token.RefreshToken)),
                token.RefreshToken,
                ct
            );
            updated.Account = token.Account;
            vault.Save(updated);
            token = updated;
        }
        return token.AccessToken;
    }

    public async Task<JsonNode> Request(
        HttpMethod method,
        string path,
        HttpContent? content = null,
        bool final = false,
        string? access = null,
        CancellationToken ct = default
    )
    {
        if (!path.StartsWith('/') || path.StartsWith("//"))
            throw new ArgumentException("Invalid API path");
        if (rateUntil > Clock.Now)
            throw new ApiFailure(
                "Ожидание лимита API",
                "rate",
                (int)Math.Ceiling(rateUntil - Clock.Now)
            );
        var token = access ?? await Access(ct);
        using var request = new HttpRequestMessage(method, "https://oauth.reddit.com" + path)
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new ApiFailure(
                final
                    ? "Ответ на публикацию потерян. Проверьте Reddit перед повторной отправкой."
                    : "Нет соединения с Reddit API",
                final ? "uncertain" : "network"
            );
        }
        using (response)
        {
            if (
                response.Headers.TryGetValues("x-ratelimit-remaining", out var left)
                && double.TryParse(
                    left.First(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var remaining
                )
                && remaining <= 0
                && response.Headers.TryGetValues("x-ratelimit-reset", out var reset)
                && double.TryParse(
                    reset.First(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var seconds
                )
            )
                rateUntil = Clock.Now + seconds;
            var status = (int)response.StatusCode;
            if (status == 429)
                throw new ApiFailure("Reddit просит подождать", "rate", Retry(response));
            if (status == 401)
                throw new ApiFailure("Сессия Reddit истекла. Подключите аккаунт заново.", "auth");
            if (status == 403)
                throw new ApiFailure(
                    "Reddit запретил запрос. Проверьте одобрение API и права в сообществе.",
                    path == "/api/v1/me" ? "auth" : "error"
                );
            if (status >= 500 || status is >= 300 and < 400)
                throw new ApiFailure(
                    final
                        ? "Reddit не подтвердил результат. Нужна проверка."
                        : "Reddit временно недоступен",
                    final ? "uncertain" : "network"
                );
            if (status >= 400)
                throw new ApiFailure($"Reddit отклонил запрос (HTTP {status})");
            try
            {
                var data =
                    JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))
                    ?? throw new System.Text.Json.JsonException();
                if (data is JsonObject)
                {
                    var envelope = data["json"] ?? data;
                    HandleErrors(envelope["errors"]);
                }
                return data;
            }
            catch (System.Text.Json.JsonException)
            {
                throw new ApiFailure(
                    "Непонятный ответ Reddit API",
                    final ? "uncertain" : "network"
                );
            }
        }
    }

    public async Task<string> Connect(string id, Action<string> openBrowser, CancellationToken ct)
    {
        ValidateClient(id);
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var listener = new TcpListener(IPAddress.Loopback, 8765);
        listener.Server.ExclusiveAddressUse = true;
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            throw new ApiFailure("Порт 8765 занят. Закройте другое окно авторизации и повторите.");
        }
        string? code = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            var url =
                "https://www.reddit.com/api/v1/authorize?"
                + string.Join(
                    "&",
                    new Dictionary<string, string>
                    {
                        { "client_id", id },
                        { "response_type", "code" },
                        { "state", state },
                        { "redirect_uri", RedirectUri },
                        { "duration", "permanent" },
                        { "scope", Scopes },
                    }.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value))
                );
            openBrowser(url);
            while (code is null)
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                using var input = socket.GetStream();
                using var reader = new StreamReader(input, Encoding.ASCII, false, 1024, true);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(deadline.Token);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }
                if (line is null || line.Length > 8192)
                    continue;
                var pieces = line.Split(' ');
                var target = pieces.Length > 1 ? pieces[1] : "";
                var valid = TryCallback(target, state, out var returnedCode, out var denied);
                if (valid && !denied)
                    code = returnedCode;
                var body = Encoding.UTF8.GetBytes(
                    "<!doctype html><meta charset=utf-8><title>Reddit Post Organizer</title><p>"
                        + (
                            valid
                                ? "Вернитесь в Reddit Post Organizer для завершения подключения."
                                : "Некорректный ответ. Вернитесь в приложение."
                        )
                        + "</p>"
                );
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {(valid ? 200 : 400)} OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"
                );
                try
                {
                    await input.WriteAsync(header, deadline.Token);
                    await input.WriteAsync(body, deadline.Token);
                }
                catch (IOException) { }
                if (valid && denied)
                    throw new ApiFailure("Доступ отклонён пользователем", "auth");
            }
        }
        catch (OperationCanceledException)
        {
            throw new ApiFailure("Подключение отменено или время ожидания истекло", "auth");
        }
        finally
        {
            listener.Stop();
        }
        var token = await Exchange(
            id,
            Form(
                ("grant_type", "authorization_code"),
                ("code", code),
                ("redirect_uri", RedirectUri)
            ),
            ct: ct
        );
        if (token.RefreshToken.Length == 0)
            throw new ApiFailure("Reddit не выдал постоянный токен", "auth");
        var me = await Request(HttpMethod.Get, "/api/v1/me", access: token.AccessToken, ct: ct);
        token.Account = Text(me, "name");
        if (token.Account.Length == 0)
            throw new ApiFailure("Не удалось подтвердить аккаунт", "auth");
        ct.ThrowIfCancellationRequested();
        vault.Save(token);
        store.Set("api_client_id", id);
        store.Set("account", token.Account);
        return token.Account;
    }

    public static bool TryCallback(string target, string state, out string? code, out bool denied)
    {
        code = null;
        denied = false;
        if (!target.StartsWith("/callback?"))
            return false;
        var values = new Dictionary<string, string>();
        foreach (var item in target[(target.IndexOf('?') + 1)..].Split('&'))
        {
            var pair = item.Split('=', 2);
            if (pair.Length != 2)
                return false;
            var key = Uri.UnescapeDataString(pair[0]);
            if (!values.TryAdd(key, Uri.UnescapeDataString(pair[1].Replace('+', ' '))))
                return false;
        }
        if (
            !values.TryGetValue("state", out var supplied)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(supplied),
                Encoding.UTF8.GetBytes(state)
            )
        )
            return false;
        denied = values.ContainsKey("error");
        values.TryGetValue("code", out code);
        return denied || !string.IsNullOrWhiteSpace(code);
    }

    public async Task<string> Check(CancellationToken ct = default)
    {
        var name = Text(await Request(HttpMethod.Get, "/api/v1/me", ct: ct), "name");
        if (
            string.IsNullOrEmpty(name)
            || !name.Equals(
                store.Setting<string?>("account", null),
                StringComparison.OrdinalIgnoreCase
            )
        )
            throw new ApiFailure(
                "Аккаунт API отличается от подключённого. Подключите заново.",
                "auth"
            );
        return name;
    }

    public async Task<string> Disconnect()
    {
        TokenRecord? token = null;
        try
        {
            token = vault.Load();
        }
        catch (ApiFailure) { }
        var revoked = false;
        try
        {
            if (token is not null)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    "https://www.reddit.com/api/v1/revoke_token"
                )
                {
                    Content = Form(
                        ("token", token.RefreshToken),
                        ("token_type_hint", "refresh_token")
                    ),
                };
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                request.Headers.Authorization = new(
                    "Basic",
                    Convert.ToBase64String(Encoding.ASCII.GetBytes(token.ClientId + ":"))
                );
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var response = await client.SendAsync(request, timeout.Token);
                revoked = response.IsSuccessStatusCode;
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { }
        finally
        {
            vault.Clear();
            store.Set<string?>("account", null);
            store.Set("paused", true);
        }
        return revoked
            ? "Доступ отозван, аккаунт отключён"
            : "Токен удалён с ПК. При необходимости отзовите доступ в настройках Reddit.";
    }

    public async Task<List<Flair>> GetFlairs(string community, CancellationToken ct = default)
    {
        var result = await Request(
            HttpMethod.Post,
            $"/r/{Community(community)}/api/flairselector/",
            Form(("is_newlink", "true")),
            ct: ct
        );
        var choices =
            result["choices"] as JsonArray ?? throw new ApiFailure("API не вернул список меток");
        var values = choices
            .Where(x => Text(x, "flair_template_id").Length > 0)
            .Select(x => new Flair(Text(x, "flair_template_id"), Text(x, "flair_text")))
            .ToList();
        store.CacheFlairs(community, values);
        return values;
    }

    public async Task<string?> ResolveFlair(PostContent content, CancellationToken ct)
    {
        if (content.Flair.Length == 0 && string.IsNullOrEmpty(content.FlairId))
            return null;
        var choices = await GetFlairs(content.Community, ct);
        var matches = choices
            .Where(c =>
                !string.IsNullOrEmpty(content.FlairId)
                    ? c.Id == content.FlairId
                    : c.Text == content.Flair
            )
            .ToList();
        if (matches.Count != 1)
            throw new ApiFailure(
                "Flair удалён или неоднозначен. Обновите метки и выберите нужную."
            );
        return matches[0].Id;
    }

    public async Task<JsonObject> Upload(MediaItem item, CancellationToken ct)
    {
        var path = store.MediaPath(item.File);
        var mime = Path.GetExtension(path) switch
        {
            ".jpg" => "image/jpeg",
            ".png" => "image/png",
            _ => "image/webp",
        };
        var lease = await Request(
            HttpMethod.Post,
            "/api/media/asset.json",
            Form(("filepath", item.File), ("mimetype", mime)),
            ct: ct
        );
        var action = Text(lease["args"], "action");
        if (action.StartsWith("//"))
            action = "https:" + action;
        if (
            !Uri.TryCreate(action, UriKind.Absolute, out var url)
            || url.Scheme != "https"
            || url.UserInfo.Length > 0
            || url.Port != 443
            || !(
                url.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)
                || url.Host.EndsWith(".redditmedia.com", StringComparison.OrdinalIgnoreCase)
                || url.Host.EndsWith(".redd.it", StringComparison.OrdinalIgnoreCase)
            )
        )
            throw new ApiFailure("Неподдерживаемый адрес загрузки от Reddit");
        var asset = Text(lease["asset"], "asset_id");
        if (asset.Length == 0)
            throw new ApiFailure("Reddit не выдал идентификатор медиа");
        using var form = new MultipartFormDataContent();
        foreach (
            var field in lease["args"]?["fields"]?.AsArray()
                ?? throw new ApiFailure("Нет параметров загрузки")
        )
            form.Add(new StringContent(Text(field, "value")), Text(field, "name"));
        using var file = File.OpenRead(path);
        using var fileContent = new StreamContent(file);
        fileContent.Headers.ContentType = new(mime);
        form.Add(fileContent, "file", item.File);
        try
        {
            using var response = await media.PostAsync(url, form, ct);
            if ((int)response.StatusCode >= 500)
                throw new ApiFailure("Хранилище изображений Reddit недоступно", "network");
            if ((int)response.StatusCode == 429)
                throw new ApiFailure("Ограничение загрузки", "rate", Retry(response));
            if (!response.IsSuccessStatusCode)
                throw new ApiFailure("Reddit отклонил изображение");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new ApiFailure(
                "Не удалось загрузить изображение. Пост ещё не отправлен.",
                "network"
            );
        }
        return new()
        {
            ["media_id"] = asset,
            ["caption"] = item.Caption,
            ["outbound_url"] = "",
        };
    }

    public async Task<string> Send(Post post, CancellationToken ct = default)
    {
        var c = post.Content;
        store.Validate(c, true);
        await Check(ct);
        var about = (await Request(HttpMethod.Get, $"/r/{Community(c.Community)}/about", ct: ct))[
            "data"
        ];
        if (about?["allow_galleries"]?.GetValue<bool>() == false || Flag(about, "user_is_banned"))
            throw new ApiFailure("Галереи недоступны для этого аккаунта в выбранном сообществе");
        var flair = await ResolveFlair(c, ct);
        var items = new JsonArray();
        foreach (var image in c.Images)
            items.Add(await Upload(image, ct));
        var payload = new JsonObject
        {
            ["api_type"] = "json",
            ["show_error_list"] = true,
            ["validate_on_submit"] = true,
            ["sr"] = c.Community,
            ["title"] = c.Title,
            ["nsfw"] = c.Nsfw || Flag(about, "over18"),
            ["spoiler"] = false,
            ["sendreplies"] = true,
            ["items"] = items,
        };
        if (flair is not null)
            payload["flair_id"] = flair;
        var token = await Access(ct);
        ct.ThrowIfCancellationRequested();
        var attempt = store.Attempt(post.Id);
        string url;
        try
        {
            var response = await Request(
                HttpMethod.Post,
                "/api/submit_gallery_post.json",
                new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                true,
                token,
                ct
            );
            url = Text(response["json"]?["data"], "url");
            if (
                !ValidPostUrl(url)
                || !new Uri(url)
                    .AbsolutePath.Split('/')[2]
                    .Equals(c.Community, StringComparison.OrdinalIgnoreCase)
            )
                throw new ApiFailure(
                    "Reddit не вернул подтверждённую ссылку. Проверьте результат вручную.",
                    "uncertain"
                );
        }
        catch (ApiFailure e) when (e.Kind == "uncertain")
        {
            // The exact uploaded asset IDs prove identity; title matching alone cannot.
            using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            string? verified = null;
            try
            {
                verified = await CheckResult(
                    c,
                    items.Select(i => Text(i, "media_id")).ToArray(),
                    verification.Token
                );
            }
            catch
            { /* Keep the original uncertain result if verification is unavailable. */
            }
            if (verified is null)
                throw;
            url = verified;
        }
        store.Confirm(attempt, url);
        return url;
    }

    public async Task<string?> CheckResult(
        PostContent content,
        string[] mediaIds,
        CancellationToken ct
    )
    {
        var account = await Check(ct);
        var listing = await Request(
            HttpMethod.Get,
            $"/user/{Uri.EscapeDataString(account)}/submitted?limit=100&sort=new&raw_json=1",
            ct: ct
        );
        var matches = new List<string>();
        foreach (var child in listing["data"]?["children"]?.AsArray() ?? [])
        {
            var data = child?["data"];
            if (
                data is null
                || Text(data, "title") != content.Title
                || !Text(data, "subreddit")
                    .Equals(content.Community, StringComparison.OrdinalIgnoreCase)
            )
                continue;
            var ids = data["gallery_data"]
                ?["items"]?.AsArray()
                .Select(i => Text(i, "media_id"))
                .ToArray();
            if (ids is null || !mediaIds.SequenceEqual(ids))
                continue;
            var url = "https://www.reddit.com" + Text(data, "permalink");
            if (ValidPostUrl(url))
                matches.Add(url);
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    public static bool DownloadAllowed(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var u)
        && u.Scheme == "https"
        && u.Port == 443
        && u.UserInfo.Length == 0
        && (u.Host is "i.redd.it" or "preview.redd.it" or "external-preview.redd.it");

    public static PostContent FromListing(JsonNode data)
    {
        var result = new PostContent
        {
            Title = Text(data, "title"),
            Community = Text(data, "subreddit"),
            Nsfw = Flag(data, "over_18"),
            Flair = Text(data, "link_flair_text"),
            FlairId = Text(data, "link_flair_template_id"),
            Body = Text(data, "selftext"),
            Source = "reddit",
            RedditId = Text(data, "id"),
        };
        if (data["gallery_data"]?["items"] is JsonArray images)
        {
            foreach (var i in images)
            {
                var id = Text(i, "media_id");
                var meta = data["media_metadata"]?[id];
                var url = WebUtility.HtmlDecode(Text(meta?["s"], "u"));
                if (url.Length > 0)
                    result.Images.Add(new() { RemoteUrl = url, Caption = Text(i, "caption") });
            }
        }
        else
        {
            var url = WebUtility.HtmlDecode(
                Text(data, "url_overridden_by_dest", Text(data, "url"))
            );
            if (
                DownloadAllowed(url)
                && Regex.IsMatch(
                    new Uri(url).AbsolutePath,
                    "\\.(jpg|jpeg|png|webp)$",
                    RegexOptions.IgnoreCase
                )
            )
                result.Images.Add(new() { RemoteUrl = url });
        }
        return result;
    }

    private async Task<bool> DownloadMedia(MediaItem image, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(image.File) && File.Exists(store.MediaPath(image.File)))
            return false;
        if (!DownloadAllowed(image.RemoteUrl))
            throw new ApiFailure("Внешнее медиа не скачивается автоматически");
        using var request = new HttpRequestMessage(HttpMethod.Get, image.RemoteUrl);
        using var response = await media.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );
        if (!response.IsSuccessStatusCode)
            throw new ApiFailure("Изображение недоступно");
        if (response.Content.Headers.ContentLength > 20 * 1024 * 1024)
            throw new ApiFailure("Изображение больше 20 МБ");
        using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + n > 20 * 1024 * 1024)
                throw new ApiFailure("Изображение больше 20 МБ");
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
        }
        image.File = store.ImportBytes(output.ToArray(), image.Caption).File;
        return true;
    }

    public async Task<ImportResult> ImportMine(
        bool download,
        Action<string> progress,
        CancellationToken ct
    )
    {
        var account = await Check(ct);
        var received = 0;
        var downloaded = 0;
        var failures = 0;
        string? after = null;
        var cursors = new HashSet<string>();
        var limited = false;
        try
        {
            for (var page = 0; page < 100; page++)
            {
                ct.ThrowIfCancellationRequested();
                var path =
                    $"/user/{Uri.EscapeDataString(account)}/submitted?limit=100&sort=new&raw_json=1"
                    + (after is null ? "" : "&after=" + Uri.EscapeDataString(after));
                var listing = await Request(HttpMethod.Get, path, ct: ct);
                var children =
                    listing["data"]?["children"]?.AsArray()
                    ?? throw new ApiFailure("Не удалось прочитать список постов");
                foreach (var child in children)
                {
                    ct.ThrowIfCancellationRequested();
                    var data = child?["data"];
                    if (data is null)
                        continue;
                    var redditId = Text(data, "id");
                    if (!Regex.IsMatch(redditId, "^[a-z0-9]+$"))
                        continue;
                    var content = FromListing(data);
                    if (content.Images.Count == 0)
                        continue;
                    var permalink = "https://www.reddit.com" + Text(data, "permalink");
                    if (!ValidPostUrl(permalink))
                        continue;
                    var id = store.ImportPost(
                        redditId,
                        content,
                        data["created_utc"]!.GetValue<double>(),
                        permalink
                    );
                    var local = store.Get(id);
                    if (local.Content.Source == "reddit" && download)
                    {
                        var error = "";
                        foreach (var image in local.Content.Images)
                        {
                            try
                            {
                                if (await DownloadMedia(image, ct))
                                    downloaded++;
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception e)
                                when (e
                                        is ApiFailure
                                            or HttpRequestException
                                            or InvalidOperationException
                                            or IOException
                                )
                            {
                                failures++;
                                error = "Часть изображений не скачана. Повторите синхронизацию.";
                            }
                        }
                        store.UpdateImportedMedia(id, local.Content, error);
                    }
                    received++;
                    progress($"Получено постов: {received} · скачано изображений: {downloaded}");
                }
                after = Text(listing["data"], "after");
                if (string.IsNullOrEmpty(after) || children.Count == 0)
                    break;
                if (!cursors.Add(after))
                {
                    limited = true;
                    break;
                }
                if (page == 99)
                    limited = true;
            }
        }
        catch (OperationCanceledException)
        {
            return new(received, downloaded, failures, limited, true);
        }
        store.Set("last_import", Clock.Now);
        store.Set("last_import_count", received);
        return new(received, downloaded, failures, limited);
    }
}
