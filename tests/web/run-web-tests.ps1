# Runs the browser tests of client.js (tests/web/*.html) in headless Edge or Chrome and prints the results.
# Usage: powershell -File tests\web\run-web-tests.ps1     Exit code: number of pages with failures.

$browser = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $browser) {
    Write-Error 'Neither Edge nor Chrome found.'
    exit 1
}

$failedPages = 0
foreach ($page in Get-ChildItem (Join-Path $PSScriptRoot '*.html')) {
    $browserProfile = Join-Path ([IO.Path]::GetTempPath()) ('ombirequests-webtest-' + [Guid]::NewGuid().ToString('N'))
    $url = 'file:///' + ($page.FullName -replace '\\', '/')
    $dom = & $browser --headless=new --disable-gpu --no-first-run "--user-data-dir=$browserProfile" --allow-file-access-from-files `
        --virtual-time-budget=15000 --dump-dom $url 2>$null | Out-String
    Remove-Item -Recurse -Force $browserProfile -ErrorAction SilentlyContinue

    $result = if ($dom -match '(?s)<pre id="result">(.*?)</pre>') { [Net.WebUtility]::HtmlDecode($Matches[1]) } else { 'no result' }
    Write-Output "== $($page.Name)"
    Write-Output $result.Trim()
    if ($result -notmatch 'ALL PASSED') {
        $failedPages++
    }
}

exit $failedPages
