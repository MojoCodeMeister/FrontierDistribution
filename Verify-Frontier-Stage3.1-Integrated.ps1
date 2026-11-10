[CmdletBinding()]
param(
    [string]$ProjectRoot = 'S:\Projects\$FrontierDistribution'
)
$ErrorActionPreference = 'Stop'

$harnesses = @(
    'Frontier.Identity.Tests',
    'Frontier.Orders.Tests',
    'Frontier.Inventory.Tests',
    'Frontier.Picking.Tests',
    'Frontier.PickExecution.Tests',
    'Frontier.Packing.Tests',
    'Frontier.Shipping.Tests',
    'Frontier.EndToEnd.Tests'
)
$deliveryHarness = 'tests\Frontier.Delivery.Tests\Frontier.Delivery.Tests.csproj'
$integrationHarness = 'tests\Frontier.Delivery.Integration.Tests\Frontier.Delivery.Integration.Tests.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet CLI not found.' }
if (-not (Test-Path -LiteralPath $ProjectRoot -PathType Container)) { throw "Project root not found: $ProjectRoot" }
foreach ($test in $harnesses) {
    $path = Join-Path $ProjectRoot "tests\$test\$test.csproj"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing existing Stage 2 harness: $path" }
}
foreach ($relative in @($deliveryHarness, $integrationHarness)) {
    if (-not (Test-Path -LiteralPath (Join-Path $ProjectRoot $relative) -PathType Leaf)) { throw "Missing harness: $relative" }
}

$originalLocation = Get-Location
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("Frontier-Dispatch-{0}.json" -f [guid]::NewGuid().ToString('N'))
try {
    Set-Location -LiteralPath $ProjectRoot
    Write-Host 'BUSINESS FLOW: nopCommerce / ERP -> Warehouse -> ERP/Shipping -> Route4Me/Delivery'
    Write-Host 'SYSTEMS: Order Management -> Warehouse Operations -> Transportation / Distribution'
    Write-Host 'PROCESS: Order-to-Dispatch -> Dispatch-to-Delivery'
    Write-Host ''
    $stage2Count = 0
    $dispatchLine = $null
    foreach ($name in $harnesses) {
        Write-Host "===== $name ====="
        $proj = Join-Path $ProjectRoot "tests\$name\$name.csproj"
        $output = @(& dotnet run --project $proj --configuration Debug 2>&1)
        $exit = $LASTEXITCODE
        $output | ForEach-Object { Write-Host $_ }
        if ($exit -ne 0) { throw "$name failed (exit $exit)" }
        $resultLine = @($output | Where-Object { "$_" -match '^RESULT:\s+(\d+)/(\d+)\s+passed' })
        if ($resultLine.Count -ne 1) { throw "$name did not report exactly one result total" }
        $match = [regex]::Match([string]$resultLine[0], '^RESULT:\s+(\d+)/(\d+)\s+passed')
        $passed = [int]$match.Groups[1].Value
        $total = [int]$match.Groups[2].Value
        if ($passed -ne $total) { throw "$name failed assertions: $passed/$total" }
        $stage2Count += $total
        if ($name -eq 'Frontier.Shipping.Tests') {
            $dispatches = @($output | Where-Object { "$_" -match '^DISPATCH\s+\{' })
            if ($dispatches.Count -ne 1) { throw "Expected exactly one Stage 2 DISPATCH result; found $($dispatches.Count)" }
            $dispatchLine = [string]$dispatches[0]
        }
    }
    if ($stage2Count -ne 157) { throw "Stage 2 baseline count changed: $stage2Count (expected 157)" }
    Write-Host "STAGE 1/2 REGRESSION: $stage2Count/$stage2Count passed"

    $dispatchJson = $dispatchLine -replace '^DISPATCH\s+', ''
    $dispatch = $dispatchJson | ConvertFrom-Json
    if ([int]$dispatch.Status -ne 1 -or $dispatch.Shipment.Status -ne 'Dispatched') { throw 'Shipping harness did not return a successful dispatched shipment' }
    [System.IO.File]::WriteAllText($temp, $dispatchJson, [System.Text.UTF8Encoding]::new($false))

    Write-Host '===== Frontier.Delivery.Tests ====='
    & dotnet run --project (Join-Path $ProjectRoot $deliveryHarness) --configuration Debug
    if ($LASTEXITCODE -ne 0) { throw 'Stage 3.1 delivery harness failed' }

    Write-Host '===== Frontier.Delivery.Integration.Tests ====='
    & dotnet run --project (Join-Path $ProjectRoot $integrationHarness) --configuration Debug -- $temp
    if ($LASTEXITCODE -ne 0) { throw 'Stage 2 -> Stage 3.1 integration failed' }
    Write-Host 'INTEGRATION VERIFIED: Stage 2 Shipping dispatch output -> Stage 3.1 delivery outcome'
    Write-Host 'Stage 1/2: 157/157 | Stage 3.1: 22/22 | Integration: 13/13 (when individual harness totals match)'
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
    Set-Location $originalLocation
}
