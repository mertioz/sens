param([switch]$NoLaunch)

$ErrorActionPreference = 'Stop'
$package = Join-Path $PSScriptRoot 'dist/Sens/Sens.exe'
if (-not (Test-Path -LiteralPath $package)) { throw 'Compile ou publie Sens avant de lancer ce script.' }
$folder = Join-Path $env:LOCALAPPDATA 'Programs/Sens'
$target = Join-Path $folder 'Sens.exe'
foreach ($running in @(Get-Process Sens -ErrorAction SilentlyContinue)) {
    if ($running.Path -eq $target) { throw 'Ouvre le panneau Sens et ferme-le avec Alt+F4, puis relance le script.' }
}
New-Item -ItemType Directory -Path $folder -Force | Out-Null
Copy-Item -LiteralPath $package -Destination $target -Force

$shell = New-Object -ComObject WScript.Shell
$startup = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup, [Environment+SpecialFolderOption]::Create)
$locations = @(
    [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory),
    [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs),
    $startup
)
foreach ($location in $locations) {
    if (-not (Test-Path -LiteralPath $location)) { continue }
    $shortcut = Join-Path $location 'Sens.lnk'
    if ((Test-Path -LiteralPath $shortcut) -and $shell.CreateShortcut($shortcut).TargetPath -ne $target) {
        $shortcut = Join-Path $location 'Sens - Traduction IA.lnk'
        if ((Test-Path -LiteralPath $shortcut) -and $shell.CreateShortcut($shortcut).TargetPath -ne $target) { continue }
    }
    $link = $shell.CreateShortcut($shortcut)
    $link.TargetPath = $target
    $link.Arguments = if ($location -eq $startup) { '--background' } else { '' }
    $link.WorkingDirectory = $folder
    $link.IconLocation = "$target,0"
    $link.Description = 'Sens - Traduction IA pour Windows'
    $link.Save()
}

if (-not $NoLaunch) { Start-Process -FilePath $target }
Write-Output $target
