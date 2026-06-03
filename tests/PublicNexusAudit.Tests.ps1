$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$trainerPath = Join-Path $repoRoot 'src\RomStar.BepInEx\Trainer\TrainerWindow.cs'
$pluginPath = Join-Path $repoRoot 'src\RomStar.BepInEx\Plugin.cs'
$readmePath = Join-Path $repoRoot 'README.md'
$distDll = Join-Path $repoRoot 'dist\RomStar.BepInEx.dll'

if (-not (Test-Path $trainerPath)) {
    throw "TrainerWindow.cs not found: $trainerPath"
}

$trainer = Get-Content -Raw -Path $trainerPath
$plugin = Get-Content -Raw -Path $pluginPath
$readme = Get-Content -Raw -Path $readmePath

function Assert-Contains([string]$Text, [string]$Needle, [string]$Message) {
    if (-not $Text.Contains($Needle)) {
        throw $Message
    }
}

function Assert-NotContains([string]$Text, [string]$Needle, [string]$Message) {
    if ($Text.Contains($Needle)) {
        throw $Message
    }
}

$headerMatch = [regex]::Match($trainer, 'private static void DrawHeader\(\)\s*\{(?<body>.*?)\n    \}', 'Singleline')
if (-not $headerMatch.Success) {
    throw 'DrawHeader was not found.'
}

$header = $headerMatch.Groups['body'].Value
Assert-Contains $header '"ROMSTAR"' 'Nexus announcement must show ROMSTAR.'
Assert-NotContains $header 'DisplayVersion' 'Nexus announcement must not show a version.'
Assert-NotContains $header 'License' 'Nexus announcement must not show license text.'
Assert-NotContains $header 'status' 'Nexus announcement must not show status text.'
Assert-NotContains $header 'This Nexus build' 'Nexus announcement must not show extra announcement copy.'

$catalogDisplayMatch = [regex]::Match($trainer, 'private static string GetCatalogDisplayName\(CatalogItem item\)\s*\{(?<body>.*?)\n    \}', 'Singleline')
if (-not $catalogDisplayMatch.Success) {
    throw 'GetCatalogDisplayName was not found.'
}

$catalogDisplay = $catalogDisplayMatch.Groups['body'].Value
Assert-NotContains $catalogDisplay 'item.ChineseName' 'Nexus catalog dropdown labels must not fall back to Chinese names.'
Assert-Contains $catalogDisplay 'CleanInternalId(item.Id)' 'Nexus catalog dropdown labels should fall back to cleaned IDs when English names are missing.'

$itemFilterMatch = [regex]::Match($trainer, 'private static void ApplyItemFilter\(\)\s*\{(?<body>.*?)\n    \}', 'Singleline')
$generatorFilterMatch = [regex]::Match($trainer, 'private static void ApplyGeneratorFilter\(\)\s*\{(?<body>.*?)\n    \}', 'Singleline')
if (-not $itemFilterMatch.Success -or -not $generatorFilterMatch.Success) {
    throw 'Catalog filter methods were not found.'
}

Assert-NotContains $itemFilterMatch.Groups['body'].Value 'ChineseName.Contains' 'Nexus item search must not search Chinese names.'
Assert-NotContains $generatorFilterMatch.Groups['body'].Value 'ChineseName.Contains' 'Nexus generator search must not search Chinese names.'

$englishOnlyDisplayMethods = @(
    'ConstructionDisplayName',
    'EntityTranslatedDisplayName',
    'BossDisplayName',
    'RaidDisplayName',
    'AuraDisplayName',
    'SafeItemName',
    'JobDisplayName'
)

foreach ($methodName in $englishOnlyDisplayMethods) {
    $methodMatch = [regex]::Match($trainer, "private static string $methodName\(.*?\)\s*\{(?<body>.*?)\n    \}", 'Singleline')
    if (-not $methodMatch.Success) {
        throw "$methodName was not found."
    }

    Assert-NotContains $methodMatch.Groups['body'].Value 'TranslateGameText' "Nexus dropdown display method $methodName must not depend on the current game translation language."
    Assert-NotContains $methodMatch.Groups['body'].Value 'GetTranslation' "Nexus dropdown display method $methodName must not depend on the current game translation language."
    Assert-NotContains $methodMatch.Groups['body'].Value 'GetName' "Nexus dropdown display method $methodName must not depend on localized game names."
}

Assert-NotContains $trainer 'DrawLanguageSelector' 'Nexus source must not include a language selector.'
Assert-NotContains $trainer 'RomesteadChineseInput' 'Nexus source must not include the Chinese input bridge.'
Assert-NotContains $trainer 'CN Input' 'Nexus source must not include Chinese input UI.'
Assert-NotContains $plugin 'LicenseManager' 'Nexus build must not reference the normal-edition key system.'
Assert-Contains $readme 'dist/RomStar.BepInEx.dll' 'Nexus README must tell players where the prebuilt DLL is stored.'
Assert-Contains $readme 'Romestead\BepInEx\plugins\RomStar\' 'Nexus README must tell players where to install the DLL.'
Assert-Contains $readme 'Copy `RomStar.BepInEx.dll`' 'Nexus README must tell players to copy the DLL into the plugin folder.'

if (-not (Test-Path $distDll)) {
    throw "Nexus GitHub DLL is missing: $distDll"
}

Write-Host 'Public Nexus audit passed.'
