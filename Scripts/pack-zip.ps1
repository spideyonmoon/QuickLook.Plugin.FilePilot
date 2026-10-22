# Packs the Release build into QuickLook.Plugin.FilePilot.qlplugin (a renamed zip).

$root = Resolve-Path "$PSScriptRoot\.."
$out = Join-Path $root "QuickLook.Plugin.FilePilot.qlplugin"
$zip = [IO.Path]::ChangeExtension($out, ".zip")

Remove-Item $out, $zip -ErrorAction SilentlyContinue

$files = Get-ChildItem -Path (Join-Path $root "bin\Release") -Exclude *.pdb, *.xml
Compress-Archive $files $zip
Move-Item $zip $out
