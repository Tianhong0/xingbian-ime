Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$dir = 'D:\Documents\deepseek-harness\default-workspace\xingbian-ime\scratch\panel'
$out = New-Object System.Text.StringBuilder
function W($s) { [void]$out.AppendLine($s) }
function Lum($c) { return [int](0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B) }

function Render($bmp, $x0, $y0, $x1, $y1, $label, $thresh) {
  W ""
  W ("### $label    x=$x0..$x1  y=$y0..$y1   (ink = luminance < $thresh)")
  $hdr = '     '
  for ($x = $x0; $x -le $x1; $x++) { $hdr += [string]($x % 10) }
  W $hdr
  for ($y = $y0; $y -le $y1; $y++) {
    $line = '{0,4} ' -f $y
    for ($x = $x0; $x -le $x1; $x++) {
      $l = Lum $bmp.GetPixel($x, $y)
      if ($l -lt ($thresh * 0.55)) { $line += '#' }
      elseif ($l -lt $thresh) { $line += '+' }
      elseif ($l -lt 245) { $line += '.' }
      else { $line += ' ' }
    }
    W $line
  }
}

$b1 = New-Object System.Drawing.Bitmap((Join-Path $dir 'shot1.png'))
$b2 = New-Object System.Drawing.Bitmap((Join-Path $dir 'shot2.png'))

W "############################################################################################"
W "#  shot1.png —— selectedIndex = 0；几何：边框y0/y120，状态行y1..28，分隔线y29，候选行高30"
W "#     第1行 y30..59（高亮 RGB 204,228,255）  第2行 y60..89  第3行 y90..119"
W "############################################################################################"
Render $b1 8 5 76 25 "shot1 状态行文字 = '形变  nima'（状态文本色 RGB 60,74,90）" 170
Render $b1 8 34 60 56 "shot1 第1行左列 = '1. 你妈'（高亮行，字色 RGB 0,51,102）" 150
Render $b1 62 34 124 56 "shot1 第1行右列 = 灰色备注 '亻尔 女马'（灰 RGB 140,140,140，高亮行上用 90,110,130）" 190
Render $b1 8 64 60 86 "shot1 第2行左列 = '2. 尼玛'（未选中，黑字）" 150
Render $b1 62 64 124 86 "shot1 第2行右列 = 灰色备注 '尸匕 马'（未选中）" 190
Render $b1 8 94 60 116 "shot1 第3行左列 = '3. 你马'（未选中，黑字）" 150
Render $b1 62 94 124 116 "shot1 第3行右列 = 灰色备注 '亻尔 马'（未选中）" 190

W ""
W "############################################################################################"
W "#  shot2.png —— selectedIndex = 2；高亮应落在第3行 y90..119"
W "############################################################################################"
Render $b2 8 94 60 116 "shot2 第3行左列 = '3. 你马'（高亮行，字色应为 RGB 0,51,102）" 150
Render $b2 62 94 124 116 "shot2 第3行右列 = 备注'亻尔 马'（高亮行上用 90,110,130）" 190
Render $b2 8 34 60 56 "shot2 第1行左列 = '1. 你妈'（未选中，黑字）" 150

# ---- 逐行取"该行内纯色像素计数"，用于精确判定高亮行归属 ----
function RowColorCensus($bmp, $name) {
  W ""
  W "### $name —— 每行内关键颜色的像素数（用于精确判定高亮/字色归属）"
  $bmpW = $bmp.Width
  W "  y   :  浅蓝204,228,255 : 黑26,26,26 : 选中字0,51,102 : 灰140 : 选中灰90,110,130 : 状态字60,74,90"
  for ($y = 0; $y -lt $bmp.Height; $y++) {
    $blue = 0; $black = 0; $seltext = 0; $gray = 0; $selgray = 0; $status = 0
    for ($x = 0; $x -lt $bmpW; $x++) {
      $c = $bmp.GetPixel($x, $y)
      if ($c.R -eq 204 -and $c.G -eq 228 -and $c.B -eq 255) { $blue++ }
      elseif ($c.R -eq 26 -and $c.G -eq 26 -and $c.B -eq 26) { $black++ }
      elseif ($c.R -eq 0 -and $c.G -eq 51 -and $c.B -eq 102) { $seltext++ }
      elseif ($c.R -eq 140 -and $c.G -eq 140 -and $c.B -eq 140) { $gray++ }
      elseif ($c.R -eq 90 -and $c.G -eq 110 -and $c.B -eq 130) { $selgray++ }
      elseif ($c.R -eq 60 -and $c.G -eq 74 -and $c.B -eq 90) { $status++ }
    }
    if ($blue + $black + $seltext + $gray + $selgray + $status -gt 0) {
      W ("  {0,3} :  {1,4} : {2,4} : {3,4} : {4,4} : {5,4} : {6,4}" -f $y, $blue, $black, $seltext, $gray, $selgray, $status)
    }
  }
}
RowColorCensus $b1 'shot1'
RowColorCensus $b2 'shot2'

$b1.Dispose(); $b2.Dispose()
$p = Join-Path $dir 'glyph_forensics.txt'
[System.IO.File]::WriteAllText($p, $out.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Output ('written: ' + $p + '  (' + $out.Length + ' chars)')
