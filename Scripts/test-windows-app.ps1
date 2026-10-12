[CmdletBinding()]
param([Parameter(Mandatory)][string]$Bundle,[switch]$Generate)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bundleRoot = (Resolve-Path -LiteralPath $Bundle).Path
$executable = Join-Path $bundleRoot 'Passphrase Memorizer.exe'
if (!(Test-Path -LiteralPath $executable)) { throw 'Windows application executable is missing.' }
$modes = @(@('--smoke-test'),@('--sandbox-test'),@('--cancel-test'))
if ($Generate) { $modes += ,@('--integration-test') }
foreach ($mode in $modes) {
    $testId = [Guid]::NewGuid().ToString('N')
    $stdout = Join-Path $root ('.build\public-fixture-' + $testId + '.stdout.txt')
    $stderr = Join-Path $root ('.build\public-fixture-' + $testId + '.stderr.txt')
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $executable -ArgumentList $mode -WorkingDirectory $bundleRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (!$process.WaitForExit(660000)) { $process.Kill(); $process.WaitForExit(); throw ('Public test timed out: ' + ($mode -join ' ')) }
    $process.Refresh()
    $output = [IO.File]::ReadAllText($stdout).Trim()
    $errorOutput = [IO.File]::ReadAllText($stderr).Trim()
    if ($process.ExitCode -ne 0 -or $output -notmatch 'PUBLIC_FIXTURE_RESULT PASS' -or $errorOutput.Length -gt 0) {
        throw ('Public test failed: ' + ($mode -join ' ') + '; exit=' + $process.ExitCode + '; result=' + $output + '; stderr=' + $errorOutput)
    }
    [pscustomobject]@{ mode=($mode -join ' '); status='passed'; seconds=[Math]::Round($watch.Elapsed.TotalSeconds,2); summary=$output } | ConvertTo-Json -Compress
}
