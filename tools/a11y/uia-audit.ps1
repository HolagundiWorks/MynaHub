<#
.SYNOPSIS
  Opens an app, optionally opens and signs in to a company file, visits pages, and records what each page offers to assistive technology.
.DESCRIPTION
  Uses UI Automation. It does not use the keyboard, except to type a file name into the system file dialog, and only while that
  dialog has the keyboard. Writes one JSON file with, for every element: type, name, automation id, focusable, off-screen and size.
  Analyse it with analyse.mjs. The screenshots (-Shot) show how a page lays out at the given window width.
.EXAMPLE
  .\uia-audit.ps1 -Exe "C:\Apps\O-Studio\Adms.Desktop.exe" -Company "D:\Demo\Demo Studio.adms" -User owner -Password $env:AUDIT_PASSWORD -Pages Pulse,Clients -Out studio.json
  .\uia-audit.ps1 -Exe "C:\Apps\O-Metra\AQCCore.exe" -Pages Dashboard,Columns -Out metra.json
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Company = "",
    [string]$User = "owner",
    [string]$Password = "",
    [string[]]$Pages = @(),
    [string]$Out = "uia.json",
    [int]$Width = 1300,
    [string]$Shot = ""
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class A11yWin {
    [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool b);
}
"@
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
function Prop($property, $value) { New-Object System.Windows.Automation.PropertyCondition($property, $value) }
function FindName($root, $name, $type = $null) {
    $c = Prop $AE::NameProperty $name
    if ($type) { $c = New-Object System.Windows.Automation.AndCondition($c, (Prop $AE::ControlTypeProperty $type)) }
    $root.FindFirst($TS::Descendants, $c)
}
function Activate($el) {
    if ($null -eq $el) { return $false }
    foreach ($pattern in @([System.Windows.Automation.InvokePattern]::Pattern, [System.Windows.Automation.SelectionItemPattern]::Pattern, [System.Windows.Automation.ExpandCollapsePattern]::Pattern)) {
        $o = $null
        if ($el.TryGetCurrentPattern($pattern, [ref]$o)) {
            if ($pattern -eq [System.Windows.Automation.InvokePattern]::Pattern) { $o.Invoke() }
            elseif ($pattern -eq [System.Windows.Automation.SelectionItemPattern]::Pattern) { $o.Select() }
            else { $o.Expand() }
            return $true
        }
    }
    return $false
}
function SetText($el, $text) {
    $o = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$o) -and -not $o.Current.IsReadOnly) { $o.SetValue($text); return $true }
    return $false
}

$p = Start-Process $Exe -PassThru
Start-Sleep 9
$p.Refresh()
[A11yWin]::MoveWindow($p.MainWindowHandle, 20, 20, $Width, 820, $true) | Out-Null
$root = $AE::FromHandle($p.MainWindowHandle)

if ($Company) {
    if (-not (Activate (FindName $root "Open an existing company" ([System.Windows.Automation.ControlType]::Button)))) { throw "The open button was not found." }
    # the system file dialog: type into it only while it has the keyboard
    $deadline = (Get-Date).AddSeconds(10); $title = ""
    while ((Get-Date) -lt $deadline) {
        $fg = [A11yWin]::GetForegroundWindow(); $sb = New-Object System.Text.StringBuilder 256; [A11yWin]::GetWindowText($fg, $sb, 256) | Out-Null; $title = $sb.ToString()
        if ($fg -ne $p.MainWindowHandle -and $title -match "Open|Select|Choose") { break }
        Start-Sleep -Milliseconds 300
    }
    if ($title -notmatch "Open|Select|Choose") { throw "The file dialog did not take the keyboard (foreground: '$title'); not typing." }
    [System.Windows.Forms.SendKeys]::SendWait(($Company.Replace("(", "{(}").Replace(")", "{)}")) + "{ENTER}")
    Start-Sleep 4
    $root = $AE::FromHandle($p.MainWindowHandle)
    if (-not (SetText (FindName $root "Username" ([System.Windows.Automation.ControlType]::Edit)) $User)) { throw "Cannot set the user name." }
    if (-not (SetText (FindName $root "Password" ([System.Windows.Automation.ControlType]::Edit)) $Password)) { throw "Cannot set the password." }
    Activate (FindName $root "Sign in" ([System.Windows.Automation.ControlType]::Button)) | Out-Null
    Start-Sleep 5
}

$report = @()
foreach ($page in $Pages) {
    $nav = FindName $root $page
    if ($null -eq $nav) { $report += [pscustomobject]@{ page = $page; count = 0; items = @(); note = "not found" }; continue }
    $scroll = $null
    if ($nav.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$scroll)) { $scroll.ScrollIntoView() }
    Activate $nav | Out-Null
    Start-Sleep 3
    if ($Shot) {
        $rc = $root.Current.BoundingRectangle
        $bmp = New-Object System.Drawing.Bitmap ([int]$rc.Width), ([int]$rc.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen([int]$rc.X, [int]$rc.Y, 0, 0, (New-Object System.Drawing.Size ([int]$rc.Width), ([int]$rc.Height)))
        $bmp.Save((Join-Path (Get-Location) ("$Shot-" + ($page -replace "[^A-Za-z0-9]", "") + ".png")), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $items = @()
    foreach ($e in $root.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        $c = $e.Current
        $r = $c.BoundingRectangle
        $items += [pscustomobject]@{
            type = $c.ControlType.ProgrammaticName.Replace("ControlType.", ""); name = $c.Name; id = $c.AutomationId; focusable = $c.IsKeyboardFocusable; offscreen = $c.IsOffscreen
            w = if ($r.IsEmpty) { 0 } else { [int]$r.Width }; h = if ($r.IsEmpty) { 0 } else { [int]$r.Height }; enabled = $c.IsEnabled
        }
    }
    $report += [pscustomobject]@{ page = $page; count = $items.Count; items = $items }
}
$report | ConvertTo-Json -Depth 5 -Compress | Out-File (Join-Path (Get-Location) $Out) -Encoding utf8
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
"done: " + (($report | ForEach-Object { "$($_.page)=$($_.count)" }) -join ", ")
