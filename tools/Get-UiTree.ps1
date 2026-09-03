param(
    [Parameter(Mandatory = $true)]
    [int] $TargetProcessId
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$targetProcess = Get-Process -Id $TargetProcessId -ErrorAction Stop
$targetProcess.Refresh()
$root = [Windows.Automation.AutomationElement]::FromHandle($targetProcess.MainWindowHandle)
$elements = $root.FindAll(
    [Windows.Automation.TreeScope]::Descendants,
    [Windows.Automation.Condition]::TrueCondition)

foreach ($element in $elements) {
    [PSCustomObject]@{
        Type = $element.Current.ControlType.ProgrammaticName
        Name = $element.Current.Name
        AutomationId = $element.Current.AutomationId
        ClassName = $element.Current.ClassName
        Enabled = $element.Current.IsEnabled
    }
}
