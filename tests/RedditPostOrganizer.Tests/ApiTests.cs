using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RedditPostOrganizer.Core;
using Xunit;

namespace RedditPostOrganizer.Tests;

public class MemoryVault : ITokenVault
{
    public TokenRecord? Value = new()
    {
        Account = "owner",
        ClientId = "test-id",
        AccessToken = "test-access",
        RefreshToken = "test-refresh",
        ExpiresAt = Clock.Now + 3600,
    };

    public TokenRecord? Load() => Value;

    public void Save(TokenRecord v) => Value = v;

    public void Clear() => Value = null;
}

public class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage r,
        CancellationToken ct
    ) => handle(r);

    public static HttpResponseMessage Reply(
        string json,
        HttpStatusCode status = HttpStatusCode.OK
    ) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class ApiTests
{
    [Theory]
    [InlineData("asset-a", true)]
    [InlineData("different-asset", false)]
    public async Task VerificationRequiresExactUploadedAssets(string returnedId, bool expected)
    {
        using var f = new Fixture();
        f.Store.Set("api_client_id", "test-id");
        f.Store.Set("account", "owner");
        var c = f.Content();
        var listing = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["children"] = new JsonArray(
                    new JsonObject
                    {
                        ["data"] = new JsonObject
                        {
                            ["title"] = c.Title,
                            ["subreddit"] = c.Community,
                            ["permalink"] = "/r/test/comments/abc/title/",
                            ["gallery_data"] = new JsonObject
                            {
                                ["items"] = new JsonArray(
                                    new JsonObject { ["media_id"] = returnedId },
                                    new JsonObject { ["media_id"] = "asset-b" }
                                ),
                            },
                        },
                    }
                ),
            },
        };
        using var api = new RedditApi(
            f.Store,
            new HttpClient(
                new Handler(r =>
                    Task.FromResult(
                        Handler.Reply(
                            r.RequestUri!.AbsolutePath == "/api/v1/me"
                                ? "{\"name\":\"owner\"}"
                                : listing.ToJsonString()
                        )
                    )
                )
            ),
            vault: new MemoryVault()
        );
        var result = await api.CheckResult(c, ["asset-a", "asset-b"], default);
        Assert.Equal(expected, result is not null);
    }

    [Theory]
    [InlineData("/callback?state=right&code=x", true)]
    [InlineData("/callback?state=wrong&code=x", false)]
    [InlineData("/callback?state=right&state=right&code=x", false)]
    [InlineData("/other?state=right&code=x", false)]
    public void CallbackChecksStateAndRejectsDuplicates(string path, bool valid) =>
        Assert.Equal(valid, RedditApi.TryCallback(path, "right", out _, out _));

    [Theory]
    [InlineData("https://i.redd.it/a.jpg", true)]
    [InlineData("https://i.redd.it.evil.test/a.jpg", false)]
    [InlineData("http://i.redd.it/a.jpg", false)]
    [InlineData("https://owner@i.redd.it/a.jpg", false)]
    [InlineData("https://i.redd.it:123/a.jpg", false)]
    public void MediaDownloadDoesNotFollowArbitraryUrls(string url, bool allowed) =>
        Assert.Equal(allowed, RedditApi.DownloadAllowed(url));

    [Fact]
    public async Task ImportPagesOnlyOwnImagesAndCachesMedia()
    {
        using var f = new Fixture();
        f.Store.Set("api_client_id", "test-id");
        f.Store.Set("account", "owner");
        int pages = 0,
            downloads = 0;
        var apiHandler = new Handler(r =>
        {
            Assert.Equal("test-access", r.Headers.Authorization!.Parameter);
            if (r.RequestUri!.AbsolutePath == "/api/v1/me")
                return Task.FromResult(Handler.Reply("{\"name\":\"owner\"}"));
            Assert.Equal("/user/owner/submitted", r.RequestUri.AbsolutePath);
            pages++;
            var page = r.RequestUri.Query.Contains("after=")
                ? "{\"data\":{\"children\":[],\"after\":null}}"
                : """
                    {"data":{"after":"t3_abc","children":[{"data":{"id":"abc","title":"My gallery","subreddit":"test","created_utc":1000,"over_18":true,"permalink":"/r/test/comments/abc/title/","gallery_data":{"items":[{"media_id":"two","caption":"Second"},{"media_id":"one","caption":"First"}]},"media_metadata":{"one":{"s":{"u":"https://i.redd.it/one.png"}},"two":{"s":{"u":"https://i.redd.it/two.png"}}}}},{"data":{"id":"txt","title":"Text only","subreddit":"test","created_utc":2000,"permalink":"/r/test/comments/txt/title/"}}]}}
                    """;
            return Task.FromResult(Handler.Reply(page));
        });
        var mediaHandler = new Handler(r =>
        {
            Assert.Null(r.Headers.Authorization);
            downloads++;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Fixture.Png),
                }
            );
        });
        using var api = new RedditApi(
            f.Store,
            new HttpClient(apiHandler),
            new HttpClient(mediaHandler),
            new MemoryVault()
        );
        var result = await api.ImportMine(true, _ => { }, default);
        Assert.Equal(1, result.Received);
        Assert.Equal(2, result.Downloaded);
        Assert.Equal(2, pages);
        Assert.Equal("Second", Assert.Single(f.Store.Posts()).Content.Images[0].Caption);
        Assert.True(f.Store.Posts()[0].Content.Nsfw);
        Assert.Equal("published", f.Store.Posts()[0].Status);
        Assert.Equal(1000, f.Store.Posts()[0].Scheduled);
        await api.ImportMine(true, _ => { }, default);
        Assert.Equal(2, downloads);
        Assert.Single(f.Store.Posts());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationUsesNativeGalleryPayloadAndDoesNotRetryLostResponse(bool lost)
    {
        using var f = new Fixture();
        f.Store.Set("api_client_id", "test-id");
        f.Store.Set("account", "owner");
        var id = f.Store.Save(f.Content(), 1000);
        int submits = 0,
            assets = 0,
            uploads = 0;
        JsonNode? sent = null;
        var handler = new Handler(async r =>
        {
            switch (r.RequestUri!.AbsolutePath)
            {
                case "/api/v1/me":
                    return Handler.Reply("{\"name\":\"owner\"}");
                case "/r/test/about":
                    return Handler.Reply("{\"data\":{\"allow_galleries\":true}}");
                case "/r/test/api/flairselector/":
                    return Handler.Reply(
                        "{\"choices\":[{\"flair_template_id\":\"flair-id\",\"flair_text\":\"Art\"}]}"
                    );
                case "/api/media/asset.json":
                    assets++;
                    return Handler.Reply(
                        "{\"args\":{\"action\":\"https://bucket.s3.amazonaws.com/upload\",\"fields\":[{\"name\":\"key\",\"value\":\"image\"}]},\"asset\":{\"asset_id\":\"asset"
                            + assets
                            + "\"}}"
                    );
                case "/api/submit_gallery_post.json":
                    submits++;
                    sent = JsonNode.Parse(await r.Content!.ReadAsStringAsync());
                    if (lost)
                        throw new HttpRequestException("lost");
                    return Handler.Reply(
                        "{\"json\":{\"errors\":[],\"data\":{\"url\":\"https://www.reddit.com/r/test/comments/abc/title/\"}}}"
                    );
                default:
                    return Handler.Reply("{\"data\":{\"children\":[]}}");
            }
        });
        var media = new Handler(async r =>
        {
            Assert.Null(r.Headers.Authorization);
            Assert.Contains("multipart/form-data", r.Content!.Headers.ContentType!.ToString());
            await r.Content.ReadAsByteArrayAsync();
            uploads++;
            return Handler.Reply("");
        });
        using var api = new RedditApi(
            f.Store,
            new HttpClient(handler),
            new HttpClient(media),
            new MemoryVault()
        );
        if (lost)
        {
            var error = await Assert.ThrowsAsync<ApiFailure>(() => api.Send(f.Store.Get(id)));
            Assert.Equal("uncertain", error.Kind);
        }
        else
            Assert.Contains("/comments/abc/", await api.Send(f.Store.Get(id)));
        Assert.Equal(1, submits);
        Assert.Equal(2, uploads);
        Assert.True(sent!["nsfw"]!.GetValue<bool>());
        Assert.Equal("flair-id", sent["flair_id"]!.GetValue<string>());
        Assert.Equal("Первое", sent["items"]![0]!["caption"]!.GetValue<string>());
        Assert.Equal("Второе", sent["items"]![1]!["caption"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeletedOrAmbiguousFlairBlocksSending()
    {
        using var f = new Fixture();
        f.Store.Set("api_client_id", "test-id");
        using var api = new RedditApi(
            f.Store,
            new HttpClient(
                new Handler(_ =>
                    Task.FromResult(
                        Handler.Reply(
                            "{\"choices\":[{\"flair_template_id\":\"a\",\"flair_text\":\"Art\"},{\"flair_template_id\":\"b\",\"flair_text\":\"Art\"}]}"
                        )
                    )
                )
            ),
            vault: new MemoryVault()
        );
        var c = f.Content();
        await Assert.ThrowsAsync<ApiFailure>(() => api.ResolveFlair(c, default));
        c.FlairId = null;
        await Assert.ThrowsAsync<ApiFailure>(() => api.ResolveFlair(c, default));
    }

    [Theory]
    [InlineData(401, "auth")]
    [InlineData(503, "network")]
    [InlineData(429, "rate")]
    [InlineData(403, "auth")]
    public async Task CheckClassifiesFailures(int status, string kind)
    {
        using var f = new Fixture();
        f.Store.Set("api_client_id", "test-id");
        using var api = new RedditApi(
            f.Store,
            new HttpClient(
                new Handler(_ => Task.FromResult(Handler.Reply("{}", (HttpStatusCode)status)))
            ),
            vault: new MemoryVault()
        );
        Assert.Equal(kind, (await Assert.ThrowsAsync<ApiFailure>(() => api.Check())).Kind);
    }
}
