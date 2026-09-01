# Enough output to scroll the screen, without evicting anything.
1..40 | ForEach-Object { Write-Host "noise-$_" }
