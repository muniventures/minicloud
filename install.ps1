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
  $url = "https://github.com/$Repo/releases/latest/download/$asset"
} else {
  $url = "https://github.com/$Repo/releases/download/$Version/$asset"
}

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("minicloud-install-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null

try {
  $zip = Join-Path $tmp $asset
  Write-Host "Downloading $url"
  Invoke-WebRequest -Uri $url -OutFile $zip
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

