function loadHeader(callback) {
  fetch('/repository/header.html')
    .then(res => res.text())
    .then(html => {
      document.getElementById('site-header').innerHTML = html;
      const isAdmin = localStorage.getItem('isAdmin') === 'true';
      const loginLink = document.getElementById('login-link');
      const logoutLink = document.getElementById('logout-link');
      if (isAdmin) {
        if (loginLink) loginLink.style.display = 'none';
        if (logoutLink) {
          logoutLink.style.display = 'inline';
          logoutLink.addEventListener('click', () => {
            localStorage.removeItem('isAdmin');
            window.location.href = '/repository/index.html';
          });
        }
      } else {
        if (loginLink) loginLink.style.display = 'inline';
        if (logoutLink) logoutLink.style.display = 'none';
      }
      if (callback) callback(isAdmin);
    });
}

if (!document.getElementById('responsive-images-script')) {
  const script = document.createElement('script');
  script.src = '/repository/responsive-images.js';
  script.defer = true;
  script.id = 'responsive-images-script';
  document.head.appendChild(script);
}

if (!document.getElementById('lightbox-script')) {
  const script = document.createElement('script');
  script.src = '/repository/lightbox.js';
  script.defer = true;
  script.id = 'lightbox-script';
  document.head.appendChild(script);
}

function initImageFadeIn(root = document) {
  const imgs = root.querySelectorAll('img');
  imgs.forEach(img => {
    img.style.opacity = '0';
    img.style.transition = 'opacity 0.5s ease-in';
    const show = () => { img.style.opacity = '1'; };
    if (img.complete) {
      requestAnimationFrame(show);
    } else {
      img.addEventListener('load', show, { once: true });
    }
  });
}

document.addEventListener('DOMContentLoaded', () => {
  loadHeader(() => initImageFadeIn(document.getElementById('site-header')));
  initImageFadeIn();
});

