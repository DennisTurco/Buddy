# Bruce, the built-in pet: a fawn greyhound in pixel art. One PNG strip of 32x32 frames per state (idle, think, work,
# alert, error, happy, speak, walk) and the claude- variants (violet collar instead of green). Side view, facing right.
# Regenerate after a change: pwsh src/Buddy.App/Pet/Bruce/bruce.ps1 (add -Preview sheet.png for every pose at 4x).
param([string]$Out = $PSScriptRoot, [string]$Preview = "")

Add-Type -AssemblyName System.Drawing
$S = 32
function C($hex) { [Drawing.ColorTranslator]::FromHtml($hex) }
$Ink = C '#2A1A12'; $Body = C '#A9693A'; $Light = C '#C4844C'; $Far = C '#86502A'; $Dark = C '#3E2416'
$Grey = C '#8A7B72'; $White = C '#EDE3D6'; $Nose = C '#120C0A'; $Eye = C '#1E120C'; $Tongue = C '#E07A8A'; $Shine = C '#FFFFFF'

function Rot($pts, $px, $py, $deg, $dx = 0, $dy = 0) {
    $a = $deg * [Math]::PI / 180; $c = [Math]::Cos($a); $s = [Math]::Sin($a)
    $res = New-Object System.Collections.Generic.List[object]
    foreach ($p in $pts) { $x = $p[0] - $px; $y = $p[1] - $py; $res.Add(@(($px + $x * $c - $y * $s + $dx), ($py + $x * $s + $y * $c + $dy))) }
    return ,$res.ToArray()
}
function PF($pts) {
    $arr = New-Object 'Drawing.PointF[]' $pts.Count
    for ($i = 0; $i -lt $pts.Count; $i++) { $arr[$i] = New-Object Drawing.PointF ([float]$pts[$i][0]), ([float]$pts[$i][1]) }
    return ,$arr
}
function Poly($g, $color, $pts) { $g.FillPolygon((New-Object Drawing.SolidBrush $color), (PF $pts)) }
function Line($g, $color, $w, $pts) {
    $pen = New-Object Drawing.Pen $color, $w; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawLines($pen, (PF $pts))
}
function Px($bmp, $color, $x, $y) {
    $ix = [int][Math]::Floor($x); $iy = [int][Math]::Floor($y)
    if ($ix -ge 0 -and $iy -ge 0 -and $ix -lt $S -and $iy -lt $S) { $bmp.SetPixel($ix, $iy, $color) }
}

