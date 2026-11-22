# Builds Paneless' small on-device helper (app names and launcher icons) into
# src/Paneless.Core/Assets/paneless-helper.jar. The built file is committed, so a normal build of
# Paneless doesn't need any of this; run it only after changing the Java source.
# Needs a JDK (Android Studio's own works) and the Android SDK (a platform and build-tools).
param(
    [string]$Sdk = "$env:LOCALAPPDATA\Android\Sdk",
    [string]$Jdk = "C:\Program Files\Android\Android Studio\jbr"
)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$platform = Get-ChildItem "$Sdk\platforms" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$buildTools = Get-ChildItem "$Sdk\build-tools" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$androidJar = Join-Path $platform.FullName "android.jar"
$out = Join-Path $here "out"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory "$out\classes" | Out-Null

$sources = @(Get-ChildItem "$here\src" -Recurse -Filter *.java | ForEach-Object FullName)
& "$Jdk\bin\javac.exe" --release 11 -nowarn -classpath $androidJar -d "$out\classes" $sources
if ($LASTEXITCODE) { throw "javac failed" }
$classes = @(Get-ChildItem "$out\classes" -Recurse -Filter *.class | ForEach-Object FullName)
$env:JAVA_HOME = $Jdk
& "$($buildTools.FullName)\d8.bat" --release --min-api 26 --lib $androidJar --output "$out\paneless-helper.jar" $classes
if ($LASTEXITCODE) { throw "d8 failed" }
$target = Join-Path $here "..\..\src\Paneless.Core\Assets\paneless-helper.jar"
New-Item -ItemType Directory (Split-Path $target) -Force | Out-Null
Copy-Item "$out\paneless-helper.jar" $target -Force
Remove-Item $out -Recurse -Force
Write-Host "Built $((Resolve-Path $target).Path) ($((Get-Item $target).Length) bytes) with $($platform.Name), build-tools $($buildTools.Name)"
