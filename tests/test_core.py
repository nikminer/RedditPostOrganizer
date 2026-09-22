import json
import sqlite3
import zipfile
from datetime import datetime

import pytest
from PIL import Image

from reddit_gallery.core import Store, local_candidates
from reddit_gallery.browser import FormChanged, NeedsLogin, NetworkProblem, RateLimited, Uncertain, valid_post_url
from reddit_gallery.worker import run_post


@pytest.fixture
def store(tmp_path):
    return Store(tmp_path / "data")


@pytest.fixture
def content(store, tmp_path):
    path = tmp_path / "source.png"
    Image.new("RGB", (12, 12), "red").save(path)
    image = store.import_image(path)
    path.unlink()
    return {"title": "Gallery", "community": "test", "nsfw": True, "flair": "Art",
            "tags": ["one"], "notes": "private", "images": [image, {**image, "caption": "Second"}]}


def test_recovery_cascades_across_days_only_once(store, content):
    a = store.save(content, 36000)
    b = store.save(content, 43200)
    c = store.save(content, 118800)
    store.recover(46800)
    assert [store.get(x)["scheduled"] for x in (a, b, c)] == [47100, 54300, 129900]
    store.recover(46801)
    assert store.get(a)["scheduled"] == 47100
    assert len(store.events(a)) == 1


def test_future_schedule_unchanged(store, content):
    key = store.save(content, 5000)
    store.recover(1000)
    assert store.get(key)["scheduled"] == 5000


def test_copies_are_independent_and_clean(store, content):
    key = store.save(content, 1000)
    store.status(key, "published", url="https://www.reddit.com/r/test/comments/abc/")
    copies = store.copies(key, 2)
    clone = store.get(copies[0])
    assert clone["status"] == "draft" and clone["scheduled"] is None and clone["url"] == ""
    assert clone["content"]["nsfw"] is True
    assert [i["caption"] for i in clone["content"]["images"]] == ["", "Second"]
    clone["content"]["title"] = "Other"
    clone["content"]["images"].reverse()
    store.save(clone["content"], post_id=copies[0])
    assert store.get(copies[1])["content"]["title"] == "Gallery"
    assert store.get(key)["content"]["images"][0]["caption"] == ""
    assert store.media_path(content["images"][0]["file"]).exists()


def test_atomic_batch_rollback(store, content):
    a = store.save(content)
    b = store.save(content)
    store.status(b, "published")
    with pytest.raises(ValueError):
        store.batch([a, b], {"nsfw": False})
    assert store.get(a)["content"]["nsfw"] is True


def test_claim_single_and_crash_requires_review(store, content):
    key = store.save(content, 1000)
    assert store.claim(1100)["id"] == key
    assert store.claim(1100) is None
    reopened = Store(store.root)
    reopened.recover(1200)
    assert reopened.get(key)["status"] == "review"
    assert reopened.claim(1200) is None


@pytest.mark.parametrize("exc,status,pause", [
    (NetworkProblem("Offline"), "scheduled", False),
    (RateLimited(3600), "scheduled", False),
    (NeedsLogin("Login expired"), "scheduled", True),
    (FormChanged("No caption field"), "error", False),
    (Uncertain("Unknown"), "review", False),
    (RuntimeError("Unexpected"), "review", False),
])
def test_send_failures(store, content, exc, status, pause):
    key = store.save(content, 1000)
    next_key = store.save(content, 8200)
    post = store.claim(1000)
    class Browser:
        def send(self, _):
            raise exc
    run_post(store, Browser(), post)
    assert store.get(key)["status"] == status
    assert bool(store.setting("pause_reason")) == pause
    if isinstance(exc, (NetworkProblem, RateLimited)):
        delay = 3600 if isinstance(exc, RateLimited) else 900
        assert store.get(key)["scheduled"] == 1000 + delay
        assert store.get(next_key)["scheduled"] == 8200 + delay
    else:
        assert store.get(next_key)["scheduled"] == 8200


def test_send_success(store, content):
    key = store.save(content, 1000)
    class Browser:
        def send(self, _):
            return "https://www.reddit.com/r/test/comments/abc/"
    run_post(store, Browser(), store.claim(1000))
    assert store.get(key)["status"] == "published"
    assert store.get(key)["url"].endswith("abc/")


def test_dst_gap_and_ambiguity():
    assert local_candidates(datetime(2026, 3, 29, 2, 30), "Europe/Berlin") == []
    choices = local_candidates(datetime(2026, 10, 25, 2, 30), "Europe/Berlin")
    assert len(choices) == 2 and choices[1] - choices[0] == 3600
    assert len(local_candidates(datetime(2026, 9, 22, 10), "Europe/Moscow")) == 1


def test_backup_restore_excludes_credentials(store, content, tmp_path):
    key = store.save(content, 9999999999)
    store.set_password("test-password")
    store.set_setting("account", "someone")
    profile = store.root / "browser-profile"
    profile.mkdir()
    (profile / "cookie").write_text("secret")
    archive = tmp_path / "backup.zip"
    store.backup(archive)
    with zipfile.ZipFile(archive) as z:
        assert all("profile" not in name for name in z.namelist())
        z.extract("gallery.sqlite3", tmp_path / "inspection")
    with sqlite3.connect(tmp_path / "inspection/gallery.sqlite3") as db:
        assert not db.execute("SELECT * FROM settings WHERE key IN ('password','account')").fetchall()
    destination = Store(tmp_path / "restored")
    destination.set_password("new-password")
    destination.restore(archive)
    assert destination.get(key)["content"] == content
    assert destination.setting("paused") is True
    assert destination.setting("account") is None
    assert destination.check_password("new-password")


def test_archive_traversal_rejected(store, tmp_path):
    archive = tmp_path / "bad.zip"
    with zipfile.ZipFile(archive, "w") as z:
        z.writestr("../evil", "bad")
    with pytest.raises(ValueError):
        store.restore(archive)
    assert not (store.root.parent / "evil").exists()


def test_password(store):
    store.set_password("long-password")
    assert store.check_password("long-password")
    assert not store.check_password("wrong")
    assert "long-password" not in json.dumps(store.setting("password"))


def test_url_validation():
    assert valid_post_url("https://www.reddit.com/r/test/comments/abc/title/")
    assert not valid_post_url("https://reddit.com.evil/r/test/comments/abc/")
    assert not valid_post_url("https://www.reddit.com/user/person")
