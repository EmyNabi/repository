function applyResponsiveImages(root = document) {
  const imgs = root.querySelectorAll('img');
  imgs.forEach(img => {
    if (img.closest('picture')) {
      img.loading = 'lazy';
      img.decoding = 'async';
      return;
    }
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

    const widths = [480, 768, 1280];
    const webpSrcset = widths
      .map(w => `/repository/resize.php?src=${encodeURIComponent(path)}&w=${w}&fmt=webp ${w}w`)
      .join(', ');
    const fallbackSrcset = widths
      .map(w => `/repository/resize.php?src=${encodeURIComponent(path)}&w=${w} ${w}w`)
      .join(', ');

    const displayWidth = img.clientWidth || (img.parentElement ? img.parentElement.clientWidth : window.innerWidth);
    const placeholder = `/repository/resize.php?src=${encodeURIComponent(path)}&w=20`;

    img.classList.add('lazy-image');
    const onLoad = () => {
      if (!img.currentSrc.includes('w=20')) {
        img.classList.add('loaded');
        img.removeEventListener('load', onLoad);
      }
    };
    img.addEventListener('load', onLoad);

    img.loading = 'lazy';
    img.decoding = 'async';
    img.src = placeholder;
    img.srcset = fallbackSrcset;
    img.sizes = `${displayWidth}px`;

    const picture = document.createElement('picture');
    const sourceWebp = document.createElement('source');
    sourceWebp.type = 'image/webp';
    sourceWebp.srcset = webpSrcset;
    sourceWebp.sizes = `${displayWidth}px`;
    picture.appendChild(sourceWebp);
    img.parentNode.replaceChild(picture, img);
    picture.appendChild(img);
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
