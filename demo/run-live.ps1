# Runs the Host against real Microsoft Graph (Graph:Mode=Live). Run from the repository root in PowerShell.
# The client secret is typed at the prompt and lives only in this PowerShell session.
param(
    [Parameter(Mandatory = $true)][string]$TenantId,
    [Parameter(Mandatory = $true)][string]$ClientId
)

$secure = Read-Host -Prompt 'Client secret' -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try { $env:Graph__ClientSecret = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }

$env:Graph__Mode = 'Live'
$env:Graph__TenantId = $TenantId
$env:Graph__ClientId = $ClientId
$env:Demo__Enabled = 'false'

dotnet run --project src/TeamsCallRecords.Host --no-launch-profile --urls 'http://localhost:60049'
