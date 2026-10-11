$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$dotnet = 'C:\Users\OPENDESK\.dotnet\dotnet.exe'
$root = 'C:\Users\OPENDESK\Desktop\Pharmco'
Set-Location $root
$log = 'C:\Users\OPENDESK\Desktop\Pharmco\_build.log'
"=== SLN ADD ===" | Out-File $log -Encoding utf8
& $dotnet sln "$root\Pharmco.sln`" add "$root\server\Pharmco.Core\Pharmco.Core.csproj" "$root\server\Pharmco.Api\Pharmco.Api.csproj" "$root\server\Pharmco.Cli\Pharmco.Cli.csproj" "$root\server\Pharmco.Tests\Pharmco.Tests.csproj" "$root\client\Pharmco.Client\Pharmco.Client.csproj" "$root\client\Pharmco.Client.Tests\Pharmco.Client.Tests.csproj" *>&1 | Out-File $log -Append -Encoding utf8
"=== BUILD SERVER (Core+Api+Cli+Tests) ===" | Out-File $log -Append -Encoding utf8
& $dotnet build "$root\server\Pharmco.Tests\Pharmco.Tests.csproj" -v minimal *>&1 | Out-File $log -Append -Encoding utf8
"=== SERVER_BUILD_EXIT=$LASTEXITCODE ===" | Out-File $log -Append -Encoding utf8
"=== BUILD CLIENT ===" | Out-File $log -Append -Encoding utf8
& $dotnet build "$root\client\Pharmco.Client\Pharmco.Client.csproj" -v minimal *>&1 | Out-File $log -Append -Encoding utf8
"=== CLIENT_BUILD_EXIT=$LASTEXITCODE ===" | Out-File $log -Append -Encoding utf8
"=== BUILD CLIENT TESTS ===" | Out-File $log -Append -Encoding utf8
& $dotnet build "$root\client\Pharmco.Client.Tests\Pharmco.Client.Tests.csproj" -v minimal *>&1 | Out-File $log -Append -Encoding utf8
"=== CLIENT_TESTS_BUILD_EXIT=$LASTEXITCODE ===" | Out-File $log -Append -Encoding utf8
'DONE_ALL' | Out-File 'C:\Users\OPENDESK\Desktop\Pharmco\_build_done.txt' -Encoding utf8