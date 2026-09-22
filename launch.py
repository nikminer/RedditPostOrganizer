if __name__ == "__main__":
    import os
    import sys
    import traceback
    from pathlib import Path
    try:
        from reddit_gallery.app import main
        raise SystemExit(main())
    except Exception:
        root = Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "RedditGallery"
        if "--data-dir" in sys.argv:
            index = sys.argv.index("--data-dir")
            if len(sys.argv) > index + 1:
                root = Path(sys.argv[index + 1])
        root.mkdir(parents=True, exist_ok=True)
        (root / "startup-error.log").write_text(traceback.format_exc(), encoding="utf-8")
        raise
