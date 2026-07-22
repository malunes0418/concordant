# Coverage baseline

CI collects Cobertura coverage on Ubuntu for `net8.0` (`coverlet.collector`) and enforces the floor in [baseline.json](baseline.json).

| Field | Meaning |
| --- | --- |
| `measuredLineCoveragePercent` | Aggregate line coverage observed when the baseline was recorded |
| `lineCoverageMinimum` | Soft CI gate (fail if aggregate drops below this) |

Re-measure locally:

```powershell
dotnet test Concordant.slnx --framework net8.0 --configuration Release --collect:"XPlat Code Coverage" --results-directory artifacts/coverage -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura
./scripts/Assert-Coverage.ps1 -ResultsDirectory artifacts/coverage -ReportPath artifacts/coverage/summary.json
```

Then update `baseline.json` with the new measured value and an agreed minimum.
