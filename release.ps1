<#
Builds every download and, with -Publish, creates the GitHub release that installed apps update from.

  1. Bump <Version> in Directory.Build.props.
  2. .\release.ps1 -Notes "What changed"            # build only, into release\<version>
  3. .\release.ps1 -Notes "What changed" -Publish   # build + upload to the public releases repo

Installed apps read latest.json from the newest release of $Repo (public, holds only the downloads; the source repo
stays private), so publishing is all it takes for everyone to get the update. Needs: .NET 9 SDK, Python, gh (logged in).
#>
param(
    [string]$Notes = "",
    [switch]$Publish,
    [string]$Repo = "GAGBI123/rl-map-loader-releases"
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (Test-Path -LiteralPath $Notes -PathType Leaf) { $Notes = [IO.File]::ReadAllText((Resolve-Path $Notes)) }

[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { throw 'No <Version> in Directory.Build.props' }
$tag = "v$version"
$out = Join-Path $root "release\$version"
$tmp = Join-Path $root 'release\obj'
Write-Host "RL Map Loader $version"

if ($Publish) {
    # Windows PowerShell turns a native command's stderr into an error under 'Stop'
    $ErrorActionPreference = 'Continue'
    & gh release view $tag --repo $Repo 2>&1 | Out-Null
    $exists = $LASTEXITCODE -eq 0
    $ErrorActionPreference = 'Stop'
    if ($exists) { throw "$Repo already has a release $tag - bump the version in Directory.Build.props" }
}

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
New-Item -ItemType Directory -Force $out | Out-Null

function Invoke-Publish([string]$project, [string]$rid, [bool]$selfContained, [string]$dir) {
    $a = @('publish', (Join-Path $root $project), '-c', 'Release', '-r', $rid, '--self-contained', $selfContained.ToString().ToLower(),
           '-p:PublishSingleFile=true', '-o', $dir, '-nologo', '-v', 'q')
    if ($selfContained) { $a += @('-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true') }
    & dotnet @a
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $project ($rid)" }
}

Write-Host 'Windows (self-contained)...'
Invoke-Publish 'RLMapLoader\RLMapLoader.csproj' 'win-x64' $true "$tmp\win"
Copy-Item "$tmp\win\RLMapLoader.exe" "$out\RLMapLoader-Windows.exe"

Write-Host 'Windows (needs .NET 9)...'
Invoke-Publish 'RLMapLoader\RLMapLoader.csproj' 'win-x64' $false "$tmp\win-net9"
Copy-Item "$tmp\win-net9\RLMapLoader.exe" "$out\RLMapLoader-Windows-NET9.exe"

Write-Host 'Linux x64...'
Invoke-Publish 'RLMapLoader.Linux\RLMapLoader.Linux.csproj' 'linux-x64' $true "$tmp\linux"
Copy-Item "$tmp\linux\rl-map-loader" "$out\rl-map-loader-linux-x64"
& python (Join-Path $root 'tools\make_linux_tar.py') "$out\rl-map-loader-linux-x64" (Join-Path $root 'linux') "$out\RLMapLoader-Linux.tar.gz" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Packing the Linux tarball failed' }

# The update feed: installed apps pick their file by key (self-contained Windows, .NET 9 Windows, Linux).
$files = [ordered]@{ 'windows' = 'RLMapLoader-Windows.exe'; 'windows-net9' = 'RLMapLoader-Windows-NET9.exe'; 'linux-x64' = 'rl-map-loader-linux-x64' }
$assets = [ordered]@{}
foreach ($key in $files.Keys) {
    $path = Join-Path $out $files[$key]
    $assets[$key] = [ordered]@{
        file   = $files[$key]
        size   = (Get-Item $path).Length
        sha256 = (Get-FileHash -Algorithm SHA256 $path).Hash.ToLower()
    }
}
$feed = [ordered]@{ version = $version; tag = $tag; date = (Get-Date -Format 'yyyy-MM-dd'); notes = $Notes.Trim(); assets = $assets }
[IO.File]::WriteAllText((Join-Path $out 'latest.json'), ($feed | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))

$body = @"
$($Notes.Trim())

### Download
- **Windows:** ``RLMapLoader-Windows.exe`` - just run it. (``RLMapLoader-Windows-NET9.exe`` is the small version for PCs that have the .NET 9 Desktop Runtime.)
- **Linux:** ``RLMapLoader-Linux.tar.gz`` - extract it and open ``rl-map-loader`` once; it adds RL Map Loader to your app menu.

Already installed? The app updates itself (Settings > Updates).
"@
[IO.File]::WriteAllText((Join-Path $out 'release-notes.md'), $body.Trim(), (New-Object Text.UTF8Encoding $false))

Get-ChildItem $out | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize | Out-String | Write-Host

if ($Publish) {
    $upload = @('RLMapLoader-Windows.exe', 'RLMapLoader-Windows-NET9.exe', 'RLMapLoader-Linux.tar.gz', 'rl-map-loader-linux-x64', 'latest.json') |
        ForEach-Object { Join-Path $out $_ }
    & gh release create $tag --repo $Repo --title "RL Map Loader $version" --notes-file (Join-Path $out 'release-notes.md') --latest @upload
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
    Write-Host "Published $tag to https://github.com/$Repo/releases/tag/$tag"
} else {
    Write-Host "Built in $out (not published; add -Publish to upload)."
}
