#Requires -Version 5.1
<#
  Builds the Paneless Windows installer into .\releases with Velopack:
    PanelessApp-win-Setup.exe     installer (no admin rights; includes .NET and scrcpy)
    PanelessApp-win-Portable.zip  runs without installing
    *.nupkg + releases.win.json   files for automatic updates later
  The version comes from <Version> in src/Paneless.App/Paneless.App.csproj.
  scrcpy is downloaded once from its official GitHub release and checked against a pinned SHA-256.
#>
param([string]$Rid = 'win-x64')

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root
$env:DOTNET_NOLOGO = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH" }

# --- scrcpy to bundle (update all three together)
$ScrcpyVersion = '5.0.1'
$ScrcpyUrl = "https://github.com/Genymobile/scrcpy/releases/download/v$ScrcpyVersion/scrcpy-win64-v$ScrcpyVersion.zip"
$ScrcpySha256 = 'b12a2c4ee8be317422451fc7dcf8ee20a71b5ea7ef9ad73ddd825a25316227a5'

$csproj = Join-Path $root 'src\Paneless.App\Paneless.App.csproj'
$version = ([xml](Get-Content -Raw $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in Paneless.App.csproj' }
$authors = ([xml](Get-Content -Raw $csproj)).Project.PropertyGroup.Authors | Where-Object { $_ } | Select-Object -First 1
if (-not $authors) { $authors = 'Paneless' }
Write-Host "Paneless $version ($Rid)"

$cache = Join-Path $root 'build-cache'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$zip = Join-Path $cache "scrcpy-win64-v$ScrcpyVersion.zip"
if (-not (Test-Path $zip)) {
    Write-Host "Downloading scrcpy $ScrcpyVersion"
    Invoke-WebRequest $ScrcpyUrl -OutFile $zip -UseBasicParsing
}
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $ScrcpySha256) { Remove-Item $zip; throw "scrcpy download doesn't match the pinned SHA-256 ($hash). Not using it." }

# --- self-contained publish: the installed app needs no .NET on the PC
$publish = Join-Path $cache "publish-$Rid"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $csproj -c Release -r $Rid --self-contained true -o $publish -nologo -v q
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

# --- bundle scrcpy (with its licence) in a scrcpy\ subfolder, where Paneless looks first
$unz = Join-Path $cache "scrcpy-unzip"
if (Test-Path $unz) { Remove-Item $unz -Recurse -Force }
Expand-Archive $zip -DestinationPath $unz
$inner = Get-ChildItem $unz -Directory | Select-Object -First 1
Move-Item $inner.FullName (Join-Path $publish 'scrcpy')
Remove-Item $unz -Recurse -Force
foreach ($junk in 'open_a_terminal_here.bat', 'scrcpy-noconsole.vbs') { Remove-Item (Join-Path $publish "scrcpy\$junk") -ErrorAction SilentlyContinue }

# --- installer
$out = Join-Path $root 'releases'
New-Item -ItemType Directory -Path $out -Force | Out-Null

# Rebuilding a version that was already packed: drop the old build of that version first
# (Velopack refuses to pack a version equal to one it already has).
$feed = Join-Path $out 'releases.win.json'
if (Test-Path $feed) {
    $json = Get-Content -Raw $feed | ConvertFrom-Json
    $same = @($json.Assets | Where-Object { $_.Version -eq $version })
    if ($same.Count) {
        Write-Host "Replacing the earlier build of $version"
        foreach ($a in $same) { Remove-Item (Join-Path $out $a.FileName) -ErrorAction SilentlyContinue }
        $json.Assets = @($json.Assets | Where-Object { $_.Version -ne $version })
        if ($json.Assets.Count) {
            $json | ConvertTo-Json -Depth 5 | Set-Content $feed -Encoding UTF8
            $releasesFile = Join-Path $out 'RELEASES'
            if (Test-Path $releasesFile) { (Get-Content $releasesFile) | Where-Object { $_ -notmatch [regex]::Escape("-$version-") } | Set-Content $releasesFile -Encoding ASCII }
        } else {
            Remove-Item $feed, (Join-Path $out 'RELEASES') -ErrorAction SilentlyContinue
        }
    }
}
dotnet tool restore | Out-Null
dotnet vpk pack --packId PanelessApp --packVersion $version --packDir $publish --mainExe Paneless.exe `
    --packTitle Paneless --packAuthors $authors --icon (Join-Path $root 'Paneless.ico') --outputDir $out
if ($LASTEXITCODE) { throw 'vpk pack failed' }
Get-ChildItem $out | Where-Object { $_.Name -like "*$version*" -or $_.Name -like 'PanelessApp-*' -or $_.Name -like 'releases*' } |
    ForEach-Object { Write-Host ("  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
