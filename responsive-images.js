function applyResponsiveImages(root = document) {
  const imgs = root.querySelectorAll('img');
  imgs.forEach(img => {
    if (img.dataset.originalSrc === undefined) {
      const orig = img.getAttribute('src');
      if (!orig) return;
      img.dataset.originalSrc = orig;
    }
    const origSrc = img.dataset.originalSrc;
    let displayWidth = img.clientWidth;
    if (!displayWidth) {
      displayWidth = img.parentElement ? img.parentElement.clientWidth : window.innerWidth;
    }
    const targetWidth = Math.ceil(displayWidth * window.devicePixelRatio);
    const absUrl = new URL(origSrc, window.location.href);
    let path = absUrl.pathname;
    if (path.startsWith('/')) path = path.substring(1);
    path = path.replace(/^repository\//, '');
    img.src = `/repository/resize.php?src=${encodeURIComponent(path)}&w=${targetWidth}`;
    img.loading = 'lazy';
    img.decoding = 'async';
  });
}
function initResponsiveImages() {
  applyResponsiveImages();
  window.addEventListener('resize', () => applyResponsiveImages());
}
if (document.readyState !== 'loading') {
  initResponsiveImages();
} else {
  document.addEventListener('DOMContentLoaded', initResponsiveImages);
}
