#!/usr/bin/env python3
"""Utility to generate webp thumbnails and update HTML image references.

Creates a `thumbs/` directory inside every project `Images` folder and
converts existing JPG/PNG files to WebP thumbnails with a maximum width of
720 pixels. It can also rewrite HTML files so that `<img>` tags point to the
new thumbnails, retain a `data-fullres` attribute for the original image and
use lazy loading.

Usage:
    python optimize_images.py           # process images and update HTML
    python optimize_images.py --images  # only process images
    python optimize_images.py --html    # only update HTML files

The script skips any image that already has a corresponding thumbnail.
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path
from typing import Iterable

from PIL import Image
from bs4 import BeautifulSoup

IMAGE_EXTS = {".jpg", ".jpeg", ".png"}


def create_thumbnail(src: Path, dest: Path, max_width: int = 720) -> None:
    """Create a WebP thumbnail for *src* at *dest* with max width *max_width*.

    Existing thumbnails are not overwritten.
    """
    if dest.exists():
        return
    try:
        with Image.open(src) as img:
            width, height = img.size
            if width > max_width:
                ratio = max_width / float(width)
                img = img.resize((max_width, int(height * ratio)), Image.LANCZOS)
            if img.mode in ("RGBA", "LA"):
                img = img.convert("RGBA")
            else:
                img = img.convert("RGB")
            dest.parent.mkdir(parents=True, exist_ok=True)
            img.save(dest, "WEBP", quality=80)
            print(f"Saved {dest}")
    except Exception as exc:  # pragma: no cover - safety net
        print(f"Failed to process {src}: {exc}")


def generate_thumbnails(root: Path = Path("Projects")) -> None:
    """Walk through *root* and create thumbnails for all images."""
    for image_dir in (p for p in root.rglob("*") if p.is_dir() and p.name.lower() == "images"):
        thumbs = image_dir / "thumbs"
        thumbs.mkdir(exist_ok=True)
        for file in image_dir.iterdir():
            if file.is_file() and file.suffix.lower() in IMAGE_EXTS:
                dest = thumbs / (file.stem + ".webp")
                create_thumbnail(file, dest)


def update_html(root: Path = Path(".")) -> None:
    """Rewrite HTML files so that <img> tags use thumbnails.

    The original image path is stored in `data-fullres` and used as a fallback
    if the thumbnail is missing.
    """
    for html_file in (p for p in root.rglob("*.html")):
        with html_file.open("r", encoding="utf-8") as f:
            soup = BeautifulSoup(f, "html.parser")
        changed = False
        for img in soup.find_all("img"):
            src = img.get("src")
            if not src:
                continue
            img["loading"] = "lazy"
            img["data-fullres"] = src
            img["onerror"] = "this.onerror=null;this.src=this.dataset.fullres;"
            ext = os.path.splitext(src)[1].lower()
            if "thumbs/" in src or ext not in IMAGE_EXTS:
                continue
            parts = src.split("/")
            try:
                idx = next(i for i, part in enumerate(parts) if part.lower() == "images")
            except StopIteration:
                continue
            thumb_src = "/".join(
                parts[: idx + 1] + ["thumbs", Path(parts[-1]).stem + ".webp"]
            )
            img["src"] = thumb_src
            changed = True
        if changed:
            with html_file.open("w", encoding="utf-8") as f:
                f.write(str(soup))
            print(f"Updated {html_file}")


def parse_args(argv: Iterable[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--images", action="store_true", help="only generate thumbnails"
    )
    parser.add_argument(
        "--html", action="store_true", help="only update HTML references"
    )
    return parser.parse_args(argv)


if __name__ == "__main__":
    args = parse_args()
    if not args.images and not args.html:
        args.images = args.html = True
    if args.images:
        generate_thumbnails()
    if args.html:
        update_html()
