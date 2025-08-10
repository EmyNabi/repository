import os
from PIL import Image

# Set source and output folders
SUPPORTED_EXTENSIONS = ('.jpg', '.jpeg', '.png', '.tiff', '.bmp')

# Resize configurations
SIZES = {
    "thumb": 400,
    "medium": 1200,
}

def resize_and_save(image_path, output_dir):
    try:
        img = Image.open(image_path)
        base_name = os.path.basename(image_path)
        name, _ = os.path.splitext(base_name)

        for label, width in SIZES.items():
            img_copy = img.copy()
            img_copy.thumbnail((width, width * 10))  # maintain aspect ratio
            out_path = os.path.join(output_dir, f"{name}-{label}.webp")
            img_copy.save(out_path, "WEBP", quality=85)
            print(f"✓ Saved: {out_path}")

    except Exception as e:
        print(f"✗ Failed to process {image_path}: {e}")

def process_directory(base_dir):
    for root, _, files in os.walk(base_dir):
        for file in files:
            if file.lower().endswith(SUPPORTED_EXTENSIONS):
                image_path = os.path.join(root, file)
                resize_and_save(image_path, root)

if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser(description="Optimize images to WebP (thumb + medium).")
    parser.add_argument("--images", type=str, required=True, help="Path to images folder")
    args = parser.parse_args()

    if os.path.exists(args.images):
        process_directory(args.images)
        print("✅ Image optimization complete.")
    else:
        print(f"✗ Directory not found: {args.images}")
