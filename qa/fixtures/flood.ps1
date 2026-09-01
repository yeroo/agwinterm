# Past the 5000-line cap plus the batched-trim slack (512), so eviction really happens.
1..6000 | ForEach-Object { Write-Host "flood-$_" }
