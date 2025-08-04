function loadHeader(path, callback) {
  fetch(path)
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
      if (!document.getElementById('responsive-images-script')) {
        const script = document.createElement('script');
        script.src = '/repository/responsive-images.js';
        script.id = 'responsive-images-script';
        document.head.appendChild(script);
      }
      if (callback) callback(isAdmin);
    });
}
