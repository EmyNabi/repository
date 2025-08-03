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
        if (!mkdir("$dir/Images", 0777, true)) {
            http_response_code(500);
            echo json_encode(['error' => 'failed to create project directory']);
            exit;
        }
        $template = @file_get_contents('project-template.html');
        if ($template === false) {
            http_response_code(500);
            echo json_encode(['error' => 'missing project template']);
            exit;
        }
        $html = str_replace('{{PROJECT_KEY}}', $key, $template);
        if (file_put_contents("$dir/$key.html", $html) === false) {
            http_response_code(500);
            echo json_encode(['error' => 'failed to write project file']);
            exit;
        }
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
