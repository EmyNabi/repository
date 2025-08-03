<?php
header('Content-Type: application/json');
$input = json_decode(file_get_contents('php://input'), true);
$email = $input['email'] ?? '';
$code = random_int(100000, 999999);
if ($email) {
    $subject = 'Account Verification';
    $message = 'Your verification code is: ' . $code;
    $headers = 'From: no-reply@example.com' . "\r\n" .
               'Reply-To: no-reply@example.com' . "\r\n" .
               'X-Mailer: PHP/' . phpversion();
    @mail($email, $subject, $message, $headers);
    echo json_encode(['status' => 'ok', 'code' => strval($code)]);
} else {
    echo json_encode(['status' => 'error', 'message' => 'Email required']);
}
?>
