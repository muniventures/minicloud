# SPDX-License-Identifier: MPL-2.0
$ErrorActionPreference = "Stop"

$Repo = if ($env:MINICLOUD_REPO) { $env:MINICLOUD_REPO } else { "muniventures/minicloud" }
$Version = if ($env:MINICLOUD_VERSION) { $env:MINICLOUD_VERSION } else { "7416e1408ecd9d11f58c638f0caf1c28e5fa6e3f" }
$InstallDir = if ($env:MINICLOUD_INSTALL_DIR) { $env:MINICLOUD_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA "Minicloud\bin" }

if ($Repo -ne "muniventures/minicloud") {
  throw "MINICLOUD_REPO must be muniventures/minicloud."
}
if (!(Get-Command git -ErrorAction SilentlyContinue)) {
  throw "Git is required to build the Minicloud CLI from source."
}
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw "The .NET 10 SDK is required to build the Minicloud CLI from source."
}
if (!((& dotnet --list-sdks) -match '^10\.')) {
  throw ".NET 10 SDK is required to build the Minicloud CLI. Install the SDK; this installer will not install it for you."
}

$ExpectedSha = ""
if ($Version -cmatch '^[0-9a-f]{40}$') {
  $ExpectedSha = $Version
  $CliVersion = "0.0.0-source.sha$($Version.Substring(0, 12))"
} elseif ($Version -cmatch '^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$') {
  $CliVersion = $Version.Substring(1)
} else {
  throw "MINICLOUD_VERSION must be a full lowercase commit SHA or an exact CLI tag such as v3.1.0; branches, latest, and server-v tags are not supported."
}

$Rid = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
  "X64" { "win-x64" }
  default { throw "Unsupported architecture: $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture). Supported target is Windows x64." }
}

$Tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("minicloud-install-" + [System.Guid]::NewGuid().ToString("N"))
$SourceDir = Join-Path $Tmp "source"
$PublishDir = Join-Path $Tmp "publish"
$Url = "https://github.com/muniventures/minicloud.git"
New-Item -ItemType Directory -Path $Tmp | Out-Null

try {
  $TagRef = ""
  if (!$ExpectedSha) {
    $TagRef = "refs/tags/$Version"
    $RemoteBefore = @(& git ls-remote $Url $TagRef "$TagRef^{}")
    $RemoteTag = $RemoteBefore | Where-Object { $_ -match "\s$([regex]::Escape($TagRef))(\^\{\})?$" } | Select-Object -Last 1
    if (!$RemoteTag) {
      throw "CLI tag $Version was not found in the canonical repository."
    }
    $ExpectedSha = ($RemoteTag -split '\s+')[0]
  }

  Write-Host "Building Minicloud CLI from muniventures/minicloud at $ExpectedSha"
  & git clone --filter=blob:none --no-tags --single-branch --branch main $Url $SourceDir
  if ($TagRef) {
    & git -C $SourceDir fetch --no-tags origin "${TagRef}:${TagRef}"
    $FetchedSha = & git -C $SourceDir rev-parse "$TagRef^{commit}"
    $RemoteAfter = @(& git ls-remote $Url $TagRef "$TagRef^{}")
    $RemoteTagAfter = $RemoteAfter | Where-Object { $_ -match "\s$([regex]::Escape($TagRef))(\^\{\})?$" } | Select-Object -Last 1
    $RemoteShaAfter = if ($RemoteTagAfter) { ($RemoteTagAfter -split '\s+')[0] } else { "" }
    if ($FetchedSha -ne $ExpectedSha -or $RemoteShaAfter -ne $ExpectedSha) {
      throw "CLI tag $Version changed while its source was being acquired; retry with a reviewed commit SHA."
    }
  }

  & git -C $SourceDir merge-base --is-ancestor $ExpectedSha refs/remotes/origin/main
  if ($LASTEXITCODE -ne 0) {
    throw "Selected CLI source $ExpectedSha is not in canonical main."
  }
  & git -C $SourceDir checkout --detach $ExpectedSha
  $ResolvedSha = & git -C $SourceDir rev-parse HEAD
  if ($ResolvedSha -ne $ExpectedSha) {
    throw "Resolved source revision did not match the selected revision."
  }

  Push-Location $SourceDir
  try {
    & dotnet publish "modules/cli/Minicloud.Cli.csproj" `
      --configuration Release `
      --runtime $Rid `
      --self-contained true `
      --output $PublishDir `
      "-p:Version=$CliVersion" `
      "-p:SourceRevisionId=$ResolvedSha" `
      "-p:InformationalVersion=$CliVersion+sha.$ResolvedSha"
    if ($LASTEXITCODE -ne 0) {
      throw "Minicloud CLI source build failed; the existing installation was not changed."
    }
  } finally {
    Pop-Location
  }

  $AppHost = Join-Path $PublishDir "Minicloud.Cli.exe"
  $Exe = Join-Path $PublishDir "minicloud.exe"
  if (!(Test-Path $AppHost)) {
    throw "Source build did not produce the Minicloud CLI Windows executable for $Rid."
  }
  Move-Item -Path $AppHost -Destination $Exe -Force
  $VersionOutput = & $Exe --version
  if ($LASTEXITCODE -ne 0 -or $VersionOutput -notmatch [regex]::Escape($CliVersion)) {
    throw "Built CLI version verification failed; the existing installation was not changed."
  }
  $HelpOutput = & $Exe --help | Out-String
  if ($LASTEXITCODE -ne 0 -or $HelpOutput -notmatch "minicloud") {
    throw "Built CLI help verification failed; the existing installation was not changed."
  }

  New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
  Copy-Item -Path (Join-Path $PublishDir "*") -Destination $InstallDir -Recurse -Force

  $UserPath = [Environment]::GetEnvironmentVariable("Path", "User")
  $PathParts = $UserPath -split ";" | Where-Object { $_ }
  if ($PathParts -notcontains $InstallDir) {
    $NewPath = if ($UserPath) { "$UserPath;$InstallDir" } else { $InstallDir }
    [Environment]::SetEnvironmentVariable("Path", $NewPath, "User")
    $env:Path = "$env:Path;$InstallDir"
    Write-Host "Added $InstallDir to user PATH. Open a new terminal if minicloud is not found."
  }

  Write-Host "Installed minicloud to $(Join-Path $InstallDir 'minicloud.exe') (source $ResolvedSha)"
} finally {
  Remove-Item -Recurse -Force $Tmp -ErrorAction SilentlyContinue
}
