using System.IO.Compression;
using RedditPostOrganizer.Core;
using Xunit;

namespace RedditPostOrganizer.Tests;

public sealed class Fixture : IDisposable
{
    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), "organizer-test-" + Guid.NewGuid());
    public Store Store { get; }
    public static byte[] Png =>
        Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAAAwCAIAAAAuKetIAAAAaElEQVR4nNXOQREAIAzAsFKZGMIfihCxB9coyNrnUiZxEidxEidxEidxEidxEidxEidxEidxEidxEidxEidxEidxEidxEidxEidxEidxEidxEidxEidxEidxEidxEidxEidxEufvwNQDat4CEzrNQ8QAAAAASUVORK5CYII="
        );

    public Fixture()
    {
        Store = new(Root);
    }

    public PostContent Content()
    {
        var a = Store.ImportBytes(Png, "Первое");
        return new()
        {
            Title = "Тестовая галерея",
            Community = "test",
            Nsfw = true,
            Flair = "Art",
            FlairId = "flair-id",
            Tags = ["art"],
            Notes = "private",
            Images = [a, a with { Caption = "Второе" }],
        };
    }

    public void Dispose()
    {
        if (
            Path.GetDirectoryName(Root) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            && Path.GetFileName(Root).StartsWith("organizer-test-")
        )
            Directory.Delete(Root, true);
    }
}

public sealed class CoreTests
{
    [Fact]
    public void LegacyDatabaseNameMigratesAndPreservesPosts()
    {
        using var f = new Fixture();
        var id = f.Store.Save(f.Content());
        File.Move(f.Store.DatabasePath, Path.Combine(f.Root, "gallery.sqlite3"));
        var migrated = new Store(f.Root);
        Assert.Equal(id, Assert.Single(migrated.Posts()).Id);
        Assert.True(File.Exists(Path.Combine(f.Root, "publications.sqlite3")));
        Assert.False(File.Exists(Path.Combine(f.Root, "gallery.sqlite3")));
        Assert.Single(new Store(f.Root).Posts());
    }

