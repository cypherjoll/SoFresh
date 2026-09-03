param(
    [Parameter(Mandatory = $true)]
    [int] $TargetProcessId,

    [Parameter(Mandatory = $true)]
    [string] $AutomationName
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$targetProcess = Get-Process -Id $TargetProcessId -ErrorAction Stop
$targetProcess.Refresh()
if ($targetProcess.MainWindowHandle -eq [IntPtr]::Zero) {
    throw "Process $TargetProcessId does not expose a main window."
}

$window = [Windows.Automation.AutomationElement]::FromHandle($targetProcess.MainWindowHandle)
$buttonCondition = New-Object Windows.Automation.AndCondition(
    (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::Button)),
    (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty,
        $AutomationName)))

$button = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $buttonCondition)
if ($null -eq $button) {
    throw "UI Automation button '$AutomationName' was not found."
}

$invokePattern = $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
$invokePattern.Invoke()
Write-Output "Invoked: $AutomationName"
