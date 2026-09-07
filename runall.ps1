ls .\reference\tiny-c\SamplePrograms\*.tc | foreach {
    Write-Host "Running '$($_.Name)'..." -ForegroundColor Cyan
    dotnet run --project src\TinyC.Cli\TinyC.Cli.fsproj -- $_
}
