Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$dir = 'D:\Documents\deepseek-harness\default-workspace\xingbian-ime\scratch\panel'
$out = New-Object System.Text.StringBuilder

function W($s) { [void]$out.AppendLine($s) }

function Lum($c) { return [int](0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B) }

function Analyze($path) {
  W ""
  W ("=" * 100)
  W ("IMAGE: " + (Split-Path $path -Leaf))
  W ("=" * 100)
  $bmp = New-Object System.Drawing.Bitmap($path)
  $w = $bmp.Width; $h = $bmp.Height
  W ("size = ${w} x ${h}")

  # ---- 颜色直方图 -------------------------------------------------------
  $hist = @{}
  for ($y = 0; $y -lt $h; $y++) {
    for ($x = 0; $x -lt $w; $x++) {
      $k = $bmp.GetPixel($x, $y).ToArgb()
      if ($hist.ContainsKey($k)) { $hist[$k]++ } else { $hist[$k] = 1 }
    }
  }
  W ("distinct colors = " + $hist.Count)
  W "top 12 colors (RGB: count):"
  $hist.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 12 | ForEach-Object {
    $c = [System.Drawing.Color]::FromArgb([int]$_.Key)
    W ("   RGB({0,3},{1,3},{2,3}) : {3}" -f $c.R, $c.G, $c.B, $_.Value)
  }

  # ---- 边框检查（1px 外圈颜色 vs 内部颜色）-----------------------------
  $corner = $bmp.GetPixel(0, 0)
  $edgeTop = $bmp.GetPixel([int]($w / 2), 0)
  $inside = $bmp.GetPixel([int]($w / 2), 3)
  W ("border pixels: corner=RGB({0},{1},{2}) topmid=RGB({2},{3},{4})" -f $corner.R, $corner.G, $corner.B, $edgeTop.R, $edgeTop.G, $edgeTop.B)
  W ("  -> 1px border present = " + (($corner.R -ne 255) -or ($corner.G -ne 255) -or ($corner.B -ne 255)))
  # 四边都应是非白
  $nonWhiteSides = 0
  if ($bmp.GetPixel([int]($w/2), 0).R -lt 250) { $nonWhiteSides++ }
  if ($bmp.GetPixel([int]($w/2), $h-1).R -lt 250) { $nonWhiteSides++ }
  if ($bmp.GetPixel(0, [int]($h/2)).R -lt 250) { $nonWhiteSides++ }
  if ($bmp.GetPixel($w-1, [int]($h/2)).R -lt 250) { $nonWhiteSides++ }
  W ("  number of non-white sides (expect 4) = " + $nonWhiteSides)

  # ---- 行/带结构：逐行统计"浅蓝像素"与"非白像素" -----------------------
  W ""
  W "row-band scan (y : nonWhiteCount : lightBlueCount : dominantColor)"
  for ($y = 0; $y -lt $h; $y++) {
    $nw = 0; $lb = 0; $dom = @{}
    for ($x = 0; $x -lt $w; $x++) {
      $c = $bmp.GetPixel($x, $y)
      if ($c.R -lt 250 -or $c.G -lt 250 -or $c.B -lt 250) { $nw++ }
      if ($c.R -ge 190 -and $c.R -le 220 -and $c.G -ge 215 -and $c.G -le 240 -and $c.B -ge 245) { $lb++ }
      $k = $c.ToArgb()
      if ($dom.ContainsKey($k)) { $dom[$k]++ } else { $dom[$k] = 1 }
    }
    $top = $dom.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1
    $tc = [System.Drawing.Color]::FromArgb([int]$top.Key)
    W ("  y={0,3} : nw={1,4} : blue={2,4} : dom=RGB({3},{4},{5}) x{6}" -f $y, $nw, $lb, $tc.R, $tc.G, $tc.B, $top.Value)
  }

  return $bmp
}

