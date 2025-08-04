function applyResponsiveImages(root = document) {
  const imgs = root.querySelectorAll('img');
  imgs.forEach(originalImg => {
    if (originalImg.closest('picture')) return;

    if (originalImg.dataset.originalSrc === undefined) {
      const orig = originalImg.getAttribute('src');
      if (!orig) return;
      originalImg.dataset.originalSrc = orig;
    }

    const origSrc = originalImg.dataset.originalSrc;
    const absUrl = new URL(origSrc, window.location.href);
    let path = absUrl.pathname;
    if (path.startsWith('/')) path = path.substring(1);
    path = path.replace(/^repository\//, '');

    const widths = [480, 768, 1200];
    const sizes = '(max-width: 600px) 480px, (max-width: 1024px) 768px, 1200px';

    const webpSrcset = widths
      .map(w => `/repository/resize.php?src=${encodeURIComponent(path)}&w=${w}&format=webp ${w}w`)
      .join(', ');

    const fallbackSrcset = widths
      .map(w => `/repository/resize.php?src=${encodeURIComponent(path)}&w=${w} ${w}w`)
      .join(', ');

    const picture = document.createElement('picture');
    const source = document.createElement('source');
    source.type = 'image/webp';
    source.srcset = webpSrcset;
    source.sizes = sizes;
    picture.appendChild(source);

    const img = originalImg.cloneNode(true);
    img.src = `/repository/resize.php?src=${encodeURIComponent(path)}&w=1200`;
    img.srcset = fallbackSrcset;
    img.sizes = sizes;
    img.loading = 'lazy';
    img.decoding = 'async';

    picture.appendChild(img);
    originalImg.replaceWith(picture);
  });
}

function initResponsiveImages() {
  applyResponsiveImages();
}

if (document.readyState !== 'loading') {
  initResponsiveImages();
} else {
  document.addEventListener('DOMContentLoaded', initResponsiveImages);
}

