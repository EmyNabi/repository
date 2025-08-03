const isAdmin = localStorage.getItem('isAdmin') === 'true';


fetch('all-projects.json')
  .then(res => res.json())
  .then(projects => {
    const container = document.getElementById('projects-container');

    Object.entries(projects).forEach(([projectId, images]) => {
      const projectGroup = document.createElement('section');
      projectGroup.className = 'project-group';

      images.forEach((img, index) => {
        const section = document.createElement('div');
        section.className = 'project-section';
        section.draggable = isAdmin;
        section.dataset.project = projectId;
        section.dataset.index = index;

        section.innerHTML = `
          <img class="lightbox-image" src="Projects/${projectId}/images/${img.filename}" alt="${img.title}">
          <div class="project-text">
            <h3 contenteditable="${isAdmin}">${img.title}</h3>
            <p contenteditable="${isAdmin}">${img.description}</p>
          </div>
        `;

        if (isAdmin) {
          section.addEventListener('dragstart', dragStart);
          section.addEventListener('dragover', dragOver);
          section.addEventListener('drop', drop);
        }

        projectGroup.appendChild(section);
      });

      container.appendChild(projectGroup);
    });
  });

let projectData = []; // Store global project list

fetch('all-projects.json')
  .then(response => response.json())
  .then(projects => {
    projectData = projects;
    const container = document.getElementById('projects-container');

    projects.forEach((project, index) => {
      const card = document.createElement('a');
      card.href = project.link;
      card.className = 'project-card';

      card.innerHTML = `
        <img src="${project.thumbnail}" alt="${project.title}">
        <div class="project-title" contenteditable="${isAdmin}" data-index="${index}">${project.title}</div>
        ${isAdmin ? `<button class="edit-button" data-index="${index}">✏️</button>` : ''}
      `;

      container.appendChild(card);
    });

    if (isAdmin) {
      document.querySelectorAll('.project-title').forEach(titleDiv => {
        titleDiv.addEventListener('input', (e) => {
          const i = +e.target.dataset.index;
          projectData[i].title = e.target.innerText;
          saveProjectsToBackend(projectData);
        });
      });
    }
  });
