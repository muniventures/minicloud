# SPDX-License-Identifier: MPL-2.0
$ErrorActionPreference = "Stop"

function Install-MinicloudPayload([string]$Payload, [string]$Destination, [string]$Version, [string]$Source) {
  New-Item -ItemType Directory -Path $Destination -Force | Out-Null
  for ($Path = [IO.Path]::GetFullPath($Destination); $Path; $Path = [IO.Path]::GetDirectoryName($Path)) {
    if ((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Install paths cannot contain reparse points." }
  }
  $Trusted = @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value, "S-1-5-18", "S-1-5-32-544")
  $DirectoryAcl = Get-Acl -LiteralPath $Destination
  if ($Trusted -notcontains $DirectoryAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value) { throw "Install directory owner is not trusted." }
  $Writes = [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles `
    -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::ChangePermissions `
    -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
  foreach ($Rule in $DirectoryAcl.Access) {
    if ($Rule.AccessControlType -eq "Allow" -and ($Rule.FileSystemRights -band $Writes) -and $Trusted -notcontains $Rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value) {
      throw "Install directory grants write access to another principal."
    }
  }
  $Exe = Join-Path $Destination "minicloud.exe"
  if ((Test-Path -LiteralPath $Exe) -and ((Get-Item -LiteralPath $Exe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw "Existing executable is a reparse point."
  }
  $Notices = Join-Path $Destination ".minicloud\$Version-$Source"
  foreach ($Path in @((Join-Path $Destination ".minicloud"), $Notices)) {
    if ((Test-Path -LiteralPath $Path) -and ((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Versioned notices cannot use reparse points." }
    if (Test-Path -LiteralPath $Path) {
      $Acl = Get-Acl -LiteralPath $Path
      if ($Trusted -notcontains $Acl.GetOwner([Security.Principal.SecurityIdentifier]).Value) { throw "Versioned notice directory owner is not trusted." }
      foreach ($Rule in $Acl.Access) {
        if ($Rule.AccessControlType -eq "Allow" -and ($Rule.FileSystemRights -band $Writes) -and $Trusted -notcontains $Rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value) { throw "Versioned notices are shared writable." }
      }
    }
  }
  if (!(Test-Path -LiteralPath $Notices)) {
    New-Item -ItemType Directory -Path $Notices -Force | Out-Null
    foreach ($Name in @("LICENSE.txt", "SOURCE.txt", "licenses", "cli-release.json")) {
      if (Test-Path -LiteralPath (Join-Path $Payload $Name)) { Copy-Item -LiteralPath (Join-Path $Payload $Name) -Destination $Notices -Recurse }
    }
  }
  $Candidate = Join-Path $Destination (".minicloud-" + [Guid]::NewGuid().ToString("N") + ".exe")
  try {
    [IO.File]::Copy((Join-Path $Payload "minicloud.exe"), $Candidate, $false)
    if ((Get-FileHash -LiteralPath $Candidate -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $Payload "minicloud.exe") -Algorithm SHA256).Hash) {
      throw "Staged executable digest changed."
    }
    $VersionOutput = & $Candidate --version
    if ($LASTEXITCODE -ne 0 -or !($VersionOutput -contains "minicloud version $($Version.TrimStart('v'))")) { throw "Candidate version check failed." }
    $HelpOutput = & $Candidate --help | Out-String
    if ($LASTEXITCODE -ne 0 -or $HelpOutput -notmatch "minicloud") { throw "Candidate help check failed." }
    if (Test-Path -LiteralPath $Exe) {
      [IO.File]::Replace($Candidate, $Exe, (Join-Path $Destination (".minicloud-previous-" + [Guid]::NewGuid().ToString("N") + ".exe")))
    } else { [IO.File]::Move($Candidate, $Exe) }
  } finally { if (Test-Path -LiteralPath $Candidate) { Remove-Item -LiteralPath $Candidate -Force } }
  $UserPath = [Environment]::GetEnvironmentVariable("Path", "User")
  if (($UserPath -split ";") -notcontains $Destination) {
    [Environment]::SetEnvironmentVariable("Path", "$UserPath;$Destination", "User")
    $env:Path = "$env:Path;$Destination"
  }
  Write-Host "Installed $Exe (source $Source). Existing instance/configuration preserved."
}

$Mode = if ($env:MINICLOUD_INSTALL_MODE) { $env:MINICLOUD_INSTALL_MODE } else { "binary" }
if ($Mode -notin @("binary", "source")) { throw "MINICLOUD_INSTALL_MODE must be binary or source; no automatic fallback." }
if ($Mode -eq "binary") {
  if ($env:MINICLOUD_REPO -and $env:MINICLOUD_REPO -cne "muniventures/minicloud") { throw "Only muniventures/minicloud is allowed." }
  $Tag = $env:MINICLOUD_VERSION
  if ($Tag -cnotmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
    throw "Binary mode requires MINICLOUD_VERSION=<exact published CLI vX.Y.Z[-prerelease]>; no latest, SHA, server-v or unsigned fallback. Use MINICLOUD_INSTALL_MODE=source for canonical source."
  }
  if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne "X64" -or [Environment]::OSVersion.Platform -ne "Win32NT") { throw "Supported target: Windows x64." }
  $Stage = Join-Path $HOME (".minicloud-install-" + [Guid]::NewGuid().ToString("N"))
  New-Item -ItemType Directory -Path $Stage | Out-Null
  $Acl = New-Object Security.AccessControl.DirectorySecurity
  $Sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
  $Acl.SetOwner($Sid)
  $Acl.SetAccessRuleProtection($true, $false)
  $Acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($Sid, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
  Set-Acl -LiteralPath $Stage -AclObject $Acl
  Add-Type -AssemblyName System.Net.Http
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $Handler = New-Object Net.Http.HttpClientHandler
  $Handler.AllowAutoRedirect = $false
  $Http = New-Object Net.Http.HttpClient($Handler)
  $Http.Timeout = [TimeSpan]::FromMinutes(10)
  function Download-Exact([string]$Url, [string]$Target, [long]$Limit) {
    $Uri = [Uri]$Url
    for ($Count = 0; $Count -le 8; $Count++) {
      if ($Uri.Scheme -cne "https" -or $Uri.UserInfo) { throw "Only public HTTPS downloads are allowed." }
      $Response = $Http.GetAsync($Uri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
      try {
        if ([int]$Response.StatusCode -ge 300 -and [int]$Response.StatusCode -lt 400 -and $Response.Headers.Location) {
          $Uri = [Uri]::new($Uri, $Response.Headers.Location); continue
        }
        if (!$Response.IsSuccessStatusCode) { throw "Exact signed release asset unavailable. No fallback." }
        $InputStream = $Response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $OutputStream = [IO.File]::Open($Target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
          $Buffer = New-Object byte[] 65536
          [long]$Total = 0
          while (($Size = $InputStream.Read($Buffer, 0, $Buffer.Length)) -gt 0) {
            $Total += $Size
            if ($Total -gt $Limit) { throw "Download exceeds safety bound." }
            $OutputStream.Write($Buffer, 0, $Size)
          }
          $OutputStream.Flush($true)
        } finally { $InputStream.Dispose(); $OutputStream.Dispose() }
        return
      } finally { $Response.Dispose() }
    }
    throw "Too many HTTPS redirects."
  }
  try {
    $Cosign = Join-Path $Stage "cosign.exe"
    Download-Exact "https://github.com/sigstore/cosign/releases/download/v2.6.5/cosign-windows-amd64.exe" $Cosign 268435456
    if ((Get-FileHash -LiteralPath $Cosign -Algorithm SHA256).Hash.ToLowerInvariant() -cne "19ec346d0050f59ffad9694da97ef9704be34ec60ddc41c5f1d16d8619385825") { throw "Pinned official Cosign 2.6.5 checksum failed." }
    $ArchiveName = "minicloud-$($Tag.Substring(1))-win-x64.zip"
    $Base = "https://github.com/muniventures/minicloud/releases/download/$Tag/"
    foreach ($Name in @("checksums.txt", "checksums.txt.sigstore.json", "cli-provenance.json", $ArchiveName)) {
      Download-Exact ($Base + $Name) (Join-Path $Stage $Name) $(if ($Name -eq $ArchiveName) { 1073741824 } else { 8388608 })
    }
    $Identity = "https://github.com/muniventures/minicloud/.github/workflows/cli-release.yml@refs/tags/$Tag"
    & $Cosign verify-blob --new-bundle-format --bundle (Join-Path $Stage "checksums.txt.sigstore.json") `
      --certificate-identity $Identity --certificate-oidc-issuer https://token.actions.githubusercontent.com (Join-Path $Stage "checksums.txt") 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Publisher authentication failed. No source fallback." }
    $Expected = @("cli-provenance.json", "minicloud-$($Tag.Substring(1))-linux-x64.tar.gz", "minicloud-$($Tag.Substring(1))-linux-arm64.tar.gz", "minicloud-$($Tag.Substring(1))-osx-arm64.tar.gz", $ArchiveName)
    $Inventory = @{}
    foreach ($Line in [IO.File]::ReadAllLines((Join-Path $Stage "checksums.txt"))) {
      if ($Line -cnotmatch '^([0-9a-f]{64})  ([a-zA-Z0-9._-]+)$' -or $Expected -cnotcontains $Matches[2] -or $Inventory.ContainsKey($Matches[2])) { throw "Invalid signed inventory." }
      $Inventory.Add($Matches[2], $Matches[1])
    }
    if ($Inventory.Count -ne $Expected.Count) { throw "Incomplete signed inventory." }
    foreach ($Name in @("cli-provenance.json", $ArchiveName)) {
      if ((Get-FileHash -LiteralPath (Join-Path $Stage $Name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Inventory[$Name]) { throw "Authenticated asset digest mismatch." }
    }
    $Provenance = Get-Content -LiteralPath (Join-Path $Stage "cli-provenance.json") -Raw | ConvertFrom-Json
    if ($Provenance.schemaVersion -ne 1 -or $Provenance.tag -cne $Tag -or $Provenance.workflow -cne $Identity.Split("@")[0] -or $Provenance.sourceRevision -cnotmatch '^[0-9a-f]{40}$') { throw "Invalid source/workflow provenance." }
    $Artifact = $Provenance.artifacts.'win-x64'
    if ($Artifact.name -cne $ArchiveName -or $Artifact.sha256 -cne $Inventory[$ArchiveName]) { throw "Provenance/archive mismatch." }
    $Payload = Join-Path $Stage "payload"
    New-Item -ItemType Directory -Path $Payload | Out-Null
    $Seen = @{}
    [long]$Total = 0
    $Zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $Stage $ArchiveName))
    try {
      foreach ($Entry in $Zip.Entries) {
        $Name = $Entry.FullName
        $UnixType = ($Entry.ExternalAttributes -shr 16) -band 61440
        if ($Name -cnotmatch '^[a-zA-Z0-9._-]+(?:/[a-zA-Z0-9._-]+)*$' -or ($Name.Split("/") | Where-Object { $_.EndsWith(".") -or $_ -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?$' }) `
            -or $UnixType -ne 32768 -or $Seen.ContainsKey($Name) -or $Artifact.files.PSObject.Properties.Name -cnotcontains $Name) { throw "Unsafe/unlisted/duplicate ZIP entry." }
        $Total += $Entry.Length
        if ($Entry.Length -lt 0 -or $Total -gt 1073741824) { throw "Expanded ZIP exceeds safety bound." }
        $Path = Join-Path $Payload $Name
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Path)) -Force | Out-Null
        $EntryInput = $Entry.Open()
        $EntryOutput = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
          [long]$Actual = 0
          $Buffer = New-Object byte[] 65536
          while (($Size = $EntryInput.Read($Buffer, 0, $Buffer.Length)) -gt 0) {
            $Actual += $Size
            if ($Actual -gt $Entry.Length) { throw "ZIP entry exceeds declared size." }
            $EntryOutput.Write($Buffer, 0, $Size)
          }
          if ($Actual -ne $Entry.Length) { throw "Truncated ZIP entry." }
          $EntryOutput.Flush($true)
        } finally { $EntryInput.Dispose(); $EntryOutput.Dispose() }
        if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Artifact.files.PSObject.Properties[$Name].Value) { throw "ZIP member digest mismatch." }
        $Seen.Add($Name, $true)
      }
    } finally { $Zip.Dispose() }
    if ($Seen.Count -ne @($Artifact.files.PSObject.Properties).Count -or !($Seen.ContainsKey("minicloud.exe") -and $Seen.ContainsKey("cli-release.json") -and $Seen.ContainsKey("LICENSE.txt") -and $Seen.ContainsKey("SOURCE.txt"))) { throw "Incomplete CLI archive." }
    $Metadata = Get-Content -LiteralPath (Join-Path $Payload "cli-release.json") -Raw | ConvertFrom-Json
    if ($Metadata.schemaVersion -ne 1 -or $Metadata.tag -cne $Tag -or $Metadata.rid -cne "win-x64" -or $Metadata.sourceRevision -cne $Provenance.sourceRevision -or $Metadata.workflow -cne $Provenance.workflow) { throw "Embedded release metadata mismatch." }
    $VersionOutput = & (Join-Path $Payload "minicloud.exe") --version
    if ($LASTEXITCODE -ne 0 -or $VersionOutput -cnotcontains "minicloud version $($Tag.Substring(1))" -or $VersionOutput -cnotcontains "source $($Provenance.sourceRevision)") { throw "CLI version/source check failed." }
    $Destination = if ($env:MINICLOUD_INSTALL_DIR) { $env:MINICLOUD_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA "Minicloud\bin" }
    Install-MinicloudPayload $Payload $Destination $Tag $Provenance.sourceRevision
  } finally { $Http.Dispose(); $Handler.Dispose(); Remove-Item -LiteralPath $Stage -Recurse -Force -ErrorAction SilentlyContinue }
  exit
}

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

$Tmp = Join-Path $HOME ("minicloud-source-install-" + [System.Guid]::NewGuid().ToString("N"))
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
      "-p:PublishSingleFile=true" "-p:IncludeNativeLibrariesForSelfExtract=true" `
      "-p:PublishTrimmed=false" "-p:IncludeSourceRevisionInInformationalVersion=false" `
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

  Install-MinicloudPayload $PublishDir $InstallDir $CliVersion $ResolvedSha

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
