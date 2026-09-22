from __future__ import annotations

import hashlib
import hmac
import json
import os
import re
import shutil
import sqlite3
import tempfile
import time
import uuid
import zipfile
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from zoneinfo import ZoneInfo

from PIL import Image

STATES = {"draft": "Черновик", "scheduled": "Запланирован", "sending": "Отправляется",
          "published": "Опубликован", "error": "Ошибка", "review": "Требуется проверка"}
EDITABLE = {"draft", "scheduled", "error"}


def data_directory():
    return Path(os.environ.get("LOCALAPPDATA", Path.home() / ".local/share")) / "RedditGallery"


def local_candidates(value: datetime, zone: str) -> list[float]:
    """Zero results for a DST gap; two for an ambiguous wall clock time."""
    tz = ZoneInfo(zone)
    result = set()
    for fold in (0, 1):
        aware = value.replace(tzinfo=tz, fold=fold)
        utc = aware.timestamp()
        if datetime.fromtimestamp(utc, tz).replace(tzinfo=None) == value:
            result.add(utc)
    return sorted(result)


def display_time(timestamp, zone):
    return datetime.fromtimestamp(timestamp, ZoneInfo(zone)).strftime("%d.%m.%Y %H:%M") if timestamp is not None else "—"


class Store:
    def __init__(self, root: Path):
        self.root = Path(root)
        self.root.mkdir(parents=True, exist_ok=True)
        self.media = self.root / "media"
        self.media.mkdir(exist_ok=True)
        self.path = self.root / "gallery.sqlite3"
        with self.connection() as db:
            db.executescript('''
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS posts(
                    id TEXT PRIMARY KEY, content TEXT NOT NULL, status TEXT NOT NULL,
                    scheduled REAL, original REAL, url TEXT NOT NULL DEFAULT '',
                    error TEXT NOT NULL DEFAULT '', created REAL NOT NULL, updated REAL NOT NULL);
                CREATE INDEX IF NOT EXISTS queue_order ON posts(status, scheduled);
                CREATE TABLE IF NOT EXISTS events(
                    id INTEGER PRIMARY KEY, post_id TEXT, at REAL NOT NULL,
                    kind TEXT NOT NULL, detail TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS attempts(
                    id TEXT PRIMARY KEY, post_id TEXT NOT NULL, at REAL NOT NULL,
                    phase TEXT NOT NULL, url TEXT NOT NULL DEFAULT '');
                CREATE TABLE IF NOT EXISTS flairs(
                    community TEXT PRIMARY KEY, labels TEXT NOT NULL, updated REAL NOT NULL);
                PRAGMA user_version=1;
            ''')

    @contextmanager
    def connection(self):
        db = sqlite3.connect(self.path, timeout=20)
        db.row_factory = sqlite3.Row
        try:
            with db:
                yield db
        finally:
            db.close()

    def setting(self, key, default=None):
        with self.connection() as db:
            row = db.execute("SELECT value FROM settings WHERE key=?", (key,)).fetchone()
            return json.loads(row[0]) if row else default

    def set_setting(self, key, value):
        with self.connection() as db:
            db.execute("INSERT OR REPLACE INTO settings VALUES(?,?)", (key, json.dumps(value)))

    @staticmethod
    def decode(row):
        if row is None:
            raise ValueError("Пост не найден")
        result = dict(row)
        result["content"] = json.loads(result["content"])
        return result

    def get(self, post_id):
        with self.connection() as db:
            return self.decode(db.execute("SELECT * FROM posts WHERE id=?", (post_id,)).fetchone())

    def posts(self):
        with self.connection() as db:
            return [self.decode(r) for r in db.execute("SELECT * FROM posts ORDER BY scheduled IS NULL, scheduled, created, id")]

    def import_image(self, source):
        source = Path(source)
        if source.stat().st_size > 20 * 1024 * 1024:
            raise ValueError("Изображение больше 20 МБ")
        with Image.open(source) as img:
            if img.format not in {"JPEG", "PNG", "WEBP"}:
                raise ValueError("Поддерживаются JPEG, PNG и WebP")
            suffix = {"JPEG": ".jpg", "PNG": ".png", "WEBP": ".webp"}[img.format]
            img.verify()
        digest = hashlib.sha256(source.read_bytes()).hexdigest()
        target = self.media / (digest + suffix)
        if not target.exists():
            shutil.copyfile(source, target)
        return {"file": target.name, "caption": ""}

    def media_path(self, name):
        if Path(name).name != name or not re.fullmatch(r"[0-9a-f]{64}\.(jpg|png|webp)", name):
            raise ValueError("Некорректный путь изображения")
        return self.media / name

    def validate(self, content, publishing=False):
        if not content.get("title", "").strip():
            raise ValueError("Укажите заголовок")
        if len(content["title"]) > 300:
            raise ValueError("Заголовок длиннее 300 символов")
        if publishing and not re.fullmatch(r"[A-Za-z0-9_]{2,32}", content.get("community", "")):
            raise ValueError("Укажите имя сообщества без r/")
        images = content.get("images", [])
        if len(images) > 20 or (publishing and len(images) < 2):
            raise ValueError("Галерея должна содержать от 2 до 20 изображений")
        for item in images:
            if not self.media_path(item["file"]).is_file():
                raise ValueError("Изображение отсутствует в хранилище")
            if len(item.get("caption", "")) > 180:
                raise ValueError("Подпись длиннее 180 символов")

    def save(self, content, scheduled=None, post_id=None):
        self.validate(content, scheduled is not None)
        now = time.time()
        with self.connection() as db:
            if post_id:
                row = db.execute("SELECT status FROM posts WHERE id=?", (post_id,)).fetchone()
                if not row or row[0] not in EDITABLE:
                    raise ValueError("Этот пост нельзя редактировать. Создайте копию.")
                db.execute("UPDATE posts SET content=?,status=?,scheduled=?,original=?,error='',updated=? WHERE id=?",
                           (json.dumps(content), "scheduled" if scheduled is not None else "draft", scheduled, scheduled, now, post_id))
            else:
                post_id = uuid.uuid4().hex
                db.execute("INSERT INTO posts(id,content,status,scheduled,original,created,updated) VALUES(?,?,?,?,?,?,?)",
                           (post_id, json.dumps(content), "scheduled" if scheduled is not None else "draft", scheduled, scheduled, now, now))
        return post_id

    def copies(self, post_id, count, start=None, interval=3600):
        if not 1 <= count <= 500 or interval < 60:
            raise ValueError("Количество: 1–500, интервал: от 1 минуты")
        content = self.get(post_id)["content"]
        self.validate(content, start is not None)
        ids = []
        now = time.time()
        with self.connection() as db:
            for i in range(count):
                key = uuid.uuid4().hex
                when = start + i * interval if start is not None else None
                db.execute("INSERT INTO posts(id,content,status,scheduled,original,created,updated) VALUES(?,?,?,?,?,?,?)",
                           (key, json.dumps(content), "scheduled" if when is not None else "draft", when, when, now, now))
                ids.append(key)
        return ids

    def batch(self, ids, changes=None, times=None, unschedule=False):
        changes = changes or {}
        with self.connection() as db:
            db.execute("BEGIN IMMEDIATE")
            for index, key in enumerate(ids):
                p = self.decode(db.execute("SELECT * FROM posts WHERE id=?", (key,)).fetchone())
                if p["status"] not in EDITABLE:
                    raise ValueError("В выборе есть отправляемый, опубликованный или непроверенный пост")
                p["content"].update(changes)
                when = None if unschedule else times[index] if times is not None else p["scheduled"]
                self.validate(p["content"], when is not None)
                db.execute("UPDATE posts SET content=?,status=?,scheduled=?,original=?,error='',updated=? WHERE id=?",
                           (json.dumps(p["content"]), "scheduled" if when is not None else "draft", when, when, time.time(), key))

    def shift(self, seconds, reason, now=None):
        now = time.time() if now is None else now
        with self.connection() as db:
            db.execute("BEGIN IMMEDIATE")
            self._shift(db, seconds, reason, now)

    @staticmethod
    def _shift(db, seconds, reason, now):
        if seconds <= 0:
            return
        rows = db.execute("SELECT id,scheduled FROM posts WHERE status='scheduled'").fetchall()
        for row in rows:
            db.execute("INSERT INTO events(post_id,at,kind,detail) VALUES(?,?,?,?)",
                       (row[0], now, "shift", json.dumps({"from": row[1], "to": row[1] + seconds, "reason": reason}, ensure_ascii=False)))
        db.execute("UPDATE posts SET scheduled=scheduled+?,updated=? WHERE status='scheduled'", (seconds, now))

    def recover(self, now=None):
        now = time.time() if now is None else now
        with self.connection() as db:
            db.execute("BEGIN IMMEDIATE")
            db.execute("UPDATE posts SET status='review',error='Работа прервана во время отправки. Проверьте Reddit.',updated=? WHERE status='sending'", (now,))
            first = db.execute("SELECT MIN(scheduled) FROM posts WHERE status='scheduled'").fetchone()[0]
            if first is not None and first < now:
                self._shift(db, now + self.setting("startup_minutes", 5) * 60 - first, "Возобновление работы", now)

    def claim(self, now=None):
        now = time.time() if now is None else now
        with self.connection() as db:
            db.execute("BEGIN IMMEDIATE")
            row = db.execute("SELECT * FROM posts WHERE status='scheduled' AND scheduled<=? ORDER BY scheduled,created,id LIMIT 1", (now,)).fetchone()
            if not row:
                return None
            db.execute("UPDATE posts SET status='sending',updated=? WHERE id=?", (now, row["id"]))
            return self.decode(row)

    def status(self, key, status, message="", url=""):
        if status not in STATES:
            raise ValueError(status)
        with self.connection() as db:
            db.execute("UPDATE posts SET status=?,error=?,url=?,updated=? WHERE id=?", (status, message, url, time.time(), key))
            db.execute("INSERT INTO events(post_id,at,kind,detail) VALUES(?,?,?,?)", (key, time.time(), status, message or url))

    def defer(self, key, seconds, reason):
        with self.connection() as db:
            db.execute("BEGIN IMMEDIATE")
            db.execute("UPDATE posts SET status='scheduled',error=? WHERE id=?", (reason, key))
            self._shift(db, seconds, reason, time.time())

    def attempt(self, key, phase="click_pending"):
        attempt_id = uuid.uuid4().hex
        with self.connection() as db:
            db.execute("INSERT INTO attempts(id,post_id,at,phase) VALUES(?,?,?,?)", (attempt_id, key, time.time(), phase))
        return attempt_id

    def complete_attempt(self, attempt_id, url):
        with self.connection() as db:
            db.execute("UPDATE attempts SET phase='confirmed',url=? WHERE id=?", (url, attempt_id))

    def events(self, key):
        with self.connection() as db:
            return [dict(r) for r in db.execute("SELECT * FROM events WHERE post_id=? ORDER BY at DESC", (key,))]

    def cache_flairs(self, community, labels):
        with self.connection() as db:
            db.execute("INSERT OR REPLACE INTO flairs VALUES(?,?,?)", (community.lower(), json.dumps(labels), time.time()))

    def flairs(self, community):
        with self.connection() as db:
            row = db.execute("SELECT labels FROM flairs WHERE community=?", (community.lower(),)).fetchone()
            return json.loads(row[0]) if row else []

    def set_password(self, password):
        salt = os.urandom(16)
        digest = hashlib.scrypt(password.encode(), salt=salt, n=16384, r=8, p=1)
        self.set_setting("password", {"salt": salt.hex(), "hash": digest.hex()})

    def check_password(self, password):
        record = self.setting("password")
        if not record:
            return True
        digest = hashlib.scrypt(password.encode(), salt=bytes.fromhex(record["salt"]), n=16384, r=8, p=1)
        return hmac.compare_digest(digest.hex(), record["hash"])

    def backup(self, destination):
        with tempfile.TemporaryDirectory() as tmp:
            copy_path = Path(tmp) / "gallery.sqlite3"
            with self.connection() as db:
                other = sqlite3.connect(copy_path)
                db.backup(other)
                other.execute("DELETE FROM settings WHERE key IN ('password','account','autostart')")
                other.execute("INSERT OR REPLACE INTO settings VALUES('paused','true')")
                other.commit()
                other.close()
            with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED) as archive:
                archive.write(copy_path, "gallery.sqlite3")
                for path in self.media.iterdir():
                    if path.is_file():
                        archive.write(path, "media/" + path.name)

    def restore(self, source):
        # Validate everything in a staging area before replacing the live database.
        with tempfile.TemporaryDirectory(dir=self.root) as tmp:
            stage = Path(tmp)
            with zipfile.ZipFile(source) as archive:
                if sum(i.file_size for i in archive.infolist()) > 20 * 1024**3:
                    raise ValueError("Архив больше 20 ГБ")
                for info in archive.infolist():
                    if info.filename != "gallery.sqlite3" and not re.fullmatch(r"media/[0-9a-f]{64}\.(jpg|png|webp)", info.filename):
                        raise ValueError("Архив содержит недопустимые файлы")
                    archive.extract(info, stage)
            incoming = stage / "gallery.sqlite3"
            if not incoming.is_file():
                raise ValueError("В архиве нет базы данных")
            check = sqlite3.connect(incoming)
            try:
                if check.execute("PRAGMA integrity_check").fetchone()[0] != "ok" or check.execute("PRAGMA user_version").fetchone()[0] != 1:
                    raise ValueError("Несовместимая или повреждённая база")
                for (content,) in check.execute("SELECT content FROM posts"):
                    for item in json.loads(content).get("images", []):
                        name = self.media_path(item["file"]).name
                        if not (stage / "media" / name).is_file():
                            raise ValueError("В архиве отсутствует изображение")
                check.execute("DELETE FROM settings WHERE key IN ('password','account','autostart')")
                check.execute("INSERT OR REPLACE INTO settings VALUES('paused','true')")
                check.commit()
                # Preserve the current local lock rather than trusting one in an archive.
                password = self.setting("password")
                autostart = self.setting("autostart", False)
                check.execute("INSERT OR REPLACE INTO settings VALUES('autostart',?)", (json.dumps(autostart),))
                if password:
                    check.execute("INSERT OR REPLACE INTO settings VALUES('password',?)", (json.dumps(password),))
                    check.commit()
                check.commit()
                for file in (stage / "media").glob("*"):
                    shutil.copyfile(file, self.media / file.name)
                with self.connection() as target:
                    check.backup(target)
            finally:
                check.close()
        self.recover()
