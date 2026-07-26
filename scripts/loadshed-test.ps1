<#
.SYNOPSIS
    Phase 5 load-shed test: enqueue N analyze jobs, confirm the Worker scales out and nothing is lost.

.DESCRIPTION
    Creates N calls through the public API, uploads the same audio file to each call's SAS URL,
    triggers analysis, then polls every call to a terminal state while sampling Worker replica
    count. Reports how many jobs reached each outcome and how far the Worker actually scaled.

    "Nothing is lost" means every accepted job reaches a terminal state — Completed or Failed.
    A job still Pending when the poll window closes counts as lost, because that is exactly what
    a dropped queue message looks like from outside.

    The API rate-limits /analyze to 30 requests per minute per API key (the "ai-cost-guardrail"
    policy). This script paces its analyze calls to stay under that rather than firing all N at
    once, because a 429 means the job was never accepted — that is the limiter working, not the
    queue shedding, and conflating the two would make the result meaningless.

.PARAMETER ApiUrl
    Base URL of the deployed Api, e.g. https://cognilens-dev-api.<region>.azurecontainerapps.io

.PARAMETER ApiKey
    A value from the COGNILENS_API_KEYS list the environment was deployed with.

.PARAMETER AudioPath
    Path to a single audio file, reused for every job. Keep it short — every job is a real
    billed transcription plus a real LLM analysis.

.EXAMPLE
    ./scripts/loadshed-test.ps1 -ApiUrl https://... -ApiKey $env:COGNILENS_API_KEY -AudioPath ./sample.wav
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ApiUrl,
    [Parameter(Mandatory = $true)][string]$ApiKey,
    [Parameter(Mandatory = $true)][string]$AudioPath,
    [int]$Count = 50,
    [string]$ResourceGroup = 'rg-cognilens-dev',
    [string]$WorkerApp = 'cognilens-dev-worker',
    [int]$PollTimeoutMinutes = 45
)

$ErrorActionPreference = 'Stop'
$ApiUrl = $ApiUrl.TrimEnd('/')

if (-not (Test-Path $AudioPath)) { throw "Audio file not found: $AudioPath" }
$audioBytes = [System.IO.File]::ReadAllBytes($AudioPath)
$audioName = Split-Path $AudioPath -Leaf
Write-Host "Audio: $audioName ($([math]::Round($audioBytes.Length / 1MB, 2)) MB), $Count jobs" -ForegroundColor Cyan

$headers = @{ 'X-Api-Key' = $ApiKey }
$jobs = [System.Collections.Generic.List[object]]::new()
$rejected = 0

# --- Phase 1: create + upload + enqueue -------------------------------------------------------
# Analyze calls are paced to stay under the 30/min limiter. Creation and upload are not limited,
# but they are done inline so a failure surfaces against the specific call it belongs to.
$windowStart = Get-Date
$inWindow = 0

for ($i = 1; $i -le $Count; $i++) {
    $create = Invoke-RestMethod -Method Post -Uri "$ApiUrl/api/calls" -Headers $headers `
        -ContentType 'application/json' `
        -Body (@{ originalFileName = "loadshed-$i-$audioName" } | ConvertTo-Json)

    Invoke-WebRequest -Method Put -Uri $create.uploadUrl -Body $audioBytes `
        -Headers @{ 'x-ms-blob-type' = 'BlockBlob' } -ContentType 'application/octet-stream' | Out-Null

    if ($inWindow -ge 28) {
        $elapsed = (Get-Date) - $windowStart
        if ($elapsed.TotalSeconds -lt 61) {
            $wait = [math]::Ceiling(61 - $elapsed.TotalSeconds)
            Write-Host "  rate-limit window: waiting ${wait}s" -ForegroundColor DarkGray
            Start-Sleep -Seconds $wait
        }
        $windowStart = Get-Date
        $inWindow = 0
    }

    try {
        $response = Invoke-WebRequest -Method Post -Uri "$ApiUrl/api/calls/$($create.callId)/analyze" `
            -Headers $headers -ContentType 'application/json'
        $inWindow++
        $jobs.Add([pscustomobject]@{
            CallId        = $create.callId
            CorrelationId = $response.Headers['X-Correlation-Id'] | Select-Object -First 1
            Outcome       = $null
        })
    }
    catch {
        # A 429 here means the limiter rejected the job before it was ever queued. Counted
        # separately from a lost job, because the system never took responsibility for it.
        if ($_.Exception.Response.StatusCode.value__ -eq 429) { $rejected++ }
        else { throw }
    }

    if ($i % 10 -eq 0) { Write-Host "  enqueued $i/$Count" -ForegroundColor DarkGray }
}

Write-Host "`nEnqueued $($jobs.Count), rejected by rate limiter: $rejected" -ForegroundColor Cyan

# --- Phase 2: poll to terminal state while sampling replica count -----------------------------
$deadline = (Get-Date).AddMinutes($PollTimeoutMinutes)
$maxReplicas = 0
$replicaSamples = [System.Collections.Generic.List[int]]::new()

while ((Get-Date) -lt $deadline) {
    $pending = $jobs | Where-Object { $null -eq $_.Outcome }
    if ($pending.Count -eq 0) { break }

    try {
        $replicas = (az containerapp replica list --name $WorkerApp --resource-group $ResourceGroup `
            --query 'length(@)' -o tsv 2>$null)
        if ($replicas -match '^\d+$') {
            $replicaSamples.Add([int]$replicas)
            if ([int]$replicas -gt $maxReplicas) { $maxReplicas = [int]$replicas }
        }
    }
    catch { }

    foreach ($job in $pending) {
        $status = Invoke-RestMethod -Method Get -Uri "$ApiUrl/api/calls/$($job.CallId)" -Headers $headers
        if ($status.status -in @('Completed', 'Failed')) { $job.Outcome = $status.status }
    }

    $done = ($jobs | Where-Object { $null -ne $_.Outcome }).Count
    Write-Host ("  {0}/{1} terminal, worker replicas: {2}" -f $done, $jobs.Count, $maxReplicas)
    Start-Sleep -Seconds 20
}

# --- Report -----------------------------------------------------------------------------------
$completed = ($jobs | Where-Object { $_.Outcome -eq 'Completed' }).Count
$failed = ($jobs | Where-Object { $_.Outcome -eq 'Failed' }).Count
$stuck = ($jobs | Where-Object { $null -eq $_.Outcome }).Count

Write-Host "`n=== Load-shed result ===" -ForegroundColor Cyan
Write-Host "  enqueued            : $($jobs.Count)"
Write-Host "  completed           : $completed"
Write-Host "  failed (terminal)   : $failed"
Write-Host "  still pending       : $stuck"
Write-Host "  rejected (429)      : $rejected"
Write-Host "  peak worker replicas: $maxReplicas"

if ($stuck -gt 0) {
    Write-Host "`nFAIL: $stuck job(s) never reached a terminal state — check the poison queue." -ForegroundColor Red
    $jobs | Where-Object { $null -eq $_.Outcome } | ForEach-Object { Write-Host "  $($_.CallId)" }
    exit 1
}
if ($maxReplicas -le 1) {
    Write-Host "`nFAIL: worker never scaled past $maxReplicas replica(s) — KEDA rule did not fire." -ForegroundColor Red
    exit 1
}

Write-Host "`nPASS: nothing lost, worker scaled to $maxReplicas replicas." -ForegroundColor Green
