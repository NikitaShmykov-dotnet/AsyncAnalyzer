param([string]$Dotnet = 'dotnet')

$ErrorActionPreference = 'Stop'
$version = '0.1.0-smoke.' + [Guid]::NewGuid().ToString('N')

function Invoke-Dotnet {
    $output = & $Dotnet @args
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        throw "dotnet exited with $exitCode"
    }
    return ($output -join [Environment]::NewLine)
}

Push-Location (Join-Path $PSScriptRoot '..')
try {
    $null = Invoke-Dotnet pack src/AsyncAnalyzer -c Release -o artifacts/feed "-p:PackageVersion=$version"

    foreach ($variant in 'Before', 'Fixed') {
        $build = Invoke-Dotnet build demo/Consumer.csproj -c Release '-t:Rebuild' `
            "-p:DemoVariant=$variant" "-p:AsyncAnalyzerVersion=$version"
        if ($build -match '\b(?:CS8032|CS9057|AD0001)\b') {
            throw "Analyzer failed to load or run for $variant"
        }

        $ids = @([regex]::Matches($build, '\bAG00[1-3]\b').Value | Sort-Object -Unique)
        $expected = if ($variant -eq 'Before') { 'AG001,AG002,AG003' } else { '' }
        if (($ids -join ',') -ne $expected) {
            throw "Unexpected diagnostics for $variant. Expected '$expected', got '$($ids -join ',')'."
        }
    }

    $result = Invoke-Dotnet exec demo/bin/Release/net10.0/Consumer.dll
    if ($result -ne 'Processed: 42') {
        throw "Unexpected demo output: $result"
    }
}
finally {
    Pop-Location
}
