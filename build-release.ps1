param(
	[string]$EnvFile = (Join-Path $PSScriptRoot '.env')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) {
	throw "Configuration file not found: $EnvFile. Copy .env.example to .env and fill in the public Supabase URL/key."
}

$values = @{}
foreach ($line in Get-Content -LiteralPath $EnvFile) {
	$entry = $line.Trim()
	if (-not $entry -or $entry.StartsWith('#')) {
		continue
	}

	if ($entry.StartsWith('export ')) {
		$entry = $entry.Substring(7).Trim()
	}

	$separator = $entry.IndexOf('=')
	if ($separator -lt 1) {
		throw 'Invalid .env entry. Expected NAME=value.'
	}

	$name = $entry.Substring(0, $separator).Trim()
	$value = $entry.Substring($separator + 1).Trim()
	if (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'"))) {
		$value = $value.Substring(1, $value.Length - 2)
	}
	$values[$name] = $value
}

$url = $values['HLPASSWORD_SUPABASE_URL']
$key = $values['HLPASSWORD_SUPABASE_PUBLISHABLE_KEY']

$urlValue = $null
if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$urlValue) -or $urlValue.Scheme -ne 'https') {
	throw 'HLPASSWORD_SUPABASE_URL must be an HTTPS project URL.'
}
if ([string]::IsNullOrWhiteSpace($key) -or -not $key.StartsWith('sb_publishable_', [StringComparison]::Ordinal)) {
	throw 'HLPASSWORD_SUPABASE_PUBLISHABLE_KEY must be an sb_publishable_ key. Do not put secret/service-role keys in .env.'
}

$env:HLPASSWORD_SUPABASE_URL = $url
$env:HLPASSWORD_SUPABASE_PUBLISHABLE_KEY = $key

dotnet publish (Join-Path $PSScriptRoot 'HLPassword\HLPassword.csproj') -p:PublishProfile=WindowsSingleFile
if ($LASTEXITCODE -ne 0) {
	exit $LASTEXITCODE
}

Write-Output 'Single-file publish completed. Public Supabase configuration is embedded in the EXE; do not distribute the .env file.'
