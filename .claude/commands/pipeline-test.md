# Pipeline Test

Run a comprehensive, fully automated pipeline test of the NzbDav system.

## IMPORTANT: This is an autonomous agent

You MUST execute all commands yourself. Do NOT ask the user for input. Do NOT wait for confirmation. Run all tests and generate the report automatically.

## Execution Steps

### Step 1: Find NZBs in Database

Execute this command to find available NZBs:
```bash
cd /home/ubuntu/nzbdav/backend && timeout 120 dotnet run -- --test-db-nzb --find "" 2>&1 | tail -40
```

From the output, select 3 NZBs of varying sizes. If $ARGUMENTS is provided, use it as a search filter or count.

### Step 2: Run Tests on Each NZB

For each of the 3 selected NZBs, execute these 3 tests IN SEQUENCE (run all 3 tests for NZB 1, then all 3 for NZB 2, etc.):

**Test A - Import Test:**
```bash
cd /home/ubuntu/nzbdav/backend && timeout 300 dotnet run -- --test-db-nzb "EXACT_FILE_NAME" --test-import --connections=20 2>&1
```

**Test B - Speed Test:**
```bash
cd /home/ubuntu/nzbdav/backend && timeout 300 dotnet run -- --test-db-nzb "EXACT_FILE_NAME" --size=50 --connections=20 2>&1
```

**Test C - Health Check:**
```bash
cd /home/ubuntu/nzbdav/backend && timeout 300 dotnet run -- --test-db-nzb "EXACT_FILE_NAME" --health-check --connections=20 2>&1
```

Replace "EXACT_FILE_NAME" with the actual filename from Step 1. Use a unique substring that matches only one file.

### Step 3: Parse Results

For each test, extract these metrics:

**Import Test Metrics:**
- Total time (from "TOTAL" line)
- Slowest step and percentage
- Any errors or warnings

**Speed Test Metrics:**
- Download speed in MB/s (from "Speed:" line)
- P95 read time (from "P95:" line)
- Max read time (from "Max:" line)
- Any bottleneck warnings

**Health Check Metrics:**
- Status (HEALTHY/UNHEALTHY/TIMEOUT)
- Segments checked
- Throughput (seg/s)
- P50, P95, P99 latency

### Step 4: Generate Report

Write the report to `/home/ubuntu/nzbdav/docs/PIPELINE_TESTING_REPORT.md` using the Write tool with this exact format:

```markdown
# NzbDav Pipeline Testing Report

**Generated:** [current date/time]
**Test Environment:** NzbDav Backend on Linux
**Connections:** 20
**Speed Test Size:** 50 MB

## Executive Summary

**Overall Status: [PASS/FAIL/PARTIAL]**

[1-2 sentence summary based on results]

## Test Configuration

| Setting | Value |
|---------|-------|
| Connections | 20 |
| Speed Test Size | 50 MB |
| Timeouts | 300s per test |
| NZBs Tested | 3 |

## NZBs Tested

| # | Name | Size |
|---|------|------|
| 1 | [name] | [size] |
| 2 | [name] | [size] |
| 3 | [name] | [size] |

---

## Detailed Results

### NZB 1: [Name]

#### Import Test
| Metric | Value | Status |
|--------|-------|--------|
| Total Time | Xs | [OK/SLOW/CRITICAL] |
| Bottleneck | [step] | [X%] |

<details>
<summary>Step Breakdown</summary>

| Step | Time | % |
|------|------|---|
| Parse NZB | | |
| Fetch first segments | | |
| Par2 descriptors | | |
| Build file infos | | |
| File processing | | |

</details>

#### Speed Test
| Metric | Value | Status |
|--------|-------|--------|
| Speed | X.XX MB/s | [GOOD/OK/POOR] |
| P95 Read | Xms | |
| Max Read | Xms | |

#### Health Check
| Metric | Value | Status |
|--------|-------|--------|
| Status | HEALTHY/UNHEALTHY | |
| Throughput | X seg/s | |
| P50 Latency | Xms | |
| P99 Latency | Xms | |

---

### NZB 2: [Name]
[Same format as NZB 1]

---

### NZB 3: [Name]
[Same format as NZB 1]

---

## Performance Summary

| Metric | NZB 1 | NZB 2 | NZB 3 | Avg |
|--------|-------|-------|-------|-----|
| Import Time (s) | | | | |
| Speed (MB/s) | | | | |
| Health (seg/s) | | | | |

## Rating Scale

| Test | Good | Acceptable | Poor |
|------|------|------------|------|
| Import Time | <30s | 30-60s | >60s |
| Download Speed | >10 MB/s | 5-10 MB/s | <5 MB/s |
| P95 Read Time | <50ms | 50-100ms | >100ms |

## Issues Found

[List any issues, or "No issues found during testing."]

## Recommendations

[Based on results, or "System operating normally. No action required."]

---
*Report generated automatically by /pipeline-test*
```

### Step 5: Report Completion

After writing the report, output a brief summary:
- Overall status (PASS/FAIL/PARTIAL)
- Average download speed
- Any critical issues
- Location of full report

## Error Handling

- If a test times out, record "TIMEOUT" and continue with next test
- If a test fails to run, record the error and continue
- If no NZBs found in database, report this and exit
- If build fails, report the build error and exit

## Thresholds for Status

**PASS:** All tests complete, speed >5 MB/s, all health checks healthy
**PARTIAL:** Some tests have warnings or minor issues
**FAIL:** Any test fails, speed <5 MB/s, or unhealthy segments found

## DO NOT

- Do NOT ask the user which NZBs to test
- Do NOT ask for confirmation before running tests
- Do NOT stop between tests for user input
- Do NOT skip generating the report

Execute everything autonomously and report results when complete.
