# 40 rows, each DISTINCT: identical rows would let a selection that slid onto other text compare
# equal to the original and pass.
1..40 | ForEach-Object { Write-Host "MARKER-$_-xxxxxxxxxxxxxxxx" }
