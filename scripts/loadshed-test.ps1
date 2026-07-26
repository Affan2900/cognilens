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

# Windows PowerShell 5.1 negotiates TLS 1.0 by default, which Azure Storage and Container Apps
# both refuse — without this the first upload fails with an unhelpful "connection closed".
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
# Invoke-WebRequest renders a progress bar per call in 5.1 and it dominates the runtime of a
# multi-megabyte upload loop.
$ProgressPreference = 'SilentlyContinue'

# The blob upload and the analyze trigger go through HttpClient
Add-Type -AssemblyName System.Net.Http
$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromMinutes(5)

function Get-ResponseHeader($response, [string]$name) {
    $values = $null
    if ($response.Headers.TryGetValues($name, [ref]$values)) { return @($values)[0] }
    return $null
}

if (-not (Test-Path $AudioPath)) { throw "Audio file not found: $AudioPath" }
$audioBytes = [System.IO.File]::ReadAllBytes($AudioPath)
$audioName = Split-Path $AudioPath -Leaf
Write-Host "Audio: $audioName ($([math]::Round($audioBytes.Length / 1MB, 2)) MB), $Count jobs" -ForegroundColor Cyan

# CallStatus has no JsonStringEnumConverter registered, so the API serialises it as an ordinal:
# Pending=0, Processing=1, Completed=2, Failed=3. Comparing against the names directly matches
# nothing and reports every job as lost. Both forms are accepted so this keeps working if a
# string converter is added later.
function ConvertTo-CallStatusName($value) {
    if ($value -is [string]) { return $value }
    switch ([int]$value) {
        0 { 'Pending' }
        1 { 'Processing' }
        2 { 'Completed' }
        3 { 'Failed' }
        default { "Unknown($value)" }
    }
}

$headers = @{ 'X-Api-Key' = $ApiKey }
$jobs = [System.Collections.Generic.List[object]]::new()
$rejected = 0


# Analyze calls are paced to stay under the 30/min limiter
$windowStart = Get-Date
$inWindow = 0

for ($i = 1; $i -le $Count; $i++) {
    $create = Invoke-RestMethod -Method Post -Uri "$ApiUrl/api/calls" -Headers $headers `
        -ContentType 'application/json' `
        -Body (@{ originalFileName = "loadshed-$i-$audioName" } | ConvertTo-Json)

    $put = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Put, $create.uploadUrl)
    $put.Headers.Add('x-ms-blob-type', 'BlockBlob')
    $put.Content = New-Object System.Net.Http.ByteArrayContent(, $audioBytes)
    $put.Content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/octet-stream')
    $putResponse = $http.SendAsync($put).GetAwaiter().GetResult()
    if (-not $putResponse.IsSuccessStatusCode) {
        throw "Upload for call $($create.callId) failed: $([int]$putResponse.StatusCode) $($putResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult())"
    }

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

    $analyze = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "$ApiUrl/api/calls/$($create.callId)/analyze")
    $analyze.Headers.Add('X-Api-Key', $ApiKey)
    $analyzeResponse = $http.SendAsync($analyze).GetAwaiter().GetResult()

    if ($analyzeResponse.IsSuccessStatusCode) {
        $inWindow++
        $jobs.Add([pscustomobject]@{
            CallId        = $create.callId
            CorrelationId = Get-ResponseHeader $analyzeResponse 'X-Correlation-Id'
            Outcome       = $null
        })
    }
    elseif ([int]$analyzeResponse.StatusCode -eq 429) {
        $rejected++
    }
    else {
        throw "Analyze for call $($create.callId) failed: $([int]$analyzeResponse.StatusCode) $($analyzeResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult())"
    }

    if ($i % 10 -eq 0) { Write-Host "  enqueued $i/$Count" -ForegroundColor DarkGray }
}

Write-Host "`nEnqueued $($jobs.Count), rejected by rate limiter: $rejected" -ForegroundColor Cyan


$deadline = (Get-Date).AddMinutes($PollTimeoutMinutes)
$maxReplicas = 0
$replicaReadFailures = 0

while ((Get-Date) -lt $deadline) {
    $pending = @($jobs | Where-Object { $null -eq $_.Outcome })
    if ($pending.Count -eq 0) { break }
    $replicas = az containerapp revision list --name $WorkerApp --resource-group $ResourceGroup `
        --query "[?properties.active].properties.replicas | [0]" -o tsv
    if ($LASTEXITCODE -eq 0 -and "$replicas".Trim() -match '^\d+$') {
        if ([int]$replicas -gt $maxReplicas) { $maxReplicas = [int]$replicas }
    }
    else {
        $replicaReadFailures++
        Write-Warning "Could not read replica count (az exit $LASTEXITCODE). Peak-replica figure will be unreliable."
    }

    foreach ($job in $pending) {
        $status = Invoke-RestMethod -Method Get -Uri "$ApiUrl/api/calls/$($job.CallId)" -Headers $headers
        $outcome = ConvertTo-CallStatusName $status.status
        if ($outcome -in @('Completed', 'Failed')) { $job.Outcome = $outcome }
    }

    $done = @($jobs | Where-Object { $null -ne $_.Outcome }).Count
    Write-Host ("  {0}/{1} terminal, worker replicas: {2}" -f $done, $jobs.Count, $maxReplicas)
    if ($done -lt $jobs.Count) { Start-Sleep -Seconds 20 }
}

# --- Report -----------------------------------------------------------------------------------
$completed = @($jobs | Where-Object { $_.Outcome -eq 'Completed' }).Count
$failed = @($jobs | Where-Object { $_.Outcome -eq 'Failed' }).Count
$stuck = @($jobs | Where-Object { $null -eq $_.Outcome }).Count

Write-Host "`n=== Load-shed result ===" -ForegroundColor Cyan
Write-Host "  enqueued            : $($jobs.Count)"
Write-Host "  completed           : $completed"
Write-Host "  failed (terminal)   : $failed"
Write-Host "  still pending       : $stuck"
Write-Host "  rejected (429)      : $rejected"
Write-Host "  peak worker replicas: $maxReplicas"

$verdict = 0

if ($stuck -gt 0) {
    Write-Host "`nFAIL: $stuck job(s) never reached a terminal state — check the poison queue." -ForegroundColor Red
    $jobs | Where-Object { $null -eq $_.Outcome } | ForEach-Object { Write-Host "  $($_.CallId)" }
    $verdict = 1
}

if ($replicaReadFailures -gt 0) {
    # Not a pass and not a fail: the scaling claim has no evidence behind it either way. Saying
    # so beats reporting the initial 0 as though it were an observation.
    Write-Host "`nINCONCLUSIVE on scaling: $replicaReadFailures replica read(s) failed, so the peak-replica figure is not trustworthy." -ForegroundColor Yellow
    Write-Host "  Check the platform's own record instead: ContainerAppSystemLogs_CL, Reason_s == 'KEDAScaleTargetActivated'." -ForegroundColor Yellow
    $verdict = 1
}
elseif ($maxReplicas -le 1 -and $jobs.Count -gt 1) {
    Write-Host "`nFAIL: worker never scaled past $maxReplicas replica(s) — KEDA rule did not fire." -ForegroundColor Red
    $verdict = 1
}

if ($verdict -eq 0) {
    Write-Host "`nPASS: nothing lost ($completed completed, $failed failed), peak $maxReplicas worker replica(s)." -ForegroundColor Green
}

exit $verdict
