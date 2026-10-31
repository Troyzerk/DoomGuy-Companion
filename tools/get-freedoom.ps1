# Fetches Freedoom's Phase 1 IWAD into Freedoom\, where the build ships it beside DoomGuy.exe so the app works
# without a copy of Doom. Pinned to one release and checked against its published SHA-256.
# Freedoom is BSD-licensed (Freedoom\COPYING.txt); the .wad itself isn't kept in git.
$ErrorActionPreference = 'Stop'
$version = '0.13.0'
$sha256 = '3f9b264f3e3ce503b4fb7f6bdcb1f419d93c7b546f4df3e874dd878db9688f59'
$url = "https://github.com/freedoom/freedoom/releases/download/v$version/freedoom-$version.zip"

$dest = Join-Path $PSScriptRoot '..\Freedoom'
if (Test-Path (Join-Path $dest 'freedoom1.wad')) { "Freedoom already present in $dest"; return }

$tmp = Join-Path ([IO.Path]::GetTempPath()) "freedoom-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $tmp | Out-Null
try {
    $zip = Join-Path $tmp "freedoom-$version.zip"
    Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
    $got = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($got -ne $sha256) { throw "Checksum mismatch for $url (got $got)." }
    Expand-Archive $zip -DestinationPath $tmp
    New-Item -ItemType Directory -Force $dest | Out-Null
    foreach ($f in 'freedoom1.wad', 'COPYING.txt', 'CREDITS.txt') {
        Copy-Item (Join-Path $tmp "freedoom-$version\$f") (Join-Path $dest $f) -Force
    }
    "Freedoom $version -> $dest"
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
