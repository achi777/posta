param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$Makensis,
    [Parameter(Mandatory)][string]$WebViewBootstrapper,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\setup'),
    [switch]$AllUsers
)

$ErrorActionPreference = 'Stop'
$payload = (Resolve-Path -LiteralPath $PublishDirectory).Path
$compiler = (Resolve-Path -LiteralPath $Makensis).Path
$bootstrapper = (Resolve-Path -LiteralPath $WebViewBootstrapper).Path
$exe = Get-Item -LiteralPath (Join-Path $payload 'Posta.App.exe')
$signature = Get-AuthenticodeSignature -LiteralPath $bootstrapper
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)') {
    throw 'Use the official Microsoft-signed WebView2 Evergreen Bootstrapper.'
}

$output = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$fileName = if ($AllUsers) { 'Posta-Setup-ProgramFiles-x64.exe' } else { 'Posta-Setup-x64.exe' }
$setup = Join-Path $output $fileName
$manifest = Join-Path ([IO.Path]::GetTempPath()) ("posta-uninstall-{0}.nsh" -f [Guid]::NewGuid())
try {
    # Delete only the packaged application files and their empty directories.
    $entries = @(
        foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse) {
            $relative = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('$', '$$')
            'Delete /REBOOTOK "$INSTDIR\{0}"' -f $relative
        }
        foreach ($folder in Get-ChildItem -LiteralPath $payload -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending) {
            $relative = [IO.Path]::GetRelativePath($payload, $folder.FullName).Replace('$', '$$')
            'RMDir /REBOOTOK "$INSTDIR\{0}"' -f $relative
        }
    )
    Set-Content -LiteralPath $manifest -Value $entries -Encoding utf8BOM
    $version = $exe.VersionInfo.FileVersion
    if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Posta.App.exe needs a four-part file version.' }
    $scope = @()
    if ($AllUsers) { $scope += '/DINSTALL_ALL_USERS=1' }
    & $compiler /V3 /WX /INPUTCHARSET UTF8 @scope "/DAPP_VERSION=$version" "/DPAYLOAD=$payload" "/DSETUP_OUTPUT=$setup" `
        "/DWEBVIEW_SETUP=$bootstrapper" "/DUNINSTALL_MANIFEST=$manifest" (Join-Path $PSScriptRoot 'Posta.nsi')
    if ($LASTEXITCODE -ne 0) { throw "makensis failed with exit code $LASTEXITCODE." }
    Get-Item -LiteralPath $setup
}
finally {
    Remove-Item -LiteralPath $manifest -ErrorAction SilentlyContinue
}
