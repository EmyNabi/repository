function applyResponsiveImages(root = document) {
  const imgs = root.querySelectorAll('img');
  imgs.forEach(img => {
    if (img.dataset.originalSrc === undefined) {
      const orig = img.getAttribute('src');
      if (!orig) return;
      img.dataset.originalSrc = orig;
    }

    const origSrc = img.dataset.originalSrc;
    const absUrl = new URL(origSrc, window.location.href);
    let path = absUrl.pathname;
    if (path.startsWith('/')) path = path.substring(1);
    path = path.replace(/^repository\//, '');

    const widths = [320, 640, 960, 1280, 1920];
    const srcset = widths
      .map(w => `/repository/resize.php?src=${encodeURIComponent(path)}&w=${w} ${w}w`)
      .join(', ');

    const displayWidth = img.clientWidth || (img.parentElement ? img.parentElement.clientWidth : window.innerWidth);

    img.srcset = srcset;
    img.sizes = `${displayWidth}px`;
    img.src = `/repository/resize.php?src=${encodeURIComponent(path)}&w=640`;
    img.loading = 'lazy';
    img.decoding = 'async';
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
