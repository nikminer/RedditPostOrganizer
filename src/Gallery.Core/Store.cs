using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Gallery.Core;

public sealed class Store
{
    private readonly object gate = new();
    public string Root { get; }
    public string MediaDirectory => Path.Combine(Root, "media");
    public string DatabasePath => Path.Combine(Root, "gallery.sqlite3");

    public Store(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(MediaDirectory);
        using var db = Open();
        if (Convert.ToInt32(Scalar(db, "PRAGMA user_version")) > 1)
            throw new InvalidOperationException("База создана более новой версией программы");
        Exec(
            db,
            """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS posts(id TEXT PRIMARY KEY,content TEXT NOT NULL,status TEXT NOT NULL,scheduled REAL,original REAL,url TEXT NOT NULL DEFAULT '',error TEXT NOT NULL DEFAULT '',created REAL NOT NULL,updated REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS queue_order ON posts(status,scheduled);
            CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY,post_id TEXT,at REAL NOT NULL,kind TEXT NOT NULL,detail TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS attempts(id TEXT PRIMARY KEY,post_id TEXT NOT NULL,at REAL NOT NULL,phase TEXT NOT NULL,url TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS flairs(community TEXT PRIMARY KEY,labels TEXT NOT NULL,updated REAL NOT NULL);
            PRAGMA user_version=1;
            """
        );
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Pooling = false,
            }.ToString()
        );
        db.Open();
        return db;
    }

    private static SqliteCommand Command(
        SqliteConnection db,
        string sql,
        object?[] values,
        SqliteTransaction? tx = null
    )
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        for (var i = 0; i < values.Length; i++)
            cmd.Parameters.AddWithValue("@p" + i, values[i] ?? DBNull.Value);
        return cmd;
    }

    private static void Exec(SqliteConnection db, string sql, params object?[] values)
    {
        using var c = Command(db, sql, values);
        c.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection db, string sql, params object?[] values)
    {
        using var c = Command(db, sql, values);
        return c.ExecuteScalar();
    }

    private static void Tx(
        SqliteConnection db,
        SqliteTransaction tx,
        string sql,
        params object?[] values
    )
    {
        using var c = Command(db, sql, values, tx);
        c.ExecuteNonQuery();
    }

    public T Setting<T>(string key, T fallback)
    {
        lock (gate)
        {
            using var db = Open();
            var value = Scalar(db, "SELECT value FROM settings WHERE key=@p0", key) as string;
            return value is null ? fallback : Json.Read<T>(value);
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (gate)
        {
            using var db = Open();
            Exec(db, "INSERT OR REPLACE INTO settings VALUES(@p0,@p1)", key, Json.Write(value));
        }
    }

    public void MigrateTransport()
    {
        lock (gate)
        {
            if (Setting("transport", "") == "reddit_api_avalonia_v1")
                return;
            using var db = Open();
            using var tx = db.BeginTransaction();
            foreach (
                var (k, v) in new[]
                {
                    ("transport", Json.Write("reddit_api_avalonia_v1")),
                    ("paused", "true"),
                    ("account", "null"),
                    ("pause_reason", "\"\""),
                }
            )
                Tx(db, tx, "INSERT OR REPLACE INTO settings VALUES(@p0,@p1)", k, v);
            tx.Commit();
        }
    }

    private static Post Decode(SqliteDataReader r) =>
        new(
            r.GetString(0),
            Json.Read<PostContent>(r.GetString(1)),
            r.GetString(2),
            r.IsDBNull(3) ? null : r.GetDouble(3),
            r.IsDBNull(4) ? null : r.GetDouble(4),
            r.GetString(5),
            r.GetString(6),
            r.GetDouble(7),
            r.GetDouble(8)
        );

    public List<Post> Posts()
    {
        lock (gate)
        {
            using var db = Open();
            using var cmd = Command(
                db,
                "SELECT id,content,status,scheduled,original,url,error,created,updated FROM posts ORDER BY scheduled IS NULL,scheduled,created,id",
                []
            );
            using var r = cmd.ExecuteReader();
            var posts = new List<Post>();
            while (r.Read())
                posts.Add(Decode(r));
            return posts;
        }
    }

    public Post Get(string id) =>
        Posts().FirstOrDefault(p => p.Id == id)
        ?? throw new InvalidOperationException("Пост не найден");

    public string MediaPath(string name)
    {
        if (!Regex.IsMatch(name, "^[a-f0-9]{64}\\.(jpg|png|webp)$"))
            throw new InvalidOperationException("Некорректный путь изображения");
        return Path.Combine(MediaDirectory, name);
    }

    public MediaItem ImportBytes(byte[] bytes, string caption = "")
    {
        if (bytes.Length > 20 * 1024 * 1024 || bytes.Length < 12)
            throw new InvalidOperationException("Изображение должно быть размером до 20 МБ");
        var ext =
            bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                ? ".png"
            : bytes[0] == 255 && bytes[1] == 216 ? ".jpg"
            : Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF"
            && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP"
                ? ".webp"
            : throw new InvalidOperationException("Поддерживаются JPEG, PNG и WebP");
        var name = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ext;
        lock (gate)
        {
            var path = MediaPath(name);
            if (!File.Exists(path))
                File.WriteAllBytes(path, bytes);
        }
        return new MediaItem { File = name, Caption = caption };
    }

    public MediaItem ImportFile(string path)
    {
        if (new FileInfo(path).Length > 20 * 1024 * 1024)
            throw new InvalidOperationException("Изображение больше 20 МБ");
        return ImportBytes(File.ReadAllBytes(path));
    }

    public void Validate(PostContent c, bool scheduled)
    {
        if (string.IsNullOrWhiteSpace(c.Title) || c.Title.Length > 300)
            throw new InvalidOperationException("Заголовок: от 1 до 300 символов");
        if (scheduled && !Regex.IsMatch(c.Community, "^[A-Za-z0-9_]{2,32}$"))
            throw new InvalidOperationException("Укажите имя сообщества без r/");
        if (c.Images.Count > 20 || (scheduled && c.Images.Count < 2))
            throw new InvalidOperationException(
                "Для публикации галереи нужно от 2 до 20 изображений"
            );
        foreach (var image in c.Images)
        {
            if (string.IsNullOrEmpty(image.File) || !File.Exists(MediaPath(image.File)))
                throw new InvalidOperationException(
                    "Изображение не скачано или отсутствует. Сначала скачайте медиа."
                );
            if (image.Caption.Length > 180)
                throw new InvalidOperationException("Подпись длиннее 180 символов");
        }
    }

    public string Save(PostContent content, double? scheduled = null, string? id = null)
    {
        Validate(content, scheduled.HasValue);
        lock (gate)
        {
            using var db = Open();
            var now = Clock.Now;
            if (id is not null)
            {
                if (!Get(id).Editable)
                    throw new InvalidOperationException(
                        "Создайте копию опубликованного или непроверенного поста"
                    );
                Exec(
                    db,
                    "UPDATE posts SET content=@p0,status=@p1,scheduled=@p2,original=@p2,error='',updated=@p3 WHERE id=@p4",
                    Json.Write(content),
                    scheduled.HasValue ? "scheduled" : "draft",
                    scheduled,
                    now,
                    id
                );
            }
            else
            {
                id = Guid.NewGuid().ToString("N");
                Exec(
                    db,
                    "INSERT INTO posts(id,content,status,scheduled,original,created,updated) VALUES(@p0,@p1,@p2,@p3,@p3,@p4,@p4)",
                    id,
                    Json.Write(content),
                    scheduled.HasValue ? "scheduled" : "draft",
                    scheduled,
                    now
                );
            }
            return id;
        }
    }

    public List<string> Copy(string id, int count, double? start = null, int intervalMinutes = 120)
    {
        if (count < 1 || count > 500 || intervalMinutes < 1)
            throw new InvalidOperationException("Количество: 1–500, интервал: от 1 минуты");
        var c = Get(id).Content.DeepCopy();
        c.Source = "local";
        c.RedditId = null;
        Validate(c, start.HasValue);
        lock (gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            var ids = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var key = Guid.NewGuid().ToString("N");
                var when = start + i * intervalMinutes * 60;
                Tx(
                    db,
                    tx,
                    "INSERT INTO posts(id,content,status,scheduled,original,created,updated) VALUES(@p0,@p1,@p2,@p3,@p3,@p4,@p4)",
                    key,
                    Json.Write(c),
                    when.HasValue ? "scheduled" : "draft",
                    when,
                    Clock.Now
                );
                ids.Add(key);
            }
            tx.Commit();
            return ids;
        }
    }

    public void Batch(
        List<string> ids,
        Action<PostContent> change,
        double? start = null,
        int interval = 120,
        bool unschedule = false
    )
    {
        lock (gate)
        {
            var posts = ids.Select(Get).ToList();
            foreach (var p in posts)
                if (!p.Editable)
                    throw new InvalidOperationException(
                        "В выборе есть опубликованный или непроверенный пост"
                    );
            using var db = Open();
            using var tx = db.BeginTransaction();
            for (int i = 0; i < posts.Count; i++)
            {
                var p = posts[i];
                var content = p.Content.DeepCopy();
                change(content);
                var when =
                    unschedule ? null
                    : start.HasValue ? start + i * interval * 60
                    : p.Scheduled;
                Validate(content, when.HasValue);
                Tx(
                    db,
                    tx,
                    "UPDATE posts SET content=@p0,status=@p1,scheduled=@p2,original=@p2,error='',updated=@p3 WHERE id=@p4",
                    Json.Write(content),
                    when.HasValue ? "scheduled" : "draft",
                    when,
                    Clock.Now,
                    p.Id
                );
            }
            tx.Commit();
        }
    }

    private static void Shift(
        SqliteConnection db,
        SqliteTransaction tx,
        List<Post> posts,
        double seconds,
        string reason,
        double now
    )
    {
        if (seconds <= 0)
            return;
        foreach (var p in posts.Where(p => p.Status == "scheduled"))
        {
            Tx(
                db,
                tx,
                "INSERT INTO events(post_id,at,kind,detail) VALUES(@p0,@p1,'shift',@p2)",
                p.Id,
                now,
                Json.Write(
                    new
                    {
                        from = p.Scheduled,
                        to = p.Scheduled + seconds,
                        reason,
                    }
                )
            );
        }
        Tx(
            db,
            tx,
            "UPDATE posts SET scheduled=scheduled+@p0,updated=@p1 WHERE status='scheduled'",
            seconds,
            now
        );
    }

    public void Recover(double? at = null)
    {
        lock (gate)
        {
            var now = at ?? Clock.Now;
            var posts = Posts();
            using var db = Open();
            using var tx = db.BeginTransaction();
            Tx(
                db,
                tx,
                "UPDATE posts SET status='review',error='Работа прервана. Проверьте результат на Reddit.',updated=@p0 WHERE status='sending'",
                now
            );
            var first = posts.Where(p => p.Status == "scheduled").Select(p => p.Scheduled).Min();
            if (first < now)
                Shift(
                    db,
                    tx,
                    posts,
                    now + Setting("startup_minutes", 5) * 60 - first.Value,
                    "Возобновление работы",
                    now
                );
            tx.Commit();
        }
    }

    public Post? Claim(double? at = null)
    {
        lock (gate)
        {
            var p = Posts()
                .FirstOrDefault(p => p.Status == "scheduled" && p.Scheduled <= (at ?? Clock.Now));
            if (p is null)
                return null;
            Status(p.Id, "sending");
            return p;
        }
    }

    public void Status(string id, string status, string message = "", string url = "")
    {
        lock (gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            Tx(
                db,
                tx,
                "UPDATE posts SET status=@p0,error=@p1,url=@p2,updated=@p3 WHERE id=@p4",
                status,
                message,
                url,
                Clock.Now,
                id
            );
            Tx(
                db,
                tx,
                "INSERT INTO events(post_id,at,kind,detail) VALUES(@p0,@p1,@p2,@p3)",
                id,
                Clock.Now,
                status,
                string.IsNullOrEmpty(message) ? url : message
            );
            tx.Commit();
        }
    }

    public void Defer(string id, int seconds, string reason)
    {
        lock (gate)
        {
            var posts = Posts()
                .Select(p => p.Id == id ? p with { Status = "scheduled" } : p)
                .ToList();
            using var db = Open();
            using var tx = db.BeginTransaction();
            Tx(db, tx, "UPDATE posts SET status='scheduled',error=@p0 WHERE id=@p1", reason, id);
            Shift(db, tx, posts, seconds, reason, Clock.Now);
            tx.Commit();
        }
    }

    public string Attempt(string id)
    {
        var key = Guid.NewGuid().ToString("N");
        lock (gate)
        {
            using var db = Open();
            Exec(
                db,
                "INSERT INTO attempts(id,post_id,at,phase) VALUES(@p0,@p1,@p2,'api_submit_pending')",
                key,
                id,
                Clock.Now
            );
        }
        return key;
    }

    public void Confirm(string id, string url)
    {
        lock (gate)
        {
            using var db = Open();
            Exec(db, "UPDATE attempts SET phase='confirmed',url=@p0 WHERE id=@p1", url, id);
        }
    }

    public string History(string id, string zone)
    {
        lock (gate)
        {
            using var db = Open();
            using var cmd = Command(
                db,
                "SELECT at,kind,detail FROM events WHERE post_id=@p0 ORDER BY at DESC",
                [id]
            );
            using var r = cmd.ExecuteReader();
            var lines = new List<string>();
            while (r.Read())
            {
                var kind = r.GetString(1);
                var detail = r.GetString(2);
                if (kind == "shift")
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(detail);
                    var e = doc.RootElement;
                    detail =
                        $"{Clock.Display(e.GetProperty("from").GetDouble(), zone)} → {Clock.Display(e.GetProperty("to").GetDouble(), zone)}\n{e.GetProperty("reason").GetString()}";
                }
                lines.Add(
                    $"{Clock.Display(r.GetDouble(0), zone)} · {Post.States.GetValueOrDefault(kind, "Перенос")}\n{detail}"
                );
            }
            return string.Join("\n\n", lines);
        }
    }

    public void CacheFlairs(string community, List<Flair> flairs)
    {
        lock (gate)
        {
            using var db = Open();
            Exec(
                db,
                "INSERT OR REPLACE INTO flairs VALUES(@p0,@p1,@p2)",
                community.ToLowerInvariant(),
                Json.Write(flairs),
                Clock.Now
            );
        }
    }

    public List<Flair> Flairs(string community)
    {
        lock (gate)
        {
            using var db = Open();
            var data =
                Scalar(
                    db,
                    "SELECT labels FROM flairs WHERE community=@p0",
                    community.ToLowerInvariant()
                ) as string;
            if (data is null)
                return [];
            try
            {
                return Json.Read<List<Flair>>(data);
            }
            catch
            {
                return Json.Read<List<string>>(data).Select(x => new Flair("", x)).ToList();
            }
        }
    }

    public string ImportPost(string redditId, PostContent content, double created, string url)
    {
        lock (gate)
        {
            var existing = Posts()
                .FirstOrDefault(p =>
                    p.Content.RedditId == redditId
                    || p.Url.Contains(
                        "/comments/" + redditId + "/",
                        StringComparison.OrdinalIgnoreCase
                    )
                );
            if (existing is not null && existing.Content.Source != "reddit")
                return existing.Id;
            if (existing is not null)
            {
                content.Notes = existing.Content.Notes;
                content.Tags = existing.Content.Tags;
                for (var i = 0; i < content.Images.Count; i++)
                {
                    var prior = existing.Content.Images.FirstOrDefault(x =>
                        x.RemoteUrl == content.Images[i].RemoteUrl
                    );
                    if (prior is not null && File.Exists(MediaPathOrEmpty(prior.File)))
                        content.Images[i].File = prior.File;
                }
            }
            content.Source = "reddit";
            content.RedditId = redditId;
            var id = existing?.Id ?? "reddit_" + redditId;
            using var db = Open();
            Exec(
                db,
                "INSERT INTO posts(id,content,status,scheduled,original,url,error,created,updated) VALUES(@p0,@p1,'published',@p2,@p2,@p3,'',@p2,@p4) ON CONFLICT(id) DO UPDATE SET content=excluded.content,scheduled=excluded.scheduled,url=excluded.url,updated=excluded.updated",
                id,
                Json.Write(content),
                created,
                url,
                Clock.Now
            );
            return id;
        }
    }

    private string MediaPathOrEmpty(string file) =>
        string.IsNullOrEmpty(file) ? "" : MediaPath(file);

    public void UpdateImportedMedia(string id, PostContent content, string error = "")
    {
        lock (gate)
        {
            using var db = Open();
            Exec(
                db,
                "UPDATE posts SET content=@p0,error=@p1,updated=@p2 WHERE id=@p3 AND status='published'",
                Json.Write(content),
                error,
                Clock.Now,
                id
            );
        }
    }

    public void SetPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600000, HashAlgorithmName.SHA256, 32);
        Set(
            "password",
            new
            {
                algorithm = "pbkdf2",
                salt = Convert.ToHexString(salt),
                hash = Convert.ToHexString(hash),
            }
        );
    }

    public bool HasPassword => Setting<System.Text.Json.JsonElement?>("password", null).HasValue;

    public bool CheckPassword(string password)
    {
        var record = Setting<System.Text.Json.JsonElement?>("password", null);
        if (record is null)
            return true;
        var r = record.Value;
        var salt = Convert.FromHexString(r.GetProperty("salt").GetString()!);
        var expected = Convert.FromHexString(r.GetProperty("hash").GetString()!);
        var actual =
            r.TryGetProperty("algorithm", out var a) && a.GetString() == "pbkdf2"
                ? Rfc2898DeriveBytes.Pbkdf2(
                    password,
                    salt,
                    600000,
                    HashAlgorithmName.SHA256,
                    expected.Length
                )
                : Org.BouncyCastle.Crypto.Generators.SCrypt.Generate(
                    Encoding.UTF8.GetBytes(password),
                    salt,
                    16384,
                    8,
                    1,
                    expected.Length
                );
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public void Backup(string destination)
    {
        lock (gate)
        {
            var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite3");
            try
            {
                using (var db = Open())
                using (var copy = new SqliteConnection("Pooling=false;Data Source=" + temp))
                {
                    copy.Open();
                    db.BackupDatabase(copy);
                    Exec(
                        copy,
                        "DELETE FROM settings WHERE key IN ('password','account','autostart'); INSERT OR REPLACE INTO settings VALUES('paused','true');"
                    );
                }
                using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);
                zip.CreateEntryFromFile(temp, "gallery.sqlite3");
                foreach (var file in Directory.EnumerateFiles(MediaDirectory))
                    zip.CreateEntryFromFile(file, "media/" + Path.GetFileName(file));
            }
            finally
            {
                File.Delete(temp);
            }
        }
    }

    public void Restore(string source)
    {
        lock (gate)
        {
            var stage = Path.Combine(Root, "restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                using (var zip = ZipFile.OpenRead(source))
                {
                    if (zip.Entries.Sum(e => e.Length) > 20L * 1024 * 1024 * 1024)
                        throw new InvalidOperationException("Архив больше 20 ГБ");
                    foreach (var e in zip.Entries)
                    {
                        if (
                            e.FullName != "gallery.sqlite3"
                            && !Regex.IsMatch(e.FullName, "^media/[a-f0-9]{64}\\.(jpg|png|webp)$")
                        )
                            throw new InvalidOperationException("Неподдерживаемый файл в архиве");
                        var target = Path.Combine(stage, e.FullName);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        e.ExtractToFile(target);
                    }
                }
                using var incoming = new SqliteConnection(
                    "Pooling=false;Data Source=" + Path.Combine(stage, "gallery.sqlite3")
                );
                incoming.Open();
                if (
                    (string?)Scalar(incoming, "PRAGMA integrity_check") != "ok"
                    || Convert.ToInt32(Scalar(incoming, "PRAGMA user_version")) != 1
                )
                    throw new InvalidOperationException("Повреждённая или несовместимая база");
                using (var cmd = Command(incoming, "SELECT content FROM posts", []))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        foreach (
                            var i in Json.Read<PostContent>(r.GetString(0))
                                .Images.Where(i => !string.IsNullOrEmpty(i.File))
                        )
                        {
                            MediaPath(i.File);
                            if (!File.Exists(Path.Combine(stage, "media", i.File)))
                                throw new InvalidOperationException(
                                    "В архиве отсутствует изображение"
                                );
                        }
                    }
                }
                var password = Setting<System.Text.Json.JsonElement?>("password", null);
                var autostart = Setting("autostart", false);
                Exec(
                    incoming,
                    "DELETE FROM settings WHERE key IN ('password','account','autostart'); INSERT OR REPLACE INTO settings VALUES('paused','true');"
                );
                if (password is not null)
                    Exec(
                        incoming,
                        "INSERT INTO settings VALUES('password',@p0)",
                        password.Value.GetRawText()
                    );
                Exec(
                    incoming,
                    "INSERT INTO settings VALUES('autostart',@p0)",
                    Json.Write(autostart)
                );
                if (Directory.Exists(Path.Combine(stage, "media")))
                    foreach (var file in Directory.EnumerateFiles(Path.Combine(stage, "media")))
                        File.Copy(file, Path.Combine(MediaDirectory, Path.GetFileName(file)), true);
                using (var target = Open())
                    incoming.BackupDatabase(target);
                File.Delete(Path.Combine(Root, "oauth-token.bin"));
                Recover();
            }
            finally
            {
                if (Path.GetDirectoryName(Path.GetFullPath(stage)) == Root)
                    Directory.Delete(stage, true);
            }
        }
    }
}
