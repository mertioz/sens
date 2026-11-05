param(
    [string]$App = (Join-Path $PSScriptRoot '../src/Sens/bin/Release/net10.0-windows/Sens.exe'),
    [switch]$Edge,
    [switch]$Background
)

# Run on the interactive desktop while no other Sens instance is open.
$ErrorActionPreference = 'Stop'
if (Get-Process Sens -ErrorAction SilentlyContinue) { throw 'Quitte Sens avant le test.' }
if (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Sens/preferences.json')) {
    throw 'Ce test doit utiliser un profil Windows qui n''a pas encore configuré Sens.'
}
$App = (Resolve-Path -LiteralPath $App).Path
Add-Type -AssemblyName PresentationFramework, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SensSmoke {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int L,T,R,B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr w, out Rect r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(IntPtr type, string title);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr w, uint message, UIntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr w);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr w);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
}
"@
[SensSmoke]::SetProcessDpiAwarenessContext([intptr](-4)) | Out-Null
function Pause-Ui([int]$ms = 120) {
    $frame = [System.Windows.Threading.DispatcherFrame]::new()
    $timer = [System.Windows.Threading.DispatcherTimer]::new()
    $timer.Interval = [TimeSpan]::FromMilliseconds($ms)
    $timer.add_Tick({
        $timer.Stop()
        $frame.Continue = $false
    }.GetNewClosure())
    $timer.Start()
    [System.Windows.Threading.Dispatcher]::PushFrame($frame)
}
function Wait-Ui([scriptblock]$Check, [string]$Problem) {
    for ($i = 0; $i -lt 55; $i++) {
        $found = & $Check
        if ($found) { return $found }
        Pause-Ui 65
    }
    throw $Problem
}
function Click-Point([int]$x, [int]$y, [switch]$Right) {
    [SensSmoke]::SetCursorPos($x, $y) | Out-Null
    if ($Right) { $down = 8; $up = 16 } else { $down = 2; $up = 4 }
    [SensSmoke]::mouse_event($down, 0, 0, 0, [uintptr]::Zero)
    Pause-Ui 100
    [SensSmoke]::mouse_event($up, 0, 0, 0, [uintptr]::Zero)
}
function Find-Visible([string]$title) {
    $h = [SensSmoke]::FindWindow([intptr]::Zero, $title)
    if ($h -ne [intptr]::Zero -and [SensSmoke]::IsWindowVisible($h)) { return $h }
}
function Invoke-Ui($element, [string]$name) {
    $filter = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $name)
    $buttonType = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)
    $buttonFilter = [Windows.Automation.AndCondition]::new($filter, $buttonType)
    $button = $element.FindFirst([Windows.Automation.TreeScope]::Descendants, $buttonFilter)
    if (-not $button) { throw "Contrôle introuvable : $name" }
    $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
