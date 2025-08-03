<?php
header('Content-Type: application/json');
$input = json_decode(file_get_contents('php://input'), true);
$email = $input['email'] ?? '';
$code = $input['code'] ?? '';
if ($email && $code) {
    $subject = 'Account Verification';
    $message = 'Your verification code is: ' . $code;
    $headers = 'From: no-reply@example.com' . "\r\n" .
               'Reply-To: no-reply@example.com' . "\r\n" .
               'X-Mailer: PHP/' . phpversion();
    @mail($email, $subject, $message, $headers);
}
echo json_encode(['status' => 'ok']);
?>
