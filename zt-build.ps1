# Builds Ultimate Doom Builder (C# part: Builder.exe + plugins, x64 Release) without Visual Studio
# and packs a ready-to-run zip into GIT_Build\.
#
# Toolchain used instead of Visual Studio:
#   - MSBuild 17 from any Visual Studio-based product found by vswhere (e.g. SQL Server Management Studio 21);
#   - the C# compiler (Roslyn) and its MSBuild task from the installed .NET SDK;
#   - .NET Framework 4.x runtime assemblies instead of the 4.7.2 reference assemblies (FrameworkPathOverride).
# The native BuilderNative.dll (C++) is not built: it is copied from an installed UDB of a compatible revision
# (-NativeFrom). The updater is disabled (NO_UPDATER), so the build never offers to "update" to upstream.
#
# Usage (PowerShell): .\zt-build.ps1 [-NativeFrom "<UDB folder with BuilderNative.dll>"] [-NoZip]
#                     .\zt-build.ps1 -Plugin ZTProject   # ZT: пересобрать только плагин (ядро уже собрано в Build\)

param(
	[string]$NativeFrom = "$env:LOCALAPPDATA\Programs\Ultimate Doom Builder",
	[switch]$NoZip,
	[string]$Plugin
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root 'Build'

# --- MSBuild 17
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = $null
if (Test-Path $vswhere) {
	foreach ($dir in & $vswhere -all -products * -prerelease -property installationPath) {
		$cand = Join-Path $dir 'MSBuild\Current\Bin\amd64\MSBuild.exe'
		if (Test-Path $cand) { $msbuild = $cand; break }
	}
}
if (-not $msbuild) { throw 'MSBuild 17 not found (install Visual Studio Build Tools or SSMS 21)' }

# --- Roslyn from the newest .NET SDK
$sdkline = (& dotnet --list-sdks | Select-Object -Last 1)
if (-not $sdkline) { throw '.NET SDK not found' }
$sdkver = $sdkline.Split(' ')[0]
$sdkroslyn = Join-Path (($sdkline -replace '^.*\[(.*)\]$', '$1')) "$sdkver\Roslyn"

# MSBuild 17 loads the Csc task from $(RoslynTargetsPath)\Microsoft.Build.Tasks.CodeAnalysis.dll: assemble such a folder
# from the SDK's .NET Framework task assembly and its targets; the compiler itself is the SDK's csc.exe.
$toolchain = Join-Path $env:TEMP "zt-udb-roslyn-$sdkver"
if (-not (Test-Path "$toolchain\Microsoft.Build.Tasks.CodeAnalysis.dll")) {
	New-Item -ItemType Directory -Force $toolchain | Out-Null
	Copy-Item "$sdkroslyn\binfx\*.dll" $toolchain
	Copy-Item "$toolchain\Microsoft.Build.Tasks.CodeAnalysis.Sdk.dll" "$toolchain\Microsoft.Build.Tasks.CodeAnalysis.dll"
	Copy-Item "$sdkroslyn\*.targets" $toolchain
}
# Legacy .NET Framework project references need no NuGet target framework negotiation (its task is VS-only)
$legacytargets = Join-Path $toolchain 'zt-legacy.targets'
Set-Content -Encoding utf8 $legacytargets '<Project><Target Name="_GetProjectReferenceTargetFrameworkProperties" /></Project>'

$common = @(
	'-nologo', '-v:minimal', '-p:Configuration=Release', '-p:Platform=x64',
	"-p:FrameworkPathOverride=$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319",
	"-p:RoslynTargetsPath=$toolchain", "-p:CscToolPath=$sdkroslyn\bincore", '-p:CscToolExe=csc.exe',
	'-p:UseSharedCompilation=false', "-p:CustomAfterMicrosoftCommonTargets=$legacytargets"
)

function Invoke-Build([string]$project, [string[]]$extra) {
	Write-Host "== $project"
	& $msbuild (Join-Path $root $project) @common @extra
	if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}

# ZT: быстрая пересборка одного плагина — ядро и остальное уже лежат в Build\ (UDB при этом должен быть закрыт)
if ($Plugin) {
	Invoke-Build "Source\Plugins\$Plugin\$Plugin.csproj" @('-p:BuildProjectReferences=false')
	Write-Host "Done: $build\Plugins\$Plugin.dll"
	return
}

# Core first: its build cleans Build\ and copies the assets there
Invoke-Build 'Source\Core\Builder.csproj' @('-p:DefineConstants=TRACE%3BNO_UPDATER')

$plugins = 'AutomapMode\AutomapMode', 'BlockmapExplorer\BlockmapExplorer', 'BuilderEffects\BuilderEffects',
	'BuilderModes\BuilderModes', 'ColorPicker\ColorPicker', 'CommentsPanel\CommentsPanel', 'NodesViewer\NodesViewer',
	'RejectExplorer\RejectExplorer', 'SoundPropagationMode\SoundPropagation', 'StairSectorBuilder\StairSectorBuilder',
	'TagExplorer\TagExplorer', 'TagRange\TagRange', '3DFloorMode\ThreeDFloorMode', 'UDBScript\UDBScript',
	'VisplaneExplorer\VisplaneExplorer', 'ZTProject\ZTProject'
foreach ($p in $plugins) { Invoke-Build "Source\Plugins\$p.csproj" @('-p:BuildProjectReferences=false') }

# ZT: семантическое слияние TEXTMAP — консольная утилита (.NET SDK, net10.0) для драйвера слияния git;
# плагин ZTProject прописывает её в рабочую копию проекта (merge.udmf.driver)
Write-Host "== Source\Tools\ZTMapMerge"
& dotnet build (Join-Path $root 'Source\Tools\ZTMapMerge\ZTMapMerge.csproj') -c Release -o (Join-Path $build 'Tools\ZTMapMerge') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed: ZTMapMerge' }

# Native library from an installed UDB
$native = Join-Path $NativeFrom 'BuilderNative.dll'
if (-not (Test-Path $native)) { throw "BuilderNative.dll not found in $NativeFrom" }
Copy-Item $native $build -Force

if ($NoZip) { Write-Host "Done: $build"; return }

$rev = (& git -C $root rev-list --count HEAD).Trim()
$branch = (& git -C $root branch --show-current).Trim() -replace '[\\/]', '-'
$outdir = Join-Path $root 'GIT_Build'
New-Item -ItemType Directory -Force $outdir | Out-Null
$zip = Join-Path $outdir "UltimateDoomBuilder-ZT-r$rev-$branch-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
$files = Get-ChildItem $build -Recurse -File | Where-Object { $_.Name -notin 'UDBuilder.cfg', 'UDBuilder.log' -and $_.Extension -ne '.xml' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::Open($zip, 'Create')
try {
	foreach ($f in $files) {
		$rel = $f.FullName.Substring($build.Length + 1) -replace '\\', '/' # zip entries use forward slashes
		[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $f.FullName, $rel, 'Optimal')
	}
} finally { $z.Dispose() }
Write-Host "Done: $zip"
