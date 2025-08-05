import os
from PIL import Image
from bs4 import BeautifulSoup
from urllib.parse import quote

# Breakpoints for responsive images
BREAKPOINTS = {
    'mobile': 480,
    'tablet': 768,
    'desktop': 1280,
}

IMAGE_EXTS = ['.jpg', '.jpeg', '.png', '.gif', '.bmp']

# Convert images to webp at different sizes
def convert_image(path):
    base, ext = os.path.splitext(path)
    if ext.lower() not in IMAGE_EXTS:
        return
    try:
        img = Image.open(path)
    except Exception as e:
        print(f'Skip {path}: {e}')
        return
    for label, width in BREAKPOINTS.items():
        new_path = f"{base}-{label}.webp"
        if os.path.exists(new_path):
            continue
        w, h = img.size
        if w > width:
            ratio = width / float(w)
            new_h = int(h * ratio)
            resized = img.resize((width, new_h), Image.LANCZOS)
        else:
            resized = img.copy()
        if resized.mode in ('RGBA', 'LA'):
            background = Image.new('RGBA', resized.size, (255, 255, 255, 0))
            background.paste(resized, (0, 0))
            resized = background
        else:
            resized = resized.convert('RGB')
        resized.save(new_path, 'WEBP', quality=80)
        print(f'Saved {new_path}')
    img.close()

# Update HTML files to use <picture> with webp sources
def update_html(html_path):
    with open(html_path, 'r', encoding='utf-8') as f:
        soup = BeautifulSoup(f, 'html.parser')
    changed = False
    for img in soup.find_all('img'):
        src = img.get('src')
        if not src:
            continue
        base, ext = os.path.splitext(src)
        if ext.lower() not in IMAGE_EXTS:
            continue
        # Ensure images are converted
        disk_path = os.path.join(os.path.dirname(html_path), src)
        convert_image(disk_path)
        srcset_parts = []
        for label, width in BREAKPOINTS.items():
            path = f"{base}-{label}.webp"
            encoded = quote(path, safe='/')
            srcset_parts.append(f"{encoded} {width}w")
        srcset = ', '.join(srcset_parts)
        picture = soup.new_tag('picture')
        source = soup.new_tag('source', type='image/webp', srcset=srcset,
                              sizes='(max-width: 480px) 100vw, (max-width: 768px) 100vw, 100vw')
        picture.append(source)
        img.replace_with(picture)
        picture.append(img)
        changed = True
    if changed:
        with open(html_path, 'w', encoding='utf-8') as f:
            f.write(str(soup))
        print(f'Updated {html_path}')

# Walk through project directories
for root, dirs, files in os.walk('Projects'):
    for file in files:
        ext = os.path.splitext(file)[1].lower()
        full_path = os.path.join(root, file)
        if ext in IMAGE_EXTS:
            convert_image(full_path)
    for file in files:
        if file.lower().endswith(('.html', '.htm')):
            update_html(os.path.join(root, file))

# Update other HTML files in repository root
for file in os.listdir('.'):
    if file.lower().endswith('.html'):
        update_html(file)
