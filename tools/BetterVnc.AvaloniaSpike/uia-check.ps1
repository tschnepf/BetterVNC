# Question 4 of the Avalonia spike: drives the "ui" window through UI Automation only (no mouse, no keyboard, no focus changes).
param([string]$Dll, [string]$Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = Start-Process -FilePath 'C:\Program Files\dotnet\dotnet.exe' -ArgumentList "`"$Dll`" ui --seconds 40" -NoNewWindow -PassThru -RedirectStandardOutput $Out -RedirectStandardError "$Out.err"
try {
    $deadline = (Get-Date).AddSeconds(25)
    while (-not ((Test-Path $Out) -and (Get-Content $Out -Raw) -match 'ready')) { if ((Get-Date) -gt $deadline) { throw 'app never said ready' }; Start-Sleep -Milliseconds 200 }
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $byPid = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $p.Id
    $win = $null
    for ($i = 0; $i -lt 30 -and -not $win; $i++) { $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid); if (-not $win) { Start-Sleep -Milliseconds 200 } }
    if (-not $win) { throw 'window not found in the UIA tree' }
    "window: name='$($win.Current.Name)' class='$($win.Current.ClassName)' framework='$($win.Current.FrameworkId)'"
    function Find($id) {
        $c = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), $id
        $e = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
        if ($e) { Write-Host "found $id : type $($e.Current.ControlType.ProgrammaticName) name '$($e.Current.Name)' patterns [$(($e.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName -replace 'PatternIdentifiers.Pattern','' }) -join ', ')]" } else { Write-Host "MISSING $id" }
        return $e
    }
    function Pattern($e, $pat) { $o = $null; if ($e -and $e.TryGetCurrentPattern($pat, [ref]$o)) { return $o }; return $null }

    $hostEl = Find 'HostBox'
    $v = Pattern $hostEl ([System.Windows.Automation.ValuePattern]::Pattern); if ($v) { $v.SetValue('unraid.local:5901'); 'SetValue on HostBox: done' } else { 'HostBox: no ValuePattern' }

    $btnInfo = Find 'ConnectButton'
    $btn = $btnInfo
    $inv = Pattern $btn ([System.Windows.Automation.InvokePattern]::Pattern); if ($inv) { $inv.Invoke(); 'Invoke on ConnectButton: done' } else { 'ConnectButton: no InvokePattern' }

    $chkInfo = Find 'FullscreenBox'
    $tog = Pattern $chkInfo ([System.Windows.Automation.TogglePattern]::Pattern); if ($tog) { $tog.Toggle(); "Toggle on FullscreenBox: now $($tog.Current.ToggleState)" } else { 'FullscreenBox: no TogglePattern' }

    $comboInfo = Find 'PictureBox'
    $combo = $comboInfo
    $ec = Pattern $combo ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($ec) { $ec.Expand(); Start-Sleep -Milliseconds 400; "ExpandCollapse on PictureBox: $($ec.Current.ExpandCollapseState)" } else { 'PictureBox: no ExpandCollapsePattern' }
    $nameCond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), 'Stream'
    $item = $combo.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
    if (-not $item) { $item = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition $nameCond, $byPid)) }
    $sel = Pattern $item ([System.Windows.Automation.SelectionItemPattern]::Pattern); if ($sel) { $sel.Select(); "Select 'Stream' in PictureBox: done" } else { "PictureBox item 'Stream': $(if ($item) { 'no SelectionItemPattern' } else { 'not found' })" }
    if ($ec) { try { $ec.Collapse() } catch { } }

    $listInfo = Find 'ConnectionList'
    $mac = $listInfo.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), 'Mac mini'))
    $sel2 = Pattern $mac ([System.Windows.Automation.SelectionItemPattern]::Pattern); if ($sel2) { $sel2.Select(); "Select 'Mac mini' in ConnectionList: done" } else { "ConnectionList item 'Mac mini': $(if ($mac) { 'no SelectionItemPattern' } else { 'not found' })" }

    $screenInfo = Find 'RemoteScreen'
    $r = $screenInfo.Current.BoundingRectangle; "RemoteScreen bounds: $r"
    Start-Sleep -Milliseconds 500
}
finally {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    Start-Sleep -Milliseconds 300
    '--- app output'
    Get-Content $Out
}