    [Fact]
    public void LegacyDataFolderMovesOnceWithoutOverwritingAnotherFolder()
    {
        using var f = new Fixture();
        var old = Path.Combine(f.Root, "RedditGallery");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "marker"), "preserved");
        var current = DataPaths.ResolveDefault(f.Root);
        Assert.EndsWith("RedditPostOrganizer", current);
        Assert.Equal("preserved", File.ReadAllText(Path.Combine(current, "marker")));
        Assert.Equal(current, DataPaths.ResolveDefault(f.Root));
        Directory.CreateDirectory(old);
        Assert.Throws<InvalidOperationException>(() => DataPaths.ResolveDefault(f.Root));
    }

    [Fact]
    public void LegacyBackupDatabaseEntryIsStillAccepted()
    {
        using var source = new Fixture();
        var id = source.Store.Save(source.Content());
        var zip = Path.Combine(source.Root, "legacy.zip");
        source.Store.Backup(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            var current = archive.GetEntry("publications.sqlite3")!;
            using (var input = current.Open())
            using (var output = archive.CreateEntry("gallery.sqlite3").Open())
                input.CopyTo(output);
            current.Delete();
        }
        using var destination = new Fixture();
        destination.Store.Restore(zip);
        Assert.Equal(id, Assert.Single(destination.Store.Posts()).Id);
    }

    [Fact]
    public void RestartShiftsWholeQueueOnceAcrossDays()
    {
        using var f = new Fixture();
        var first = f.Store.Save(f.Content(), 1000);
        var later = f.Store.Save(f.Content(), 1000 + 86400 * 3);
        f.Store.Recover(2000);
        Assert.Equal(2300, f.Store.Get(first).Scheduled);
        Assert.Equal(2300 + 86400 * 3, f.Store.Get(later).Scheduled);
        new Store(f.Root).Recover(2001);
        Assert.Equal(2300, f.Store.Get(first).Scheduled);
        Assert.Contains("Возобновление", f.Store.History(first, "UTC"));
    }

    [Fact]
    public void CopiesAreIndependentDraftsWithNsfwAndOrderedCaptions()
    {
        using var f = new Fixture();
        var original = f.Store.Save(f.Content(), Clock.Now + 5000);
        f.Store.Status(
            original,
            "published",
            url: "https://www.reddit.com/r/test/comments/abc/test/"
        );
        var ids = f.Store.Copy(original, 2);
        var edited = f.Store.Get(ids[0]).Content;
        edited.Title = "Changed";
        edited.Tags.Add("new");
        edited.Images.Reverse();
        f.Store.Save(edited, id: ids[0]);
        var other = f.Store.Get(ids[1]);
        Assert.Equal("draft", other.Status);
        Assert.Null(other.Scheduled);
        Assert.Empty(other.Url);
        Assert.True(other.Content.Nsfw);
        Assert.Equal("flair-id", other.Content.FlairId);
        Assert.Equal("Первое", other.Content.Images[0].Caption);
        Assert.Single(other.Content.Tags);
        Assert.Equal("Тестовая галерея", f.Store.Get(original).Content.Title);
    }

    [Fact]
    public void BatchRollsBackWhenAnySelectionInvalid()
    {
        using var f = new Fixture();
        var a = f.Store.Save(f.Content());
        var b = f.Store.Save(f.Content());
        int i = 0;
        Assert.Throws<InvalidOperationException>(() =>
            f.Store.Batch(
                [a, b],
                c =>
                {
                    c.Title = ++i == 2 ? "" : "changed";
                }
            )
        );
        Assert.Equal("Тестовая галерея", f.Store.Get(a).Content.Title);
    }

    [Fact]
    public void InterruptedAttemptRequiresReviewAndIsNeverClaimedAgain()
    {
        using var f = new Fixture();
        var id = f.Store.Save(f.Content(), 1000);
        Assert.NotNull(f.Store.Claim(1001));
        f.Store.Attempt(id);
        f.Store.Recover(2000);
        Assert.Equal("review", f.Store.Get(id).Status);
        Assert.Null(f.Store.Claim(99999));
    }

    [Theory]
    [InlineData("network", 900, "scheduled")]
    [InlineData("rate", 3601, "scheduled")]
    [InlineData("auth", 0, "scheduled")]
    [InlineData("uncertain", 0, "review")]
    [InlineData("error", 0, "error")]
    public void FailuresHaveDifferentQueueEffects(string kind, int seconds, string state)
    {
        using var f = new Fixture();
        var a = f.Store.Save(f.Content(), 1000);
        var b = f.Store.Save(f.Content(), 90000);
        var post = f.Store.Claim(1001)!;
        PublicationService.ApplyFailure(f.Store, post, new ApiFailure("failure", kind, seconds));
        Assert.Equal(state, f.Store.Get(a).Status);
        Assert.Equal(90000 + (kind is "network" or "rate" ? seconds : 0), f.Store.Get(b).Scheduled);
        if (kind == "auth")
            Assert.Equal("failure", f.Store.Setting("pause_reason", ""));
    }

    [Fact]
    public void TimeZoneChangesDoNotAlterScheduleAndDstIsExplicit()
    {
        using var f = new Fixture();
        var id = f.Store.Save(f.Content(), Clock.Now + 9000);
        var time = f.Store.Get(id).Scheduled;
        f.Store.Set("timezone", "America/New_York");
        Assert.Equal(time, f.Store.Get(id).Scheduled);
        Assert.Empty(Clock.Candidates(new DateTime(2026, 3, 8, 2, 30, 0), "America/New_York"));
        Assert.Equal(
            2,
            Clock.Candidates(new DateTime(2026, 11, 1, 1, 30, 0), "America/New_York").Length
        );
    }

    [Fact]
    public void ImportIsIdempotentAndRetainsPrivateMetadata()
    {
        using var f = new Fixture();
        var c = f.Content();
        c.Images.ForEach(i => i.RemoteUrl = "https://i.redd.it/a.png");
        var id = f.Store.ImportPost(
            "abc",
            c,
            1000,
            "https://www.reddit.com/r/test/comments/abc/test/"
        );
        var second = f.Content();
        second.Notes = "";
        second.Tags.Clear();
        second.Images.ForEach(i =>
        {
            i.File = "";
            i.RemoteUrl = "https://i.redd.it/a.png";
        });
        Assert.Equal(
            id,
            f.Store.ImportPost(
                "abc",
                second,
                1000,
                "https://www.reddit.com/r/test/comments/abc/test/"
            )
        );
        Assert.Single(f.Store.Posts());
        Assert.Equal("private", f.Store.Get(id).Content.Notes);
        Assert.NotEmpty(f.Store.Get(id).Content.Images[0].File);
        Assert.Null(f.Store.Claim(Clock.Now));
    }

    [Fact]
    public void BackupExcludesCredentialsAndRestoreKeepsLocalPassword()
    {
        using var f = new Fixture();
        var id = f.Store.Save(f.Content());
        f.Store.SetPassword("password-123");
        f.Store.Set("account", "test-user");
        File.WriteAllText(Path.Combine(f.Root, "oauth-token.bin"), "test-token");
        var zip = Path.Combine(f.Root, "backup.zip");
        f.Store.Backup(zip);
        using (var archive = ZipFile.OpenRead(zip))
            Assert.All(
                archive.Entries,
                e =>
                    Assert.True(
                        e.FullName == "publications.sqlite3" || e.FullName.StartsWith("media/")
                    )
            );
        using var target = new Fixture();
        target.Store.SetPassword("target-password");
        target.Store.Restore(zip);
        Assert.Equal(id, Assert.Single(target.Store.Posts()).Id);
        Assert.Null(target.Store.Setting<string?>("account", null));
        Assert.True(target.Store.CheckPassword("target-password"));
        Assert.False(target.Store.CheckPassword("password-123"));
        Assert.True(
            File.Exists(target.Store.MediaPath(target.Store.Get(id).Content.Images[0].File))
        );
    }

    [Fact]
    public void RestoreRejectsTraversal()
    {
        using var f = new Fixture();
        var zip = Path.Combine(f.Root, "bad.zip");
        using (var a = ZipFile.Open(zip, ZipArchiveMode.Create))
            a.CreateEntry("../outside.txt");
        Assert.Throws<InvalidOperationException>(() => f.Store.Restore(zip));
    }

    [Fact]
    public void LegacyPythonPasswordAndFlairAreReadable()
    {
        using var f = new Fixture();
        var salt = new byte[16];
        var hash = Org.BouncyCastle.Crypto.Generators.SCrypt.Generate(
            System.Text.Encoding.UTF8.GetBytes("legacy"),
            salt,
            16384,
            8,
            1,
            64
        );
        f.Store.Set(
            "password",
            new { salt = Convert.ToHexString(salt), hash = Convert.ToHexString(hash) }
        );
        Assert.True(f.Store.CheckPassword("legacy"));
        Assert.False(f.Store.CheckPassword("wrong"));
        f.Store.MigrateTransport();
        Assert.True(f.Store.Setting("paused", false));
    }

    [Fact]
    public void TokenVaultUsesWindowsEncryption()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var f = new Fixture();
        var vault = new TokenVault(f.Root);
        vault.Save(
            new()
            {
                AccessToken = "secret-access",
                RefreshToken = "secret-refresh",
                ClientId = "test-id",
                ExpiresAt = Clock.Now + 3600,
            }
        );
        Assert.DoesNotContain(
            "secret-access",
            System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(vault.PathName))
        );
        Assert.Equal("secret-refresh", vault.Load()!.RefreshToken);
        vault.Clear();
        Assert.Null(vault.Load());
    }
}