# One frame. Head: angle (deg, + = down) around the neck base; eye: open|blink|up|focus|wide|sad|joy; mouth: shut|open|tongue;
# ear: fold|up|back; tail: low|wagA|wagB|up|tuck; legs: stand|walk1..walk4; collar colour; bob: dy of the whole dog.
function Frame($o) {
    $bmp = New-Object Drawing.Bitmap $S, $S
    $g = [Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'None'; $g.PixelOffsetMode = 'Half'
    $dy = $o.bob

    # Legs (far ones first, darker), each: shoulder/hip -> knee -> paw.
    $legs = @{
        stand = @{ ff = @(@(19,19),@(19,24),@(19,29)); rf = @(@(6,17),@(8,21),@(5,25),@(6,29)); fn = @(@(21,19),@(21,24),@(21,29)); rn = @(@(8,17),@(10,21),@(7,25),@(8,29)) }
        walk1 = @{ ff = @(@(19,19),@(18,24),@(17,29)); rf = @(@(6,17),@(9,21),@(8,25),@(9,29)); fn = @(@(21,19),@(23,23),@(24,28)); rn = @(@(8,17),@(8,21),@(5,25),@(4,29)) }
        walk2 = @{ ff = @(@(19,19),@(19,24),@(19,29)); rf = @(@(6,17),@(8,21),@(5,25),@(6,29)); fn = @(@(21,19),@(22,24),@(21,29)); rn = @(@(8,17),@(10,21),@(7,25),@(8,29)) }
        walk3 = @{ ff = @(@(19,19),@(21,23),@(22,28)); rf = @(@(6,17),@(6,21),@(3,25),@(2,29)); fn = @(@(21,19),@(20,24),@(19,29)); rn = @(@(8,17),@(11,21),@(10,25),@(11,29)) }
    }
    $l = $legs[$o.legs]; if ($o.legs -eq 'walk4') { $l = $legs['walk2'] }
    foreach ($k in 'ff','rf') { Line $g $Far 1.1 (Rot $l[$k] 0 0 0 0 $dy) }

    # Tail: thin, long, from the rump.
    $tails = @{
        low  = @(@(5,15),@(4,18),@(3,21),@(3,24),@(4,26),@(5,26))
        wagA = @(@(5,15),@(3,17),@(1,20),@(1,23),@(2,24))
        wagB = @(@(5,15),@(3,15),@(1,17),@(0,20))
        up   = @(@(5,14),@(3,12),@(2,9),@(2,7),@(3,6))
        tuck = @(@(5,16),@(5,19),@(6,22),@(8,23))
    }
    Line $g $Body 1.1 (Rot $tails[$o.tail] 0 0 0 0 $dy)
    $tail = $tails[$o.tail]

    # Body: deep chest at the front, a tucked-up waist, the rump at the back.
    Poly $g $Body (Rot @(@(5,14.5),@(8,13),@(12,12.2),@(16,12.4),@(19,12),@(22,13),@(24,15),@(23.5,18),@(22,20),@(19,20),@(16,17.5),@(13,16),@(10,16.5),@(6,17.5),@(4.5,16)) 0 0 0 0 $dy)
    Poly $g $Body (Rot @(@(5,14),@(9,14),@(11,17),@(9,19),@(6,18.5)) 0 0 0 0 $dy)
    Poly $g $Light (Rot @(@(9,13),@(17,12.6),@(19,13.2),@(12,14)) 0 0 0 0 $dy)
    Poly $g $White (Rot @(@(22.5,15),@(24,15.5),@(23.5,18),@(22,19.5)) 0 0 0 0 $dy)

    # Neck and head, turned together around the base of the neck.
    $pv = 22; $pvy = 12 + $dy; $a = $o.head
    $neck = Rot @(@(18.5,12.5),@(20,9),@(22,6.5),@(24.5,7.5),@(24,11),@(23.5,14)) 0 0 0 0 $dy
    $neck = Rot $neck $pv $pvy ($a * 0.5)
    Poly $g $Body $neck
    $head = @(@(21.5,6),@(23,4.8),@(25,4.5),@(27,5.3),@(29,6.6),@(31,7.8),@(31,8.8),@(29.5,9.3),@(26.5,9.2),@(24,8.8),@(22.5,8.2))
    Poly $g $Body (Rot (Rot $head 0 0 0 0 $dy) $pv $pvy $a)
    Poly $g $Grey (Rot (Rot @(@(27.5,7.4),@(30.5,7.9),@(31,8.6),@(30,9.2),@(28,9.2),@(27.2,8.6)) 0 0 0 0 $dy) $pv $pvy $a)
    if ($o.mouth -ne 'shut') {
        Poly $g $Grey (Rot (Rot @(@(25,9),@(29,10),@(29,11),@(26,10.5)) 0 0 0 0 $dy) $pv $pvy $a)
    }

    # The ear: folded back (rose), pricked up when alert, flat when sad.
    $ears = @{ fold = @(@(23,5),@(21,4.6),@(19.5,5.3),@(20.5,6.3),@(22.5,6.4)); up = @(@(22,5),@(22,2),@(20.5,2.5),@(20.5,5)); back = @(@(22,5.5),@(19.5,5.5),@(19,6.5),@(21.5,6.5)) }
    Poly $g $Dark (Rot (Rot $ears[$o.ear] 0 0 0 0 $dy) $pv $pvy $a)

    # Collar: the chat mode's colour.
    Poly $g $o.collar (Rot (Rot @(@(20.5,9.6),@(22,8),@(24.3,9),@(23.6,10.8)) 0 0 0 0 $dy) $pv $pvy ($a * 0.5))

    # Near legs on top.
    foreach ($k in 'fn','rn') { Line $g $Body 1.6 (Rot $l[$k] 0 0 0 0 $dy) }
    $g.Dispose()

    # Pixel details that move with the head.
    function HP($x, $y) { $p = Rot (Rot @(,@($x, $y)) 0 0 0 0 $dy) $pv $pvy $a; return ,$p[0] }
    $e = HP 25 6.3
    switch ($o.eye) {
        'blink' { Px $bmp $Dark $e[0] $e[1]; Px $bmp $Dark ($e[0]-1) $e[1] }
        'up'    { Px $bmp $Eye $e[0] ($e[1]-0.6); Px $bmp $Dark ($e[0]-1) $e[1] }
        'focus' { Px $bmp $Eye $e[0] $e[1]; Px $bmp $Dark ($e[0]-1) ($e[1]-0.6) }
        'wide'  { Px $bmp $Eye $e[0] $e[1]; Px $bmp $Shine $e[0] ($e[1]-1); Px $bmp $Dark ($e[0]-1) $e[1] }
        'sad'   { Px $bmp $Eye $e[0] $e[1]; Px $bmp $Dark ($e[0]-1) ($e[1]+0.6) }
        'joy'   { Px $bmp $Dark $e[0] $e[1]; Px $bmp $Dark ($e[0]-1) ($e[1]+0.6); Px $bmp $Dark ($e[0]+1) ($e[1]+0.6) }
        default { Px $bmp $Eye $e[0] $e[1]; Px $bmp $Dark ($e[0]-1) $e[1]; Px $bmp $Dark ($e[0]+1) ($e[1]-0.7) }
    }
    $n = HP 31 8.2; Px $bmp $Nose $n[0] $n[1]
    if ($o.mouth -eq 'tongue') { $t = HP 27.5 10.6; Px $bmp $Tongue $t[0] $t[1]; Px $bmp $Tongue ($t[0]-1) $t[1] }
    foreach ($k in 'fn','ff','rn','rf') { $p = $l[$k][-1]; Px $bmp $White $p[0] ($p[1] + $dy) }

    # His tail ends white: the last points of the tail and the pixel between them, 2-3 pixels whatever the pose.
    $t1 = $tail[-1]; $t2 = $tail[-2]; $t3 = $tail[-3]
    foreach ($p in @($t1, $t2, @((($t1[0] + $t2[0]) / 2), (($t1[1] + $t2[1]) / 2)), @((($t2[0] * 2 + $t3[0]) / 3), (($t2[1] * 2 + $t3[1]) / 3)))) {
        Px $bmp $White $p[0] ($p[1] + $dy)
    }

    # Outline: every empty pixel touching the dog becomes ink.
    $mask = New-Object 'bool[,]' $S, $S
    for ($y = 0; $y -lt $S; $y++) { for ($x = 0; $x -lt $S; $x++) { $mask[$x, $y] = $bmp.GetPixel($x, $y).A -gt 0 } }
    for ($y = 0; $y -lt $S; $y++) { for ($x = 0; $x -lt $S; $x++) {
        if ($mask[$x, $y]) { continue }
        foreach ($d in @(@(1,0),@(-1,0),@(0,1),@(0,-1))) {
            $nx = $x + $d[0]; $ny = $y + $d[1]
            if ($nx -ge 0 -and $ny -ge 0 -and $nx -lt $S -and $ny -lt $S -and $mask[$nx, $ny]) { $bmp.SetPixel($x, $y, $Ink); break }
        }
    } }
    return $bmp
}

# Lying down, curled up: the body an oval on the ground, the haunch round at the back, the head resting on the front
# paws stretched forward, eyes closed, the tail wrapped along the bottom with its white tip. $breath lifts the back.
function RestFrame($collar, $breath) {
    $bmp = New-Object Drawing.Bitmap $S, $S
    $g = [Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'None'; $g.PixelOffsetMode = 'Half'
    $b = New-Object Drawing.SolidBrush $Body

    # Front paws stretched forward under the chin (far one first, darker).
    Line $g $Far 1.4 @(@(19,27.5),@(27,27.5))
    # Body, haunch, a lighter back.
    $g.FillEllipse($b, 4, (20 - $breath), 21, (9.5 + $breath))
    $g.FillEllipse($b, 3.5, (18.5 - $breath), 10, 10)
    Poly $g $Light @(@(8,(20.5 - $breath)),@(17,(20.5 - $breath)),@(19,(21.5 - $breath)),@(10,(21.5 - $breath)))
    # Neck and head, low, the chin on the paws.
    Poly $g $Body @(@(18,22.5),@(20.5,18.5),@(22.5,17.6),@(24,20.5),@(23,25),@(19,26))
    Poly $g $Body @(@(20.8,18.8),@(22.3,17),@(24.3,16.6),@(26.3,17.4),@(28.3,18.8),@(31,20.3),@(31.2,21.3),@(29.8,22),@(26.5,22.1),@(23.5,21.8),@(21.3,21))
    Poly $g $Grey @(@(27.5,19.7),@(30.6,20.3),@(31.1,21.2),@(29.8,21.9),@(28,21.9),@(27.3,21.1))
    Poly $g $Dark @(@(22.3,17.7),@(20.3,17.3),@(18.8,18.1),@(19.8,19.1),@(21.8,19.2))
    Poly $g $collar @(@(19.6,20.6),@(21,19),@(23,19.8),@(22.4,21.8))
    Poly $g $White @(@(21,24),@(23,24.5),@(22.5,26),@(20.5,26))
    # Near front paw, then the tail wrapped along the bottom towards the front.
    Line $g $Body 1.6 @(@(20,28.3),@(28,28.3))
    Line $g $Body 1.2 @(@(4,24),@(4,27),@(7,29.3),@(12,29.6),@(17,29.3))
    $g.Dispose()

    # Eye closed (a dark dash), nose, white paws and tail tip.
    Px $bmp $Dark 24.5 19.2; Px $bmp $Dark 25.5 19.2
    Px $bmp $Nose 31 20.6
    Px $bmp $White 28.2 28.3; Px $bmp $White 27.2 27.5
    foreach ($p in @(@(17,29.3),@(16,29.4),@(15,29.5))) { Px $bmp $White $p[0] $p[1] }

    $mask = New-Object 'bool[,]' $S, $S
    for ($y = 0; $y -lt $S; $y++) { for ($x = 0; $x -lt $S; $x++) { $mask[$x, $y] = $bmp.GetPixel($x, $y).A -gt 0 } }
    for ($y = 0; $y -lt $S; $y++) { for ($x = 0; $x -lt $S; $x++) {
        if ($mask[$x, $y]) { continue }
        foreach ($d in @(@(1,0),@(-1,0),@(0,1),@(0,-1))) {
            $nx = $x + $d[0]; $ny = $y + $d[1]
            if ($nx -ge 0 -and $ny -ge 0 -and $nx -lt $S -and $ny -lt $S -and $mask[$nx, $ny]) { $bmp.SetPixel($x, $y, $Ink); break }
        }
    } }
    return $bmp
}

function Strip($name, $frames) {
    $strip = New-Object Drawing.Bitmap ($S * $frames.Count), $S
    $g = [Drawing.Graphics]::FromImage($strip)
    for ($i = 0; $i -lt $frames.Count; $i++) { $g.DrawImage($frames[$i], $i * $S, 0) }
    $g.Dispose(); $strip.Save((Join-Path $Out $name), [Drawing.Imaging.ImageFormat]::Png)
    return $strip
}

New-Item -ItemType Directory -Force $Out | Out-Null
$strips = [ordered]@{}
foreach ($mode in @(@{ prefix = ''; collar = (C '#7FD1AE') }, @{ prefix = 'claude-'; collar = (C '#B99BF5') })) {
    function F($h) { $d = @{ head = 0; eye = 'open'; mouth = 'shut'; ear = 'fold'; tail = 'low'; legs = 'stand'; bob = 0; collar = $mode.collar }; foreach ($k in $h.Keys) { $d[$k] = $h[$k] }; Frame $d }
    $p = $mode.prefix
    $idle = F @{}; $wag = F @{ tail = 'wagA' }
    $strips["${p}idle"]  = Strip "${p}idle.png"  (@($idle) * 5 + @((F @{ tail = 'wagA' })) + @($idle) * 4 + @((F @{ eye = 'blink' })))
    $strips["${p}think"] = Strip "${p}think.png" @((F @{ head = -12; eye = 'up' }), (F @{ head = -12; eye = 'up'; tail = 'wagA' }))
    $strips["${p}work"]  = Strip "${p}work.png"  @((F @{ head = 22; eye = 'focus' }), (F @{ head = 26; eye = 'focus' }), (F @{ head = 22; eye = 'blink' }))
    $strips["${p}alert"] = Strip "${p}alert.png" @((F @{ head = -8; eye = 'wide'; ear = 'up'; tail = 'up' }))
    $strips["${p}error"] = Strip "${p}error.png" @((F @{ head = 18; eye = 'sad'; ear = 'back'; tail = 'tuck' }))
    $strips["${p}happy"] = Strip "${p}happy.png" @((F @{ head = -6; eye = 'joy'; mouth = 'tongue'; tail = 'wagA'; bob = -1 }), (F @{ head = -6; eye = 'joy'; mouth = 'tongue'; tail = 'wagB' }))
    $strips["${p}speak"] = Strip "${p}speak.png" @((F @{ head = -4 }), (F @{ head = -4; mouth = 'open' }), (F @{ head = -4; mouth = 'open'; tail = 'wagA' }))
    # Resting, curled up: a slow breath (four frames in, four out).
    $restIn = RestFrame $mode.collar 0; $restOut = RestFrame $mode.collar 1
    $strips["${p}rest"]  = Strip "${p}rest.png"  (@($restIn) * 4 + @($restOut) * 4)
    $strips["${p}walk"]  = Strip "${p}walk.png"  @((F @{ legs = 'walk1'; tail = 'wagA' }), (F @{ legs = 'walk2'; bob = -1 }), (F @{ legs = 'walk3'; tail = 'wagA' }), (F @{ legs = 'walk4'; bob = -1 }))
}

if ($Preview) {
    # Every strip at 4x on a light and a dark band, one row per strip.
    $z = 4; $rows = $strips.Count; $w = 12 * $S * $z + 160
    $sheet = New-Object Drawing.Bitmap $w, ($rows * ($S * $z + 8))
    $g = [Drawing.Graphics]::FromImage($sheet); $g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'
    $g.Clear((C '#F4F2EE')); $font = New-Object Drawing.Font 'Segoe UI', 11
    $r = 0
    foreach ($k in $strips.Keys) {
        $y = $r * ($S * $z + 8)
        if ($k -like 'claude-*') { $g.FillRectangle((New-Object Drawing.SolidBrush (C '#2B2738')), 0, $y, $w, $S * $z + 8) }
        $g.DrawString($k, $font, (New-Object Drawing.SolidBrush (C '#888888')), 4, $y + 4)
        $strip = $strips[$k]; $g.DrawImage($strip, (New-Object Drawing.Rectangle 150, ($y + 4), ($strip.Width * $z), ($S * $z)))
        $r++
    }
    $sheet.Save($Preview, [Drawing.Imaging.ImageFormat]::Png)
}
"ok: $($strips.Count) strips in $Out"
