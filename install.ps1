# SPDX-License-Identifier: MPL-2.0
$ErrorActionPreference = "Stop"

$Repo = if ($env:MINICLOUD_REPO) { $env:MINICLOUD_REPO } else { "muniventures/minicloud" }
$Version = if ($env:MINICLOUD_VERSION) { $env:MINICLOUD_VERSION } else { "latest" }
$InstallDir = if ($env:MINICLOUD_INSTALL_DIR) { $env:MINICLOUD_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA "Minicloud\bin" }

$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
switch ($arch) {
  "X64" { $rid = "win-x64" }
  default { throw "Unsupported architecture: $arch" }
}

$asset = "minicloud-$rid.zip"
if ($Version -eq "latest") {
  $release = "https://github.com/$Repo/releases/latest/download"
} else {
  $release = "https://github.com/$Repo/releases/download/$Version"
}

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("minicloud-install-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null

try {
  $zip = Join-Path $tmp $asset
  $url = "$release/$asset"
  Write-Host "Downloading $url"
  Invoke-WebRequest -Uri $url -OutFile $zip
  $checksums = Join-Path $tmp "checksums.txt"
  Invoke-WebRequest -Uri "$release/checksums.txt" -OutFile $checksums
  $assetPattern = [regex]::Escape($asset)
  $matchesForAsset = @(Get-Content $checksums | Select-String -Pattern "^([a-fA-F0-9]{64})\s+$assetPattern$")
  if ($matchesForAsset.Count -ne 1 -or
      (Get-FileHash -Algorithm SHA256 -Path $zip).Hash -ne $matchesForAsset[0].Matches[0].Groups[1].Value) {
    throw "Release archive checksum verification failed: $asset"
  }
  Expand-Archive -Path $zip -DestinationPath $tmp -Force

  $exe = Join-Path $tmp "minicloud.exe"
  if (!(Test-Path $exe)) {
    throw "Release asset does not contain executable: minicloud.exe"
  }

  New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
  Copy-Item $exe (Join-Path $InstallDir "minicloud.exe") -Force

  $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
  $pathParts = $userPath -split ";" | Where-Object { $_ }
  if ($pathParts -notcontains $InstallDir) {
    $newPath = if ($userPath) { "$userPath;$InstallDir" } else { $InstallDir }
    [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
    $env:Path = "$env:Path;$InstallDir"
    Write-Host "Added $InstallDir to user PATH. Open a new terminal if minicloud is not found."
  }

  Write-Host "Installed minicloud to $(Join-Path $InstallDir 'minicloud.exe')"
} finally {
  Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

