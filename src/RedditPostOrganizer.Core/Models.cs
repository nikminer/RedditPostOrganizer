using System.Text.Json;
using System.Text.Json.Serialization;

namespace RedditPostOrganizer.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static string Write<T>(T item) => JsonSerializer.Serialize(item, Options);

    public static T Read<T>(string text) => JsonSerializer.Deserialize<T>(text, Options)!;
}

public sealed record MediaItem
{
    public string File { get; set; } = "";
    public string Caption { get; set; } = "";
    public string RemoteUrl { get; set; } = "";

    public override string ToString() =>
        (string.IsNullOrEmpty(Caption) ? "Без подписи" : Caption)
        + (string.IsNullOrEmpty(File) ? " · не скачано" : "");
}

public sealed record PostContent
{
    public string Title { get; set; } = "";
    public string Community { get; set; } = "";
    public string Flair { get; set; } = "";
    public string? FlairId { get; set; }
    public bool Nsfw { get; set; }
    public List<string> Tags { get; set; } = [];
    public string Notes { get; set; } = "";
    public List<MediaItem> Images { get; set; } = [];
    public string Body { get; set; } = "";
    public string Source { get; set; } = "local";
    public string? RedditId { get; set; }

    public PostContent DeepCopy() => Json.Read<PostContent>(Json.Write(this));
}

public sealed record Post(
    string Id,
    PostContent Content,
    string Status,
    double? Scheduled,
    double? Original,
    string Url,
    string Error,
    double Created,
    double Updated
)
{
    public bool Editable => Status is "draft" or "scheduled" or "error";
    public static readonly IReadOnlyDictionary<string, string> States = new Dictionary<
        string,
        string
    >
    {
        ["draft"] = "Черновик",
        ["scheduled"] = "Запланирован",
        ["sending"] = "Отправляется",
        ["published"] = "Опубликован",
        ["error"] = "Ошибка",
        ["review"] = "Требуется проверка",
    };
    public string StatusLabel => States.GetValueOrDefault(Status, Status);

    public override string ToString() => Content.Title;
}

public record Flair(string Id, string Text)
{
    public override string ToString() => string.IsNullOrEmpty(Text) ? "Без метки" : Text;
}

public record ImportResult(
    int Received,
    int Downloaded,
    int MediaErrors,
    bool Limited,
    bool Cancelled = false
);

public sealed class ApiFailure(string message, string kind = "error", int seconds = 900)
    : Exception(message)
{
    public string Kind { get; } = kind;
    public int Seconds { get; } = seconds;
}

public static class Clock
{
    public static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public static DateTime Local(double unix, string zone) =>
        TimeZoneInfo
            .ConvertTime(
                DateTimeOffset.FromUnixTimeMilliseconds((long)(unix * 1000)),
                TimeZoneInfo.FindSystemTimeZoneById(zone)
            )
            .DateTime;

    public static string Display(double? unix, string zone) =>
        unix is { } n ? Local(n, zone).ToString("dd.MM.yyyy HH:mm") : "—";

    public static double[] Candidates(DateTime local, string zone)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(zone);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local))
            return [];
        var offsets = tz.IsAmbiguousTime(local)
            ? tz.GetAmbiguousTimeOffsets(local)
            : [tz.GetUtcOffset(local)];
        return offsets
            .Select(o => new DateTimeOffset(local, o).ToUnixTimeSeconds() * 1.0)
            .Order()
            .ToArray();
    }
}
