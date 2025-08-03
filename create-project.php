<?php
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $data = json_decode(file_get_contents('php://input'), true);
    $key = preg_replace('/[^a-zA-Z0-9_-]/', '', $data['key'] ?? '');
    if (!$key) {
        http_response_code(400);
        echo json_encode(['error' => 'invalid key']);
        exit;
    }
    $dir = "Projects/$key";
    if (!is_dir($dir)) {
        mkdir("$dir/Images", 0777, true);
        $template = file_get_contents('project-template.html');
        $html = str_replace('{{PROJECT_KEY}}', $key, $template);
        file_put_contents("$dir/$key.html", $html);
    }

    $projectsFile = 'all-projects.json';
    $projects = file_exists($projectsFile)
        ? json_decode(file_get_contents($projectsFile), true)
        : [];
    if (!isset($projects[$key])) {
        $projects[$key] = [];
        file_put_contents($projectsFile, json_encode($projects, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES));
    }
    echo json_encode(['status' => 'ok']);
} else {
    http_response_code(405);
    echo json_encode(['error' => 'Method not allowed']);
}
?>
