param(
    [string]$Version = "0.1.0",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repo "artifacts"))
$name = "Tesseris-PreAlpha-$Version-win-x64"
$stage = [IO.Path]::GetFullPath((Join-Path $artifacts $name))
$zip = [IO.Path]::GetFullPath((Join-Path $artifacts "$name.zip"))
$runtimeStage = [IO.Path]::GetFullPath((Join-Path $artifacts ".runtime-payload-$Version-win-x64"))
$runtimeZip = [IO.Path]::GetFullPath((Join-Path $artifacts ".runtime-payload-$Version-win-x64.zip"))

foreach ($path in @($stage, $runtimeStage, $runtimeZip)) {
    if (-not $path.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release staging path escaped the artifacts directory: $path"
    }
}

dotnet build (Join-Path $repo "Tesseris.sln") -c Release --nologo -m:1
if ($LASTEXITCODE -ne 0) { throw "Release build failed." }

if (-not $SkipTests) {
    dotnet test (Join-Path $repo "tests\Tesseris.Tests.csproj") -c Release --no-build --nologo -m:1
    if ($LASTEXITCODE -ne 0) { throw "Release tests failed." }
}

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
if (Test-Path -LiteralPath $runtimeStage) { Remove-Item -LiteralPath $runtimeStage -Recurse -Force }
if (Test-Path -LiteralPath $runtimeZip) { Remove-Item -LiteralPath $runtimeZip -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
New-Item -ItemType Directory -Path $runtimeStage -Force | Out-Null

# The framework-dependent game and its content become an embedded payload. The single-file
# launcher carries the .NET runtime and extracts this payload into LocalAppData on first launch.
dotnet publish (Join-Path $repo "src\Game\Game.csproj") `
    -c Release -r win-x64 --self-contained false --nologo `
    -p:UseAppHost=false -p:DebugType=None -p:DebugSymbols=false `
    -o $runtimeStage
if ($LASTEXITCODE -ne 0) { throw "Game payload publish failed." }

Get-ChildItem -LiteralPath $runtimeStage -Filter *.pdb -Recurse | Remove-Item -Force
if (-not (Test-Path -LiteralPath (Join-Path $runtimeStage "Tesseris.dll"))) {
    throw "Published game payload is missing Tesseris.dll."
}
Compress-Archive -Path (Join-Path $runtimeStage "*") -DestinationPath $runtimeZip -CompressionLevel Optimal

dotnet publish (Join-Path $repo "src\Launcher\Tesseris.Launcher.csproj") `
    -c Release -r win-x64 --self-contained true --nologo `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    -p:TesserisSkipGameDistribution=true `
    "-p:TesserisRuntimePayloadPath=$runtimeZip" `
    -o $stage
if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed." }

$launcher = Join-Path $stage "Tesseris.Launcher.exe"
if (-not (Test-Path -LiteralPath $launcher)) { throw "Published launcher executable is missing." }
Move-Item -LiteralPath $launcher -Destination (Join-Path $stage "Tesseris.exe")

Get-ChildItem -LiteralPath $stage -Filter *.pdb -Recurse | Remove-Item -Force
foreach ($runtimeFolder in @("assets", "saves", ".voxelity", ".tesseris", "mods")) {
    $target = Join-Path $stage $runtimeFolder
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
}
foreach ($metadata in @("Tesseris.deps.json", "Tesseris.runtimeconfig.json")) {
    $target = Join-Path $stage $metadata
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
}
New-Item -ItemType Directory -Path (Join-Path $stage "mods") -Force | Out-Null

$ruby = Join-Path $repo "examples\RubyWorldgenMod\bin\Release\net8.0\tesseris-mod\ruby-1.0.0.tmod"
if (-not (Test-Path -LiteralPath $ruby)) { throw "Ruby example package is missing." }
Copy-Item -LiteralPath $ruby -Destination (Join-Path $stage "mods\ruby-1.0.0.tmod")
Copy-Item -LiteralPath (Join-Path $repo "packaging\README-PREALPHA.txt") -Destination (Join-Path $stage "README.txt")

$looseDlls = @(Get-ChildItem -LiteralPath $stage -Filter *.dll -File)
if ($looseDlls.Count -ne 0) {
    throw "Single-file release contains unexpected loose DLLs: $($looseDlls.Name -join ', ')"
}
$allowedRootEntries = @("Tesseris.exe", "mods", "README.txt")
$unexpectedRootEntries = @(Get-ChildItem -LiteralPath $stage -Force | Where-Object {
    $_.Name -notin $allowedRootEntries
})
if ($unexpectedRootEntries.Count -ne 0) {
    throw "Single-file release contains unexpected root entries: $($unexpectedRootEntries.Name -join ', ')"
}

Remove-Item -LiteralPath $runtimeStage -Recurse -Force
Remove-Item -LiteralPath $runtimeZip -Force

Compress-Archive -LiteralPath $stage -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
Write-Host "Release: $zip"
Write-Host "SHA256:  $hash"
