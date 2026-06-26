Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 32,32
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::FromArgb(0x0B,0x0D,0x0F))
$br = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0xD9,0x77,0x57))
$g.FillEllipse($br, 3, 3, 26, 26)
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 2
$g.DrawEllipse($pen, 9, 9, 14, 14)
$h = $bmp.GetHicon()
$ico = [System.Drawing.Icon]::FromHandle($h)
$out = 'C:\Users\kotas\Ollama-2.0\src\Ollama2\app.ico'
$fs = [System.IO.File]::Create($out)
$ico.Save($fs)
$fs.Close()
Write-Output "ico written to $out"
