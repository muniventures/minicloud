$ErrorActionPreference = "Stop"

$Repo = if ($env:MUNICLOUD_REPO) { $env:MUNICLOUD_REPO } else { "muniventures/minicloud" }
$Version = if ($env:MUNICLOUD_VERSION) { $env:MUNICLOUD_VERSION } else { "latest" }
$InstallDir = if ($env:MUNICLOUD_INSTALL_DIR) { $env:MUNICLOUD_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA "Municloud\bin" }

$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
switch ($arch) {
  "X64" { $rid = "win-x64" }
  default { throw "Unsupported architecture: $arch" }
}

$asset = "municloud-$rid.zip"
if ($Version -eq "latest") {
  $url = "https://github.com/$Repo/releases/latest/download/$asset"
} else {
  $url = "https://github.com/$Repo/releases/download/$Version/$asset"
}

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("municloud-install-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null

try {
  $zip = Join-Path $tmp $asset
  Write-Host "Downloading $url"
  Invoke-WebRequest -Uri $url -OutFile $zip
  Expand-Archive -Path $zip -DestinationPath $tmp -Force

  $exe = Join-Path $tmp "municloud.exe"
  if (!(Test-Path $exe)) {
    throw "Release asset does not contain executable: municloud.exe"
  }

  New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
  Copy-Item $exe (Join-Path $InstallDir "municloud.exe") -Force

  $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
  $pathParts = $userPath -split ";" | Where-Object { $_ }
  if ($pathParts -notcontains $InstallDir) {
    $newPath = if ($userPath) { "$userPath;$InstallDir" } else { $InstallDir }
    [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
    $env:Path = "$env:Path;$InstallDir"
    Write-Host "Added $InstallDir to user PATH. Open a new terminal if municloud is not found."
  }

  Write-Host "Installed municloud to $(Join-Path $InstallDir 'municloud.exe')"
} finally {
  Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

