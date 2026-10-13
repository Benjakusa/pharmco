$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:PATH = 'C:\Users\OPENDESK\.dotnet;' + $env:PATH
$env:DOTNET_ROOT = 'C:\Users\OPENDESK\.dotnet'
$dotnet = 'C:\Users\OPENDESK\.dotnet\dotnet.exe'
$root = 'C:\Users\OPENDESK\Desktop\Pharmco'
Set-Location $root
"=== DOTNET INFO ===" | Out-File "$root\_fix1.log" -Encoding utf8
& $dotnet --list-sdks *>&1 | Out-File "$root\_fix1.log" -Append -Encoding utf8
& $dotnet --info *>&1 | Out-File "$root\_fix1.log" -Append -Encoding utf8
"=== RESTORE CORE ===" | Out-File "$root\_fix1.log" -Append -Encoding utf8
& $dotnet restore "$root\server\Pharmco.Core\Pharmco.Core.csproj" *>&1 | Out-File "$root\_fix1.log" -Append -Encoding utf8
"RESTORE_CORE_EXIT=$LASTEXITCODE" | Out-File "$root\_fix1.log" -Append -Encoding utf8
'DONE_FIX1' | Out-File "$root\_fix1_done.txt" -Encoding utf8
