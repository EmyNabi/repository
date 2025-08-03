<?php
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $project = preg_replace('/[^a-zA-Z0-9_-]/', '', $_POST['project'] ?? '');
    if (!$project) {
        http_response_code(400);
        echo json_encode(['error' => 'no project']);
        exit;
    }
    $targetDir = "Projects/$project/Images/";
    if (!is_dir($targetDir)) {
        mkdir($targetDir, 0777, true);
    }
    $uploaded = [];
    if (!empty($_FILES['images'])) {
        foreach ($_FILES['images']['name'] as $i => $name) {
            $tmp = $_FILES['images']['tmp_name'][$i];
            $basename = basename($name);
            move_uploaded_file($tmp, $targetDir . $basename);
            $uploaded[] = ['filename' => $basename, 'title' => '', 'description' => '', 'layout' => ''];
        }
    }
    echo json_encode($uploaded);
} else {
    http_response_code(405);
    echo json_encode(['error' => 'Method not allowed']);
}
?>
