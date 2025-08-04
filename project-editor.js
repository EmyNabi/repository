const isAdmin = localStorage.getItem('isAdmin') === 'true';
const main = document.querySelector('main.project-detail');
const projectKey = main.dataset.project;
const container = document.getElementById('dynamic-project-sections');
let allProjects = {};
let images = [];
let dragIndex = null;

fetch('../../list-projects.php')
  .then(res => res.json())
  .then(data => {
    allProjects = data;
    let projectData = allProjects[projectKey];
    if (Array.isArray(projectData)) {
      projectData = { images: projectData, cover: projectData[0]?.filename || '' };
      allProjects[projectKey] = projectData;
    }
    if (!projectData) {
      projectData = { images: [], cover: '' };
      allProjects[projectKey] = projectData;
    }
    images = projectData.images;
    render();
    if (isAdmin) setupAdmin();
  });

function render() {
  container.innerHTML = '';
  const cover = allProjects[projectKey].cover;
  images.forEach((img, index) => {
    const section = document.createElement('div');
    const isCover = cover === img.filename;
    section.className = 'project-section ' + (img.layout || '') + (isCover ? ' cover' : '');
    section.draggable = isAdmin;
    section.dataset.index = index;
    section.innerHTML = `
      <img class="lightbox-image" src="Images/${img.filename}" alt="${img.title}">
      <div class="project-text">
        <h3 contenteditable="${isAdmin}">${img.title}</h3>
        <p contenteditable="${isAdmin}">${img.description}</p>
        ${isAdmin ? `<div class="layout-toolbar">
            <button data-layout="">Default</button>
            <button data-layout="left">Left</button>
            <button data-layout="right">Right</button>
            <button data-layout="grid-2">Grid 2</button>
            <button data-layout="grid-3">Grid 3</button>
            <button data-layout="carousel">Carousel</button>
          </div>
          <button class="set-cover">${isCover ? 'Cover Image' : 'Set Cover'}</button>` : ''}
      </div>
    `;
    if (isAdmin) {
      section.querySelectorAll('.layout-toolbar button').forEach(btn => {
        if (btn.dataset.layout === (img.layout || '')) btn.classList.add('active');
        btn.addEventListener('click', () => {
          images[index].layout = btn.dataset.layout;
          render();
          save();
        });
      });
      const coverBtn = section.querySelector('.set-cover');
      coverBtn.addEventListener('click', () => {
        allProjects[projectKey].cover = img.filename;
        save();
        render();
      });
      if (isCover) {
        coverBtn.disabled = true;
      }
      section.querySelector('h3').addEventListener('input', e => {
        images[index].title = e.target.innerText;
        save();
      });
      section.querySelector('p').addEventListener('input', e => {
        images[index].description = e.target.innerText;
        save();
      });
      section.addEventListener('dragstart', dragStart);
      section.addEventListener('dragover', dragOver);
      section.addEventListener('drop', drop);
    }
    container.appendChild(section);
  });
}

function setupAdmin() {
  document.getElementById('admin-tools').style.display = 'block';
  document.getElementById('image-upload').addEventListener('change', handleUpload);
}

function handleUpload(e) {
  const files = [...e.target.files];
  if (files.length === 0) return;
  const formData = new FormData();
  files.forEach(f => formData.append('images[]', f));
  formData.append('project', projectKey);
  fetch('../../upload.php', { method: 'POST', body: formData })
    .then(res => res.json())
    .then(newItems => {
      images.push(...newItems);
      allProjects[projectKey].images = images;
      if (!allProjects[projectKey].cover && newItems[0]) {
        allProjects[projectKey].cover = newItems[0].filename;
      }
      render();
      save();
    });
}

function dragStart(e) {
  dragIndex = +this.dataset.index;
}
function dragOver(e) {
  e.preventDefault();
}
function drop(e) {
  e.preventDefault();
  const dropIndex = +this.dataset.index;
  if (dragIndex === dropIndex) return;
  const [moved] = images.splice(dragIndex, 1);
  images.splice(dropIndex, 0, moved);
  render();
  save();
}

function save() {
  allProjects[projectKey].images = images;
  fetch('../../save-projects.php', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(allProjects)
  }).catch(err => console.error('Save failed', err));
}

document.addEventListener('click', e => {
  if (e.target.classList.contains('lightbox-image')) {
    showLightbox(Array.from(document.querySelectorAll('.lightbox-image')), e.target);
  }
});

function showLightbox(images, activeImage) {
  let currentIndex = images.indexOf(activeImage);
  const overlay = document.createElement('div');
  overlay.className = 'lightbox-overlay';
  overlay.innerHTML = `
    <span class="close">&times;</span>
    <span class="nav prev">&#10094;</span>
    <img src="${activeImage.src}" />
    <span class="nav next">&#10095;</span>
  `;
  document.body.appendChild(overlay);
  const overlayImg = overlay.querySelector('img');
  function update(idx) {
    overlayImg.src = images[idx].src;
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
