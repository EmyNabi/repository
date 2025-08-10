document.addEventListener('click', e => {
  const images = Array.from(document.querySelectorAll('img[data-fullres]'));
  if (images.includes(e.target)) {
    showLightbox(images, e.target);
  }
});

function showLightbox(images, activeImage) {
  let currentIndex = images.indexOf(activeImage);
  const overlay = document.createElement('div');
  overlay.className = 'lightbox-overlay';
  const fullSrc = activeImage.dataset.fullres || activeImage.src;
  overlay.innerHTML = `
    <span class="close">&times;</span>
    <span class="nav prev">&#10094;</span>
    <img src="${fullSrc}" loading="lazy" />
    <span class="nav next">&#10095;</span>
  `;
  overlay.style.display = 'flex';
  document.body.appendChild(overlay);
  const overlayImg = overlay.querySelector('img');

  function update(idx) {
    overlayImg.src = images[idx].dataset.fullres || images[idx].src;
    currentIndex = idx;
  }

  overlay.querySelector('.prev').onclick = () => update((currentIndex - 1 + images.length) % images.length);
  overlay.querySelector('.next').onclick = () => update((currentIndex + 1) % images.length);
  overlay.querySelector('.close').onclick = () => overlay.remove();
  overlay.onclick = e => { if (e.target === overlay) overlay.remove(); };
  document.onkeydown = e => {
    if (e.key === 'Escape') overlay.remove();
    if (e.key === 'ArrowLeft') overlay.querySelector('.prev').click();
    if (e.key === 'ArrowRight') overlay.querySelector('.next').click();
  };
}

