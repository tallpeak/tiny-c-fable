$ErrorActionPreference = "Stop"

$archive = Join-Path $PSScriptRoot "tc.7z"
$staging = Join-Path $PSScriptRoot ".deploy"
$remote = "root@qomph.com"
$remoteDirectory = "/var/www/qomph.com/tc"

Push-Location $PSScriptRoot
try {
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "Fable build failed" }

    Remove-Item $archive -Force -ErrorAction SilentlyContinue
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

    $samples = Join-Path $staging "reference/tiny-c/SamplePrograms"
    $libraries = Join-Path $staging "reference/tiny-c/pps"
    New-Item $samples -ItemType Directory -Force | Out-Null
    New-Item $libraries -ItemType Directory -Force | Out-Null

    Copy-Item ".\dist" $staging -Recurse
    Copy-Item ".\web" $staging -Recurse
    Copy-Item ".\reference\tiny-c\SamplePrograms\*" $samples -Recurse
    Copy-Item ".\reference\tiny-c\pps\*" $libraries -Recurse

    Push-Location $staging
    try {
        & 7z a $archive ".\*"
        if ($LASTEXITCODE -ne 0) { throw "Could not create $archive" }
    }
    finally {
        Pop-Location
    }

    & scp $archive "${remote}:${remoteDirectory}/tc.7z"
    if ($LASTEXITCODE -ne 0) { throw "Upload failed" }

    & ssh $remote "set -e; mkdir -p '$remoteDirectory'; cd '$remoteDirectory'; rm -rf dist web reference SamplePrograms pps; rm -f ./*.tc; 7z x tc.7z -y"
    if ($LASTEXITCODE -ne 0) { throw "Remote extraction failed" }
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    Pop-Location
}
