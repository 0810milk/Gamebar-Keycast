# KeyDisplay 浮层派生色算法测试窗口（0.8.2）
# 展示：基准面板色 → 浮层背景派生色（深底提亮 1.2x / 浅底压暗 0.8x）→ 边框派生色（0.7x / 1.35x）
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Shift-Lum([System.Drawing.Color]$c, [double]$f) {
    [System.Drawing.Color]::FromArgb($c.A,
        [Math]::Min(255, [int]($c.R * $f)),
        [Math]::Min(255, [int]($c.G * $f)),
        [Math]::Min(255, [int]($c.B * $f)))
}
function Get-Luma([System.Drawing.Color]$c) {
    (0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B) / 255.0
}

$f = New-Object System.Windows.Forms.Form
$f.Text = "KeyDisplay 浮层派生色算法测试"
$f.Size = New-Object System.Drawing.Size(520, 300)
$f.StartPosition = "CenterScreen"
$f.FormBorderStyle = "FixedDialog"
$f.MaximizeBox = $false

$rows = @(
    @{ name = "深色面板 (dark)";   base = [System.Drawing.Color]::FromArgb(255, 31, 31, 31) },
    @{ name = "浅色面板 (light)";  base = [System.Drawing.Color]::FromArgb(255, 245, 245, 245) },
    @{ name = "彩色面板 (custom)"; base = [System.Drawing.Color]::FromArgb(255, 40, 70, 120) }
)

$y = 20
foreach ($r in $rows) {
    $lum = Get-Luma $r.base
    $fl  = Shift-Lum $r.base ($(if ($lum -gt 0.5) { 0.8 } else { 1.2 }))
    $fb  = Shift-Lum $r.base ($(if ($lum -gt 0.5) { 0.7 } else { 1.35 }))

    $lblName = New-Object System.Windows.Forms.Label
    $lblName.Text = $r.name
    $lblName.Location = New-Object System.Drawing.Point(15, $y)
    $lblName.Size = New-Object System.Drawing.Size(140, 28)
    $lblName.TextAlign = "MiddleLeft"

    $lblBase = New-Object System.Windows.Forms.Label
    $lblBase.Text = "基准面板"
    $lblBase.Location = New-Object System.Drawing.Point(165, $y)
    $lblBase.Size = New-Object System.Drawing.Size(90, 28)
    $lblBase.BackColor = $r.base
    $lblBase.ForeColor = $(if ($lum -gt 0.5) { "Black" } else { "White" })
    $lblBase.TextAlign = "MiddleCenter"
    $lblBase.BorderStyle = "FixedSingle"

    $lblFloat = New-Object System.Windows.Forms.Label
    $lblFloat.Text = "浮层背景"
    $lblFloat.Location = New-Object System.Drawing.Point(265, $y)
    $lblFloat.Size = New-Object System.Drawing.Size(90, 28)
    $lblFloat.BackColor = $fl
    $lblFloat.ForeColor = $(if ($lum -gt 0.5) { "Black" } else { "White" })
    $lblFloat.TextAlign = "MiddleCenter"
    $lblFloat.BorderStyle = "FixedSingle"

    $lblBorder = New-Object System.Windows.Forms.Label
    $lblBorder.Text = "浮层边框"
    $lblBorder.Location = New-Object System.Drawing.Point(365, $y)
    $lblBorder.Size = New-Object System.Drawing.Size(90, 28)
    $lblBorder.BackColor = $fb
    $lblBorder.ForeColor = $(if ($lum -gt 0.5) { "Black" } else { "White" })
    $lblBorder.TextAlign = "MiddleCenter"
    $lblBorder.BorderStyle = "FixedSingle"

    $f.Controls.Add($lblName)
    $f.Controls.Add($lblBase)
    $f.Controls.Add($lblFloat)
    $f.Controls.Add($lblBorder)
    $y += 42
}

$tip = New-Object System.Windows.Forms.Label
$tip.Text = "说明：右键菜单 / 改名面板 / 删除确认框 = 浮层背景 + 浮层边框；深底提亮、浅底压暗，与主面板拉开层次。"
$tip.Location = New-Object System.Drawing.Point(15, ($y + 15))
$tip.Size = New-Object System.Drawing.Size(480, 30)
$tip.ForeColor = "Gray"
$f.Controls.Add($tip)

$btn = New-Object System.Windows.Forms.Button
$btn.Text = "关闭"
$btn.Location = New-Object System.Drawing.Point(215, ($y + 50))
$btn.Size = New-Object System.Drawing.Size(80, 30)
$btn.Add_Click({ $f.Close() })
$f.Controls.Add($btn)

$f.ShowDialog()
