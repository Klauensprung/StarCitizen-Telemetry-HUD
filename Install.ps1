[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $ExecutablePath
)

$ErrorActionPreference = 'Stop'
$appName = 'Star Citizen Telemetry HUD'
$exeName = 'StarCitizenOverlay.exe'
$installDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/StarCitizenTelemetryHud'
$installExe = Join-Path $installDir $exeName

if (-not $ExecutablePath) {
    $ExecutablePath = Join-Path $PSScriptRoot $exeName
    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        $project = Join-Path $PSScriptRoot 'Overlay.csproj'
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
            throw 'Keep Install.ps1 beside StarCitizenOverlay.exe, or run it from the source folder.'
        }
        $publishDir = Join-Path $PSScriptRoot 'artifacts/install-win-x64'
        & dotnet publish $project -c Release -f net10.0-windows -r win-x64 --self-contained true -p:TargetFrameworks=net10.0-windows -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $publishDir
        if ($LASTEXITCODE -ne 0) { throw 'Building the Windows installer payload failed.' }
        $ExecutablePath = Join-Path $publishDir $exeName
    }
}

$sourceExe = Get-Item -LiteralPath $ExecutablePath
if ($sourceExe.PSIsContainer -or $sourceExe.Name -ne $exeName) {
    throw "Choose a published $exeName."
}

New-Item -ItemType Directory -Path $installDir -Force | Out-Null
if ($sourceExe.FullName -ne $installExe) {
    $stagedExe = Join-Path $installDir ([Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Copy-Item -LiteralPath $sourceExe.FullName -Destination $stagedExe
        if (Test-Path -LiteralPath $installExe -PathType Leaf) {
            try { [IO.File]::Replace($stagedExe, $installExe, [NullString]::Value) }
            catch [IO.IOException] { throw 'Close the installed HUD from the tray and run Install.ps1 again.' }
        } else {
            [IO.File]::Move($stagedExe, $installExe)
        }
    } finally {
        if (Test-Path -LiteralPath $stagedExe) { Remove-Item -LiteralPath $stagedExe -Force }
    }
}

$programs = [Environment]::GetFolderPath('Programs')
if (-not $programs) { throw 'Windows did not provide a Start menu Programs folder.' }
New-Item -ItemType Directory -Path $programs -Force | Out-Null
$shortcutPath = Join-Path $programs "$appName.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $installExe
$shortcut.WorkingDirectory = $installDir
$shortcut.IconLocation = "$installExe,0"
$shortcut.Description = 'Open the Star Citizen telemetry overlay.'
$shortcut.Save()

Write-Output "Installed $appName. Search for it in Windows Start."
