# Test API script

Write-Host "`n=== Testing Support Chat API ===" -ForegroundColor Cyan

# Test 1: Create a chat session
Write-Host "`n1. Creating a chat session..." -ForegroundColor Yellow
$body = '{"customerReference": "customer123"}'
try {
    $response = Invoke-RestMethod -Uri "http://localhost:5000/api/chat/sessions" -Method POST -Body $body -ContentType "application/json"
    Write-Host "✓ Session created successfully!" -ForegroundColor Green
    $response | Format-List
    $sessionId = $response.id
    Write-Host "Session ID: $sessionId" -ForegroundColor Cyan
} catch {
    Write-Host "✗ Error creating session: $_" -ForegroundColor Red
    exit 1
}

# Test 2: Get session status
Write-Host "`n2. Getting session status..." -ForegroundColor Yellow
try {
    $status = Invoke-RestMethod -Uri "http://localhost:5000/api/chat/sessions/$sessionId" -Method GET
    Write-Host "✓ Session status retrieved!" -ForegroundColor Green
    $status | Format-List
} catch {
    Write-Host "✗ Error getting status: $_" -ForegroundColor Red
}

# Test 3: Poll for assignment
Write-Host "`n3. Polling for assignment..." -ForegroundColor Yellow
$maxAttempts = 10
$attempt = 0
$assigned = $false

while ($attempt -lt $maxAttempts -and -not $assigned) {
    $attempt++
    try {
        $poll = Invoke-RestMethod -Uri "http://localhost:5000/api/chat/sessions/$sessionId/poll" -Method GET
        Write-Host "Poll $attempt - Status: $($poll.status)" -ForegroundColor White
        
        if ($poll.status -eq "ASSIGNED") {
            Write-Host "✓ Session assigned!" -ForegroundColor Green
            Write-Host "  Agent: $($poll.agentId)" -ForegroundColor Cyan
            Write-Host "  Team: $($poll.team)" -ForegroundColor Cyan
            $assigned = $true
        } elseif ($poll.status -eq "WAIT") {
            Write-Host "  Waiting for assignment..." -ForegroundColor Gray
            Start-Sleep -Seconds 2
        } else {
            Write-Host "  Status: $($poll.status)" -ForegroundColor Yellow
            break
        }
    } catch {
        Write-Host "✗ Error polling: $_" -ForegroundColor Red
        break
    }
}

if (-not $assigned) {
    Write-Host "`n⚠ Session was not assigned within $maxAttempts attempts" -ForegroundColor Yellow
}

Write-Host "`n=== Testing complete ===" -ForegroundColor Cyan
