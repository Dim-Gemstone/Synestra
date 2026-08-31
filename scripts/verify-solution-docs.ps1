$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot "Synestra.slnx"
$docsRoot = Join-Path $repositoryRoot "docs"

[xml]$solution = Get-Content $solutionPath -Raw

$solutionDocNodes = @($solution.SelectNodes("//File[starts-with(@Path, 'docs/')]") )
$solutionDocs = @(
    $solutionDocNodes |
        ForEach-Object { $_.Path.Replace('\', '/') } |
        Sort-Object -Unique
)

$fileSystemDocs = @(
    Get-ChildItem $docsRoot -Recurse -File -Filter "*.md" |
        ForEach-Object {
            [System.IO.Path]::GetRelativePath($repositoryRoot, $_.FullName).Replace('\', '/')
        } |
        Sort-Object -Unique
)

$missingFromSolution = @($fileSystemDocs | Where-Object { $_ -notin $solutionDocs })
$missingFromFileSystem = @($solutionDocs | Where-Object { $_ -notin $fileSystemDocs })
$incorrectFolders = @(
    $solutionDocNodes | ForEach-Object {
        $relativePath = $_.Path.Replace('\', '/')
        $directory = [System.IO.Path]::GetDirectoryName($relativePath).Replace('\', '/')
        $expectedFolder = "/$directory/"
        $actualFolder = $_.ParentNode.GetAttribute("Name")

        if ($actualFolder -ne $expectedFolder) {
            "$relativePath (expected $expectedFolder, found $actualFolder)"
        }
    }
)

if (
    $missingFromSolution.Count -eq 0 -and
    $missingFromFileSystem.Count -eq 0 -and
    $incorrectFolders.Count -eq 0
) {
    Write-Host "Solution documentation items match docs/**/*.md."
    exit 0
}

if ($missingFromSolution.Count -gt 0) {
    Write-Error (
        "Documentation files missing from Synestra.slnx:`n- " +
        ($missingFromSolution -join "`n- ")
    )
}

if ($missingFromFileSystem.Count -gt 0) {
    Write-Error (
        "Synestra.slnx references missing documentation files:`n- " +
        ($missingFromFileSystem -join "`n- ")
    )
}

if ($incorrectFolders.Count -gt 0) {
    Write-Error (
        "Documentation files placed in incorrect solution folders:`n- " +
        ($incorrectFolders -join "`n- ")
    )
}

exit 1