function HollowBoxTest($bmp, $x0, $y0, $x1, $y1, $label, $thresh) {
  # 找暗像素的连通列区间（按列投影），用于把一串文字切成"字符/字块"
  $cols = @()
  for ($x = $x0; $x -le $x1; $x++) {
    $cnt = 0
    for ($y = $y0; $y -le $y1; $y++) {
      if ((Lum $bmp.GetPixel($x, $y)) -lt $thresh) { $cnt++ }
    }
    $cols += $cnt
  }
  # 切分为 run
  $runs = @()
  $start = -1
  for ($i = 0; $i -lt $cols.Count; $i++) {
    if ($cols[$i] -gt 0 -and $start -lt 0) { $start = $i }
    elseif ($cols[$i] -eq 0 -and $start -ge 0) { $runs += ,@($start, ($i - 1)); $start = -1 }
  }
  if ($start -ge 0) { $runs += ,@($start, ($cols.Count - 1)) }

  W ""
  W ("--- $label  (x $x0..$x1, y $y0..$y1, ink threshold luminance<$thresh) ---")
  W ("  ink column runs (glyph clusters): " + $runs.Count)

  $idx = 0
  foreach ($r in $runs) {
    $idx++
    $rx0 = $x0 + $r[0]; $rx1 = $x0 + $r[1]
    # 该 run 内的暗像素包围盒（垂直方向）
    $ry0 = 9999; $ry1 = -1; $ink = 0
    for ($y = $y0; $y -le $y1; $y++) {
      for ($x = $rx0; $x -le $rx1; $x++) {
        if ((Lum $bmp.GetPixel($x, $y)) -lt $thresh) {
          $ink++
          if ($y -lt $ry0) { $ry0 = $y }
          if ($y -gt $ry1) { $ry1 = $y }
        }
      }
    }
    $bw = $rx1 - $rx0 + 1; $bh = $ry1 - $ry0 + 1
    if ($bw -le 0 -or $bh -le 0) { continue }

    # 内部墨迹：把包围盒向内收 25%
    $ix0 = $rx0 + [int]($bw * 0.25); $ix1 = $rx1 - [int]($bw * 0.25)
    $iy0 = $ry0 + [int]($bh * 0.25); $iy1 = $ry1 - [int]($bh * 0.25)
    $iInk = 0; $iTot = 0
    for ($y = $iy0; $y -le $iy1; $y++) {
      for ($x = $ix0; $x -le $ix1; $x++) {
        $iTot++
        if ((Lum $bmp.GetPixel($x, $y)) -lt $thresh) { $iInk++ }
      }
    }
    $interiorRatio = if ($iTot -gt 0) { $iInk / $iTot } else { 0 }

    # 边框环测试：只保留包围盒最外 1px 圈时，墨迹占比
    $perim = 0; $perimInk = 0
    for ($y = $ry0; $y -le $ry1; $y++) {
      for ($x = $rx0; $x -le $rx1; $x++) {
        if ($y -eq $ry0 -or $y -eq $ry1 -or $x -eq $rx0 -or $x -eq $rx1) {
          $perim++
          if ((Lum $bmp.GetPixel($x, $y)) -lt $thresh) { $perimInk++ }
        }
      }
    }
    $perimRatio = if ($perim -gt 0) { $perimInk / $perim } else { 0 }

    $verdict = "REAL GLYPH (interior has strokes)"
    if ($bw -le 3) { $verdict = "punctuation/short mark (too small to judge)" }
    elseif ($interiorRatio -lt 0.06) { $verdict = "*** HOLLOW BOX / TOFU (interior EMPTY) ***" }

    W ("  #{0}: x={1}..{2} (w={3})  y={4}..{5} (h={6})  ink={7}  fill={8:P1}  perimeterInk={9:P1}  interiorInk={10:P1}  -> {11}" -f `
        $idx, $rx0, $rx1, $bw, $ry0, $ry1, $bh, $ink, ($ink / ($bw * $bh)), $perimRatio, $interiorRatio, $verdict)
  }
}

function AsciiRender($bmp, $x0, $y0, $x1, $y1, $label, $thresh) {
  W ""
  W ("--- ASCII 1:1 pixel render: $label  (x $x0..$x1, y $y0..$y1) ---")
  W "     ( '#'=dark ink  '+'=mid  '.'=faint  ' '=background ; 每个像素对应一个字符 )"
  $hdr = "     "
  for ($x = $x0; $x -le $x1; $x++) { $hdr += [string]([int]($x % 10)) }
  W $hdr
  for ($y = $y0; $y -le $y1; $y++) {
    $line = "{0,4} " -f $y
    for ($x = $x0; $x -le $x1; $x++) {
      $l = Lum $bmp.GetPixel($x, $y)
      if ($l -lt 110) { $line += '#' }
      elseif ($l -lt 175) { $line += '+' }
      elseif ($l -lt 235) { $line += '.' }
      else { $line += ' ' }
    }
    W $line
  }
}

$b1 = Analyze (Join-Path $dir 'shot1.png')

# shot1: status(1..23) sep(24) rows start 25, rowHeight 31, padY 5, main line height 14
# 主文本 x 起点 = 1 + 10 = 11；宽约 50px（"1. 你妈"）
W ""
W "########## shot1 文字区域取证 ##########"
HollowBoxTest $b1 1 1 ($b1.Width - 2) ($b1.Height - 2) "shot1 全图所有墨迹簇（主文本+状态+备注）" 200
AsciiRender $b1 8 6 70 20 "shot1 状态行文本 '形变  nima'" 160
AsciiRender $b1 8 28 62 42 "shot1 第1行左列 '1. 你妈'（本应为高亮行：深色字 + 浅蓝底）" 110
AsciiRender $b1 68 28 128 42 "shot1 第1行右列灰色备注 '亻尔 女马'（高亮行上）" 200
AsciiRender $b1 8 59 62 73 "shot1 第2行左列 '2. 尼玛'（未选中：白底黑字）" 110
AsciiRender $b1 8 90 62 104 "shot1 第3行左列 '3. 你马'（未选中：白底黑字）" 110
$b1.Dispose()

$b2 = Analyze (Join-Path $dir 'shot2.png')
W ""
W "########## shot2 文字区域取证（selectedIndex=2）##########"
HollowBoxTest $b2 1 1 ($b2.Width - 2) ($b2.Height - 2) "shot2 全图所有墨迹簇" 200
AsciiRender $b2 8 90 62 104 "shot2 第3行左列 '3. 你马'（本应为高亮行）" 110
$b2.Dispose()

$p = Join-Path $dir 'pixel_forensics.txt'
[System.IO.File]::WriteAllText($p, $out.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Output ('written: ' + $p + '  (' + $out.Length + ' chars)')
