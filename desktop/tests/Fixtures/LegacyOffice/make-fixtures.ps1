# Regenerates the synthetic legacy Office fixtures with an installed Microsoft Office (Word, Excel, PowerPoint).
# Run with: pwsh -File make-fixtures.ps1   (no Office window may be open: the script quits the instances it starts).
$ErrorActionPreference = 'Stop'
$out = 'D:\projects\WinSight\desktop\tests\Fixtures\LegacyOffice'
New-Item -ItemType Directory -Force -Path $out | Out-Null
Get-ChildItem $out -Filter 'sample.*' -File -ErrorAction SilentlyContinue | Remove-Item -Force

# --- Word 97-2003 -----------------------------------------------------------
$word = $null
try {
    $word = New-Object -ComObject Word.Application
    $word.Visible = $false
    $word.DisplayAlerts = 0
    $doc = $word.Documents.Add()
    $sel = $word.Selection
    $sel.TypeText("项目评审会议纪要")
    $sel.TypeParagraph()
    $sel.TypeText("本次会议讨论医院信息平台升级方案，并确认预算。English phrase: legacy-doc-marker-7391.")
    $sel.TypeParagraph()
    $sel.TypeText("第三段包含数字 12345 与符号 A-1。")
    $sel.TypeParagraph()
    $table = $doc.Tables.Add($sel.Range, 2, 2)
    $table.Cell(1, 1).Range.Text = "设备"
    $table.Cell(1, 2).Range.Text = "数量"
    $table.Cell(2, 1).Range.Text = "心电图机"
    $table.Cell(2, 2).Range.Text = "42"
    $doc.SaveAs2("$out\sample.doc", 0)   # wdFormatDocument97
    $doc.Close($false)
}
finally { if ($word) { $word.Quit(0) } }

# --- Excel 97-2003 ----------------------------------------------------------
$excel = $null
try {
    $excel = New-Object -ComObject Excel.Application
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $wb = $excel.Workbooks.Add()
    $s1 = $wb.Worksheets.Item(1)
    $s1.Name = "清单"
    $s1.Cells.Item(1, 1).Value2 = "编号"
    $s1.Cells.Item(1, 2).Value2 = "名称"
    $s1.Cells.Item(1, 3).Value2 = "金额"
    $s1.Cells.Item(2, 1).Value2 = "A-1"
    $s1.Cells.Item(2, 2).Value2 = "心电图机 legacy-xls-marker-5520"
    $s1.Cells.Item(2, 3).Value2 = 1234.5
    $s1.Cells.Item(3, 1).Value2 = "A-2"
    $s1.Cells.Item(3, 2).Value2 = "监护仪"
    $s1.Cells.Item(3, 3).Value2 = 100
    # Enough distinct strings that the shared string table spills into CONTINUE records (> 8224 bytes).
    $s2 = $wb.Worksheets.Add([Type]::Missing, $s1)
    $s2.Name = "Bulk"
    $cells = New-Object 'object[,]' 700, 1
    for ($i = 0; $i -lt 700; $i++) { $cells[$i, 0] = "bulk-row-$i 批量条目 item $i" }
    $s2.Range("A1:A700").Value2 = $cells
    $wb.SaveAs("$out\sample.xls", 56)    # xlExcel8
    $wb.Close($false)
}
finally { if ($excel) { $excel.Quit() } }

# --- PowerPoint 97-2003 -----------------------------------------------------
$ppt = $null
try {
    $ppt = New-Object -ComObject PowerPoint.Application
    $pres = $ppt.Presentations.Add(0)
    $layoutTitleBody = 2   # ppLayoutText
    $slide1 = $pres.Slides.Add(1, $layoutTitleBody)
    $slide1.Shapes.Item(1).TextFrame.TextRange.Text = "医院数字化路线图"
    $slide1.Shapes.Item(2).TextFrame.TextRange.Text = "第一阶段 信创替代`r第二阶段 legacy-ppt-marker-8842"
    $slide1.NotesPage.Shapes.Item(2).TextFrame.TextRange.Text = "讲稿备注 notes-marker-3310"
    $slide2 = $pres.Slides.Add(2, $layoutTitleBody)
    $slide2.Shapes.Item(1).TextFrame.TextRange.Text = "Second slide"
    $slide2.Shapes.Item(2).TextFrame.TextRange.Text = "DRG/DIP 合规运营"
    $pres.SaveAs("$out\sample.ppt", 1)   # ppSaveAsPresentation: 97-2003 binary
    $pres.Close()
}
finally { if ($ppt) { $ppt.Quit() } }

Get-ChildItem $out | Select-Object Name, Length
