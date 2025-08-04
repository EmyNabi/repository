<?php
header('Content-Type: application/json');

$projectsDir = __DIR__ . '/Projects';
$projectsFile = __DIR__ . '/all-projects.json';

$projects = [];
if (file_exists($projectsFile)) {
    $json = file_get_contents($projectsFile);
    $projects = json_decode($json, true);
    if (!is_array($projects)) {
        $projects = [];
    }
}

if (is_dir($projectsDir)) {
    foreach (scandir($projectsDir) as $dir) {
        if ($dir === '.' || $dir === '..') continue;
        $projectPath = $projectsDir . '/' . $dir;
        if (!is_dir($projectPath)) continue;

        if (!isset($projects[$dir])) {
            $projects[$dir] = ['cover' => '', 'images' => []];
        }

        $imagesDir = $projectPath . '/Images';
        if (is_dir($imagesDir)) {
            $existing = [];
            foreach ($projects[$dir]['images'] as $img) {
                $existing[$img['filename']] = $img;
            }
            $files = array_values(array_filter(scandir($imagesDir), function ($f) use ($imagesDir) {
                return $f !== '.' && $f !== '..' && is_file($imagesDir . '/' . $f);
            }));
            $images = [];
            foreach ($files as $file) {
                if (isset($existing[$file])) {
                    $images[] = $existing[$file];
                } else {
                    $images[] = ['filename' => $file, 'title' => '', 'description' => '', 'layout' => ''];
                }
            }
            $projects[$dir]['images'] = $images;
            if (empty($projects[$dir]['cover']) || !in_array($projects[$dir]['cover'], $files)) {
                $projects[$dir]['cover'] = $files[0] ?? '';
            }
        }
    }
}

echo json_encode($projects, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES);
