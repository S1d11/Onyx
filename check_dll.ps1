Add-Type -AssemblyName System.Reflection
$asm = [System.Reflection.Assembly]::LoadFrom("$pwd\publish\Ollama2.dll")
$res = $asm.GetManifestResourceStream("Ollama2.Web.app.js")
$sr = New-Object System.IO.StreamReader($res)
$content = $sr.ReadToEnd()
$sr.Close()
if ($content.Contains('function bindUI()') -and $content.Contains('function renderMessages(c) {')) {
    Write-Host 'OK: bindUI and renderMessages are at same scope'
} else {
    Write-Host 'SCOPE ISSUE!'
}
