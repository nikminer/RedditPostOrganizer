"""Browser integration tests use authored local HTML, never real Reddit posts."""
import os
from contextlib import contextmanager

import pytest
from PIL import Image
from playwright.sync_api import sync_playwright

from reddit_gallery.browser import FormChanged, NeedsLogin, RedditBrowser, Uncertain
from reddit_gallery.core import Store

FORM = '''<!doctype html><html><body>
<textarea name="title" aria-label="Title"></textarea>
<input type="file" multiple>
<input placeholder="Add a caption"><input placeholder="Add a caption">
<button onclick="document.querySelector('dialog').showModal()">Add flair and tags</button>
<dialog><label><input type="radio" name="flair">Art</label>
<label><input type="checkbox">NSFW</label>
<button onclick="document.querySelector('dialog').close()">Apply</button></dialog>
<button id="submit">Post</button></body></html>'''


@pytest.fixture
def page():
    with sync_playwright() as pw:
        browser = pw.chromium.launch(headless=True, channel="chromium")
        context = browser.new_context()
        context.route("**/*", lambda route: route.fulfill(status=200, content_type="text/html", body=FORM))
        page = context.new_page()
        page.goto("https://www.reddit.com/r/test/submit/")
        yield page
        browser.close()


@pytest.fixture
def adapter(tmp_path):
    store = Store(tmp_path / "store")
    image = tmp_path / "image.png"
    Image.new("RGB", (10, 10)).save(image)
    media = store.import_image(image)
    content = {"title": "Test Gallery", "community": "test", "nsfw": True, "flair": "Art",
               "images": [{**media, "caption": "First"}, {**media, "caption": "Second"}]}
    key = store.save(content, 1000)
    return RedditBrowser(store), store.get(key)


def test_fill_checks_nsfw_flair_and_caption_order(page, adapter):
    browser, post = adapter
    browser.fill_gallery(page, post["content"])
    assert page.locator('textarea[name="title"]').input_value() == "Test Gallery"
    assert page.locator('input[type="checkbox"]').is_checked()
    assert page.locator('input[type="radio"]').is_checked()
    assert page.locator('input[placeholder="Add a caption"]').nth(0).input_value() == "First"
    assert page.locator('input[placeholder="Add a caption"]').nth(1).input_value() == "Second"


def test_missing_nsfw_fails_closed(page, adapter):
    browser, post = adapter
    page.locator('input[type="checkbox"]').evaluate("e => e.parentElement.remove()")
    with pytest.raises(FormChanged, match="NSFW"):
        browser.apply_labels(page, post["content"])


def test_humanity_challenge_pauses(page):
    page.set_content("<body>Prove your humanity</body>")
    with pytest.raises(NeedsLogin):
        RedditBrowser.guard(page)


def test_missing_flair_is_not_silently_ignored(page, adapter):
    browser, post = adapter
    post["content"]["flair"] = "Removed flair"
    with pytest.raises(FormChanged):
        browser.apply_labels(page, post["content"])


def test_crash_after_submit_click_requires_review(page, adapter, monkeypatch):
    browser, post = adapter
    @contextmanager
    def context(*args, **kwargs):
        yield page.context
    monkeypatch.setattr(browser, "context", context)
    monkeypatch.setattr(browser, "identity", lambda _: "test")
    monkeypatch.setattr(browser, "check_account", lambda _: None)
    monkeypatch.setattr(browser, "open_form", lambda *_: None)
    monkeypatch.setattr(browser, "result", lambda *_: (_ for _ in ()).throw(RuntimeError("Connection lost")))
    with pytest.raises(Uncertain):
        browser.send(post)
    with browser.store.connection() as db:
        attempts = db.execute("SELECT phase FROM attempts").fetchall()
        assert len(attempts) == 1 and attempts[0][0] == "click_pending"
