<?php
$src = $_GET['src'] ?? '';
$w = intval($_GET['w'] ?? 0);
$format = $_GET['format'] ?? '';
if (!$src || $w <= 0) {
    http_response_code(400);
    exit('bad request');
}
$src = ltrim($src, '/');
if (!preg_match('/^[A-Za-z0-9_\-\.\/]+$/', $src)) {
    http_response_code(400);
    exit('invalid path');
}
$path = __DIR__ . '/' . $src;
if (!file_exists($path)) {
    http_response_code(404);
    exit('not found');
}
$info = getimagesize($path);
if (!$info) {
    http_response_code(415);
    exit('not image');
}
list($origW, $origH) = $info;
$mime = $info['mime'];
$h = (int)($origH * ($w / $origW));
switch ($mime) {
    case 'image/jpeg':
    case 'image/jpg':
        $srcImg = imagecreatefromjpeg($path);
        break;
    case 'image/png':
        $srcImg = imagecreatefrompng($path);
        break;
    case 'image/gif':
        $srcImg = imagecreatefromgif($path);
        break;
    default:
        http_response_code(415);
        exit('unsupported');
}
$dst = imagecreatetruecolor($w, $h);
if ($mime === 'image/png') {
    imagealphablending($dst, false);
    imagesavealpha($dst, true);
}
imagecopyresampled($dst, $srcImg, 0, 0, 0, 0, $w, $h, $origW, $origH);
$mimeOut = ($format === 'webp') ? 'image/webp' : $mime;
header('Content-Type: ' . $mimeOut);
if ($format === 'webp') {
    imagewebp($dst, null, 85);
} else {
    switch ($mime) {
        case 'image/png':
            imagepng($dst);
            break;
        case 'image/gif':
            imagegif($dst);
            break;
        default:
            imagejpeg($dst, null, 85);
    }
}
imagedestroy($srcImg);
imagedestroy($dst);
