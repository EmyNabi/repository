import os
import sys
import argparse
from PIL import Image

SUPPORTED_EXTENSIONS = ('.jpg', '.jpeg', '.png', '.tif', '.tiff', '.bmp', '.gif')

# Default resize targets (longest side in px)
DEFAULT_SIZES = {
    "thumb": 400,
    "medium": 1200,
}

def should_skip(src_path: str, out_path: str, force: bool) -> bool:
    """Skip if output exists and is newer than source, unless --force."""
    if force:
        return False
    if not os.path.exists(out_path):
        return False
    return os.path.getmtime(out_path) >= os.path.getmtime(src_path)

def save_as_webp(img: Image.Image, out_path: str, quality: int) -> None:
    img.save(out_path, "WEBP", quality=quality, method=6)

def process_image(image_path: str, output_dir: str, sizes: dict, quality: int, force: bool, verbose: bool) -> None:
    try:
        base_name = os.path.basename(image_path)
        name, _ = os.path.splitext(base_name)

        # Open once, convert to RGB to avoid palette/alpha edge cases in webp
        with Image.open(image_path) as im:
            # Some formats come in with P/LA modes; convert to RGB to be safe
            if im.mode not in ("RGB", "RGBA"):
                im = im.convert("RGB")

            for label, longest_side in sizes.items():
                out_path = os.path.join(output_dir, f"{name}-{label}.webp")
                if should_skip(image_path, out_path, force):
                    if verbose:
                        print(f"↷ Skip (up-to-date): {out_path}")
                    continue

                # Work on a copy for each size
                img_copy = im.copy()
                # Maintain aspect ratio using thumbnail (in-place)
                # Use a very tall second dimension to ensure the longer side caps at longest_side
                img_copy.thumbnail((longest_side, longest_side), Image.LANCZOS)

                # Ensure RGB for saving (handles RGBA via background if needed)
                if img_copy.mode == "RGBA":
                    bg = Image.new("RGB", img_copy.size, (255, 255, 255))
                    bg.paste(img_copy, mask=img_copy.split()[-1])
                    img_copy = bg

                save_as_webp(img_copy, out_path, quality)
                print(f"✓ Saved: {out_path}")

    except Exception as e:
        print(f"✗ Failed: {image_path} — {e}", file=sys.stderr)

def walk_and_process(base_dir: str, sizes: dict, quality: int, force: bool, verbose: bool) -> None:
    processed = 0
    for root, _, files in os.walk(base_dir):
        for file in files:
            if file.lower().endswith(SUPPORTED_EXTENSIONS):
                image_path = os.path.join(root, file)
                process_image(image_path, root, sizes, quality, force, verbose)
                processed += 1
    if processed == 0:
        print(f"No images found under: {base_dir}")

def parse_sizes(sizes_arg: str) -> dict:
    """
    Parse sizes like "thumb:320,medium:1280,large:1920"
    """
    sizes = {}
    for pair in sizes_arg.split(","):
        if not pair.strip():
            continue
        label, val = pair.split(":")
        sizes[label.strip()] = int(val.strip())
    return sizes

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Recursively optimize images to WebP (thumb/medium) and skip up-to-date files.")
    parser.add_argument("--base", default="Projects", help="Base directory to scan recursively (default: Projects)")
    parser.add_argument("--sizes", default="", help='Custom sizes, e.g. "thumb:400,medium:1200"')
    parser.add_argument("--quality", type=int, default=85, help="WEBP quality (default: 85)")
    parser.add_argument("--force", action="store_true", help="Force reprocess even if outputs are newer")
    parser.add_argument("--verbose", action="store_true", help="Verbose skipping logs")

    args = parser.parse_args()
    sizes = DEFAULT_SIZES if not args.sizes else parse_sizes(args.sizes)

    if not os.path.exists(args.base):
        print(f"✗ Base directory not found: {args.base}")
        sys.exit(1)

    print(f"Scanning: {args.base}")
    print(f"Sizes: {sizes} | Quality: {args.quality} | Force: {args.force}")
    walk_and_process(args.base, sizes, args.quality, args.force, args.verbose)
    print("✅ Done. GOod Job Nabs")