$proc = if ($Background) {
    Start-Process -FilePath $App -ArgumentList '--background' -WindowStyle Hidden -PassThru
} else {
    Start-Process -FilePath $App -PassThru
}
$fixture = $null
$browser = $null
$profileRoot = $null
try {
    if ($Background) {
        $null = Wait-Ui {
            $nativeHost = [SensSmoke]::FindWindow([intptr]::Zero, 'Sens background')
            $nativeHost -ne [intptr]::Zero
        } 'Sens ne démarre pas en arrière-plan.'
        Pause-Ui 250
        if (Find-Visible 'Sens') { throw 'Sens affiche le panneau au démarrage.' }
    } else {
        $appHandle = Wait-Ui { Find-Visible 'Sens' } 'Sens ne s''ouvre pas.'
        $uia = [Windows.Automation.AutomationElement]::FromHandle($appHandle)
        Pause-Ui 500
        Invoke-Ui $uia 'Réglages'
        Pause-Ui
        $inputFilter = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'Texte à traduire')
        $inputBox = $uia.FindFirst([Windows.Automation.TreeScope]::Descendants, $inputFilter)
        if (-not $inputBox) { throw 'La saisie manuelle est absente.' }
        $inputBox.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('It is raining cats and dogs.')
        Invoke-Ui $uia 'Traduire'
        Pause-Ui
        Invoke-Ui $uia 'Masquer'
        Pause-Ui
    }

    $sample = 'This sentence is selected in another Windows app.'
    if ($Edge) {
        $edgeExe = Join-Path ${env:ProgramFiles(x86)} 'Microsoft/Edge/Application/msedge.exe'
        $profileRoot = Join-Path $env:TEMP ('sens-test-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $profileRoot | Out-Null
        $html = Join-Path $profileRoot 'source.html'
        Set-Content -LiteralPath $html -Encoding utf8 -Value "<title>Sens Browser Test</title><style>body{padding:42px;font:26px Arial}span{white-space:nowrap}</style><span>$sample</span>"
        $url = [uri]::new($html).AbsoluteUri
        $browser = Start-Process -FilePath $edgeExe -PassThru -ArgumentList @(
            "--user-data-dir=""$profileRoot\profile""", '--no-first-run', '--disable-background-networking',
            '--window-size=750,460', '--window-position=110,150', "--app=$url")
        $testHwnd = Wait-Ui { Find-Visible 'Sens Browser Test' } 'La page Edge de test ne s''ouvre pas.'
        [SensSmoke]::SetForegroundWindow($testHwnd) | Out-Null
        Pause-Ui 500
        $web = [Windows.Automation.AutomationElement]::FromHandle($testHwnd)
        $testFilter = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $sample)
        $snippet = Wait-Ui { $web.FindFirst([Windows.Automation.TreeScope]::Descendants, $testFilter) } 'Texte web de test introuvable.'
        $sampleRect = $snippet.Current.BoundingRectangle
        $position = [Windows.Point]::new($sampleRect.Left + 12, $sampleRect.Top + $sampleRect.Height/2)
        Click-Point $position.X ($sampleRect.Bottom + 20)
        Pause-Ui
    } else {
        $fixture = [Windows.Window]::new()
        $fixture.Topmost = $true
        $fixture.Title = 'Sens test : source'
        $fixture.Width = 610; $fixture.Height = 200; $fixture.Left = 120; $fixture.Top = 165
        $text = [Windows.Controls.TextBox]::new()
        $text.Text = 'This sentence is selected in another Windows app.'
        $text.Margin = [Windows.Thickness]::new(24)
        $text.Padding = [Windows.Thickness]::new(8)
        $text.FontSize = 21
        $fixture.Content = $text
        $fixture.Show()
        $fixture.Activate() | Out-Null
        $text.Focus() | Out-Null
        $testHwnd = [Windows.Interop.WindowInteropHelper]::new($fixture).Handle
        [SensSmoke]::SetForegroundWindow($testHwnd) | Out-Null
        Pause-Ui 300
        $position = $text.PointToScreen([Windows.Point]::new(32,21))
    }
    if (-not $Edge) {
        Click-Point $position.X $position.Y
        $null = Wait-Ui { [SensSmoke]::GetForegroundWindow() -eq $testHwnd } 'La source de test ne reçoit pas le focus.'
        Click-Point $position.X $position.Y -Right
        Pause-Ui 380
        if (Find-Visible 'Traduire avec Sens') { throw 'Bouton affiché sans texte sélectionné.' }
        [SensSmoke]::keybd_event(27, 0, 0, [uintptr]::Zero)
        [SensSmoke]::keybd_event(27, 0, 2, [uintptr]::Zero)
        Pause-Ui
    }
    if ($Edge) {
        $y = $position.Y
        [SensSmoke]::SetCursorPos([int]($sampleRect.Left + 1), $y) | Out-Null
        [SensSmoke]::mouse_event(2, 0, 0, 0, [uintptr]::Zero)
        Pause-Ui 110
        [SensSmoke]::SetCursorPos([int]($sampleRect.Right + 6), $y) | Out-Null
        Pause-Ui 110
        [SensSmoke]::mouse_event(4, 0, 0, 0, [uintptr]::Zero)
        Pause-Ui 200
    } else {
        $fixture.Activate() | Out-Null
        $text.Focus() | Out-Null
        $firstChar = $text.GetRectFromCharacterIndex(0)
        $lastChar = $text.GetRectFromCharacterIndex($sample.Length)
        $first = $text.PointToScreen([Windows.Point]::new($firstChar.Left, $firstChar.Top + $firstChar.Height / 2))
        $last = $text.PointToScreen([Windows.Point]::new($lastChar.Right + 5, $lastChar.Top + $lastChar.Height / 2))
        [SensSmoke]::SetCursorPos($first.X, $first.Y) | Out-Null
        [SensSmoke]::mouse_event(2, 0, 0, 0, [uintptr]::Zero)
        Pause-Ui 110
        [SensSmoke]::SetCursorPos($last.X, $last.Y) | Out-Null
        Pause-Ui 110
        [SensSmoke]::mouse_event(4, 0, 0, 0, [uintptr]::Zero)
        Pause-Ui 300
        if ($text.SelectedText -ne $sample) { throw 'La fenêtre de test ne contient pas la sélection attendue.' }
    }
    Click-Point $position.X $position.Y -Right
    $chip = Wait-Ui { Find-Visible 'Traduire avec Sens' } 'Le bouton n''apparaît pas sur la sélection.'
    $bounds = [SensSmoke+Rect]::new()
    [SensSmoke]::GetWindowRect($chip, [ref]$bounds) | Out-Null
    Click-Point (($bounds.L + $bounds.R) / 2) (($bounds.T + $bounds.B) / 2)
    $appHandle = Wait-Ui { Find-Visible 'Sens' } 'La fenêtre ne s''ouvre pas après le clic sur Traduire.'
    $uia = [Windows.Automation.AutomationElement]::FromHandle($appHandle)
    Pause-Ui 300
    $language = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'Langue de traduction')
    if (-not $uia.FindFirst([Windows.Automation.TreeScope]::Descendants, $language)) {
        throw 'Les réglages de la sélection ne sont pas ouverts.'
    }
    $quit = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'Quitter Sens')
    if ($uia.FindFirst([Windows.Automation.TreeScope]::Descendants, $quit)) { throw 'Le bouton Quitter est encore présent.' }
    $bounds = [SensSmoke+Rect]::new()
    [SensSmoke]::GetWindowRect($appHandle, [ref]$bounds) | Out-Null
    if ($bounds.L -le $position.X -and $bounds.R -ge $position.X -and
        $bounds.T -le $position.Y -and $bounds.B -ge $position.Y) {
        throw 'Le panneau recouvre le point de sélection.'
    }
    [SensSmoke]::PostMessage($appHandle, 0x10, [uintptr]::Zero, [intptr]::Zero) | Out-Null
    if (-not $proc.WaitForExit(2500) -or $proc.ExitCode -ne 0) { throw 'Sens ne se ferme pas proprement.' }
    if ($Background) { 'OK : premier clic droit après lancement en arrière-plan et panneau à côté de la sélection.' }
    else { 'OK : saisie manuelle, clic droit, panneau à côté de la sélection, réglages et fermeture.' }
}
finally {
    if ($fixture) { $fixture.Close() }
    if ($browser -and -not $browser.HasExited) {
        $null = $browser.CloseMainWindow()
        if (-not $browser.WaitForExit(2000)) { $browser.Kill() }
    }
    if ($profileRoot -and (Test-Path -LiteralPath $profileRoot)) {
        $resolved = (Resolve-Path -LiteralPath $profileRoot).Path
        $base = (Resolve-Path -LiteralPath $env:TEMP).Path
        if ((Split-Path -Parent $resolved) -eq $base -and (Split-Path -Leaf $resolved) -match '^sens-test-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    if (-not $proc.HasExited) { $proc.Kill() }
}
