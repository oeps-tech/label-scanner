param(
    [Parameter(Mandatory=$true)][string]$Stage,
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory=$true)][string]$Output,
    [Parameter(Mandatory=$true)][string]$Dotnet
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Stage = [IO.Path]::GetFullPath($Stage)
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $Output) { throw "Artifact already exists: $Output." }
$msiVersion = [version]$Version
if ($msiVersion.Major -gt 255 -or $msiVersion.Minor -gt 255 -or $msiVersion.Build -gt 65535) {
    throw 'MSI versions require major/minor <= 255 and patch <= 65535.'
}
$toolDirectory = Join-Path $repo '.tools\wix'
$wix = Join-Path $toolDirectory 'wix.exe'
if (-not (Test-Path -LiteralPath $wix)) {
    & $Dotnet tool install wix --version 4.0.6 --tool-path $toolDirectory --configfile (Join-Path $PSScriptRoot 'wix.nuget.config')
    if ($LASTEXITCODE -ne 0) { throw 'WiX installation failed.' }
}

# Bundle the matching Microsoft runtime already installed on the build agent.
# This copies only runtime/host files, never SDKs; ASP.NET is included for the local WebSocket server.
$runtimeLines = & $Dotnet --list-runtimes
foreach ($framework in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App','Microsoft.AspNetCore.App')) {
    $matchesForFramework = @($runtimeLines | Where-Object { $_ -match ('^' + [regex]::Escape($framework) + ' 10\.0\.\d+ \[') })
    if ($matchesForFramework.Count -eq 0) { throw "Build agent needs .NET 10 $framework." }
    $line = $matchesForFramework | Sort-Object { [version](($_ -split ' ')[1]) } | Select-Object -Last 1
    if ($line -notmatch '^\S+ (\S+) \[(.+)\]$') { throw 'Invalid runtime location.' }
    $runtimeVersion = $Matches[1]; $sharedRoot = $Matches[2]
    $destination = Join-Path $Stage ('runtime\shared\' + $framework)
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Copy-Item -LiteralPath (Join-Path $sharedRoot $runtimeVersion) -Destination $destination -Recurse -Force
    if ($framework -eq 'Microsoft.NETCore.App') {
        $dotnetRoot = Split-Path (Split-Path $sharedRoot -Parent) -Parent
        Copy-Item -LiteralPath (Join-Path $dotnetRoot 'dotnet.exe') -Destination (Join-Path $Stage 'runtime')
        $hostDirectory = Join-Path $Stage 'runtime\host\fxr'
        New-Item -ItemType Directory -Force -Path $hostDirectory | Out-Null
        Copy-Item -LiteralPath (Join-Path $dotnetRoot ('host\fxr\' + $runtimeVersion)) -Destination $hostDirectory -Recurse -Force
        foreach ($notice in @('LICENSE.txt','ThirdPartyNotices.txt')) {
            if (Test-Path -LiteralPath (Join-Path $dotnetRoot $notice)) {
                Copy-Item -LiteralPath (Join-Path $dotnetRoot $notice) -Destination (Join-Path $Stage 'runtime')
            }
        }
    }
}
function Escape-Xml([string]$value) { [Security.SecurityElement]::Escape($value) }
function Stable-Id([string]$value) {
    # Components belong to this product, even when the sticker MSI contains identical runtime files.
    $value = 'OEPS.Scanner/' + $value
    $hash = [Security.Cryptography.SHA256]::Create()
    try { 'id' + ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($value.ToLowerInvariant())))).Replace('-','').Substring(0,30) }
    finally { $hash.Dispose() }
}
$components = New-Object Collections.Generic.List[string]
function Directory-Xml([string]$directory, [string]$relative) {
    $xml = New-Object Text.StringBuilder
    foreach ($file in Get-ChildItem -LiteralPath $directory -File | Sort-Object Name) {
        if ($file.Extension -in @('.pdb','.ps1','.cmd')) { continue }
        $key = ($relative + '/' + $file.Name).TrimStart('/')
        $id = Stable-Id $key
        $components.Add($id)
        $fileId = if ($key -eq 'launcher/Scanner.Launcher.exe') { 'LauncherExe' } else { 'f' + $id }
        [void]$xml.Append('<Component Id="' + $id + '" Guid="' + ([Guid]::ParseExact(($id.Substring(2) + '00'), 'N')).ToString() + '"><File Id="' + $fileId + '" Source="' + (Escape-Xml $file.FullName) + '" /><RegistryValue Root="HKCU" Key="Software\OEPS\Scanner\Installer" Name="' + $id + '" Type="integer" Value="1" KeyPath="yes" /></Component>')
    }
    foreach ($child in Get-ChildItem -LiteralPath $directory -Directory | Sort-Object Name) {
        $childRelative = ($relative + '/' + $child.Name).TrimStart('/')
        $id = Stable-Id ('directory/' + $childRelative)
        $components.Add('c' + $id)
        [void]$xml.Append('<Directory Id="' + $id + '" Name="' + (Escape-Xml $child.Name) + '">')
        [void]$xml.Append((Directory-Xml $child.FullName $childRelative))
        [void]$xml.Append('<Component Id="c' + $id + '" Guid="*"><RemoveFolder Id="r' + $id + '" On="uninstall"/><RegistryValue Root="HKCU" Key="Software\OEPS\Scanner\Installer" Name="' + $id + '" Type="integer" Value="1" KeyPath="yes" /></Component></Directory>')
    }
    $xml.ToString()
}
$tree = Directory-Xml $Stage ''
$refs = ($components | ForEach-Object { '<ComponentRef Id="' + $_ + '"/>' }) -join ''
$icon = Escape-Xml (Join-Path $repo 'assets\app.ico')
$source = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="OEPS Scanner" Manufacturer="OEPS" Version="$Version" Language="1033" UpgradeCode="49835117-1347-4BC7-A126-6B2FE9629269" Scope="perUser">
    <MajorUpgrade DowngradeErrorMessage="A newer OEPS installer is already installed." Schedule="afterInstallInitialize" />
    <MediaTemplate EmbedCab="yes" CompressionLevel="mszip" />
    <Icon Id="AppIcon" SourceFile="$icon" />
    <Property Id="ARPPRODUCTICON" Value="AppIcon" />
    <StandardDirectory Id="LocalAppDataFolder">
      <Directory Id="INSTALLFOLDER" Name="OEPS Scanner Installer">
        $tree
        <Component Id="Shortcuts" Guid="*">
          <Shortcut Id="DesktopShortcut" Directory="DesktopFolder" Name="OEPS Scanner Server" Target="[#LauncherExe]" Arguments="--install-package &quot;[INSTALLFOLDER]OEPS.Scanner-$Version-win-x64.zip&quot;" WorkingDirectory="INSTALLFOLDER" Icon="AppIcon" />
          <Shortcut Id="StartMenuShortcut" Directory="ProgramMenuFolder" Name="OEPS Scanner Server" Target="[#LauncherExe]" Arguments="--install-package &quot;[INSTALLFOLDER]OEPS.Scanner-$Version-win-x64.zip&quot;" WorkingDirectory="INSTALLFOLDER" Icon="AppIcon" />
          <Shortcut Id="ClientDesktopShortcut" Directory="DesktopFolder" Name="OEPS Scanner Test Client" Target="[#LauncherExe]" Arguments="--app TestClient --install-package &quot;[INSTALLFOLDER]OEPS.Scanner-$Version-win-x64.zip&quot;" WorkingDirectory="INSTALLFOLDER" Icon="AppIcon" /><Shortcut Id="ClientStartMenuShortcut" Directory="ProgramMenuFolder" Name="OEPS Scanner Test Client" Target="[#LauncherExe]" Arguments="--app TestClient --install-package &quot;[INSTALLFOLDER]OEPS.Scanner-$Version-win-x64.zip&quot;" WorkingDirectory="INSTALLFOLDER" Icon="AppIcon" /><RemoveFolder Id="RemoveInstallFolder" On="uninstall" />
          <RegistryValue Root="HKCU" Key="Software\OEPS\Scanner\Installer" Name="Installed" Type="integer" Value="1" KeyPath="yes" />
        </Component>
      </Directory>
    </StandardDirectory>
    <StandardDirectory Id="DesktopFolder" />
    <StandardDirectory Id="ProgramMenuFolder" />
    <Feature Id="Main" Title="OEPS Scanner" Level="1"><ComponentRef Id="Shortcuts" />$refs</Feature>
  </Package>
</Wix>
"@
$sourcePath = Join-Path (Split-Path $Stage -Parent) 'installer.wxs'
[IO.File]::WriteAllText($sourcePath,$source,[Text.UTF8Encoding]::new($false))
& $wix build $sourcePath -arch x64 -o $Output
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
