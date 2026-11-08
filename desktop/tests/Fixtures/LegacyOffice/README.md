# Legacy Office fixtures

`sample.doc`, `sample.xls` and `sample.ppt` are synthetic Word 97-2003, Excel 97-2003 and PowerPoint 97-2003 files produced by
`make-fixtures.ps1` with a local Microsoft Office installation. They contain only invented text (Chinese and English, a table,
two worksheets with 700 shared strings so the shared string table spills into CONTINUE records, slide notes) and each carries a
unique `legacy-*-marker-*` token. They hold no private data. CI never needs Office: the tests read these committed bytes.
