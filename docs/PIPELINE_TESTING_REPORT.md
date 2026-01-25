# NzbDav Pipeline Testing Report

**Generated:** 2026-01-25 20:47 UTC
**Test Environment:** NzbDav Backend on Linux
**Connections:** 20
**Speed Test Size:** 50 MB

## Executive Summary

**Overall Status: PARTIAL**

Speed tests show lower than optimal throughput (3-9 MB/s vs target >10 MB/s). Health checks timeout due to large file sizes, but show healthy segment availability where tested. Import pipeline functions correctly with fetch bottleneck identified.

## Test Configuration

| Setting | Value |
|---------|-------|
| Connections | 20 |
| Speed Test Size | 50 MB |
| Timeouts | 120-300s per test |
| NZBs Tested | 3 |
| Providers | 4 (xsnews, usenetexpress, newshosting, frugalusenet) |

## NZBs Tested

| # | Name | Size |
|---|------|------|
| 1 | The.French.Dispatch.2021.PROPER.BluRay.1080p.DTS-HD.MA.5.1.AVC.HYBRID.REMUX-FraMeSToR | 28.27 GB |
| 2 | Spartacus.House.of.Ashur.S01E08.HORIZONS.1080p.AMZN.WEB-DL.DDP5.1.H.264-playWEB | 4.35 GB |
| 3 | Dust.Bunny.2025.Hybrid.2160p.WEB-DL.DV.HDR.DDP5.1.Atmos.H265-AOC | 18.83 GB |

---

## Detailed Results

### NZB 1: The.French.Dispatch.2021

**Size:** 28.27 GB | **Segments:** 41,353

#### Import Test
| Metric | Value | Status |
|--------|-------|--------|
| Total Time | 21.00s | OK |
| Bottleneck | Fetch first segments | 62.1% |

<details>
<summary>Step Breakdown</summary>

| Step | Time | % |
|------|------|---|
| Parse NZB | 1.36s | 6.5% |
| Fetch first segments | 13.03s | 62.1% |
| Par2 descriptors | 6.53s | 31.1% |
| Build file infos | 0.06s | 0.3% |
| File processing | 0.00s | 0.0% |

</details>

#### Speed Test
| Metric | Value | Status |
|--------|-------|--------|
| Speed | 3.23 MB/s | POOR |
| P95 Read | 0.11ms | GOOD |
| Max Read | 9065ms | STALL |
| Connection Acquire | 84% of fetch time | HIGH |

#### Health Check
| Metric | Value | Status |
|--------|-------|--------|
| Status | TIMEOUT | - |
| Progress | 5.0% | - |
| Throughput | 19.8 seg/s | OK |
| ETA | 1988s | - |

---

### NZB 2: Spartacus.House.of.Ashur

**Size:** 4.35 GB | **Segments:** 6,359

#### Import Test
| Metric | Value | Status |
|--------|-------|--------|
| Total Time | 13.48s | OK |
| Bottleneck | Fetch first segments | 90.9% |

<details>
<summary>Step Breakdown</summary>

| Step | Time | % |
|------|------|---|
| Parse NZB | 0.35s | 2.6% |
| Fetch first segments | 12.24s | 90.9% |
| Par2 descriptors | 0.85s | 6.3% |
| Build file infos | 0.02s | 0.2% |
| File processing | 0.00s | 0.0% |

</details>

#### Speed Test
| Metric | Value | Status |
|--------|-------|--------|
| Speed | 3.17 MB/s | POOR |
| P95 Read | 0.49ms | GOOD |
| Max Read | 5654ms | STALL |
| Connection Acquire | 65% of fetch time | HIGH |

#### Health Check
| Metric | Value | Status |
|--------|-------|--------|
| Status | TIMEOUT | - |
| Progress | 54.8% | - |
| Throughput | 21.2 seg/s | OK |
| ETA | 135s remaining | - |

---

### NZB 3: Dust.Bunny.2025

**Size:** 18.83 GB | **Segments:** 27,551

#### Import Test
| Metric | Value | Status |
|--------|-------|--------|
| Total Time | 11.80s | GOOD |
| Bottleneck | Fetch first segments | 95.2% |

<details>
<summary>Step Breakdown</summary>

| Step | Time | % |
|------|------|---|
| Parse NZB | 0.53s | 4.5% |
| Fetch first segments | 11.22s | 95.2% |
| Par2 descriptors | 0.01s | 0.0% |
| Build file infos | 0.03s | 0.3% |
| File processing | 0.00s | 0.0% |

</details>

#### Speed Test
| Metric | Value | Status |
|--------|-------|--------|
| Speed | 9.05 MB/s | OK |
| P95 Read | 140.51ms | SLOW |
| Max Read | 2306ms | STALL |
| Connection Acquire | 70% of fetch time | HIGH |

#### Health Check
| Metric | Value | Status |
|--------|-------|--------|
| Status | TIMEOUT | - |
| Progress | 5.0% | - |
| Throughput | 17.9 seg/s | OK |
| ETA | 1466s | - |

---

## Performance Summary

| Metric | NZB 1 | NZB 2 | NZB 3 | Avg |
|--------|-------|-------|-------|-----|
| Import Time (s) | 21.00 | 13.48 | 11.80 | 15.43 |
| Speed (MB/s) | 3.23 | 3.17 | 9.05 | 5.15 |
| Health (seg/s) | 19.8 | 21.2 | 17.9 | 19.63 |
| Max Stall (ms) | 9065 | 5654 | 2306 | 5675 |
| Conn Acquire % | 84% | 65% | 70% | 73% |

## Rating Scale

| Test | Good | Acceptable | Poor |
|------|------|------------|------|
| Import Time | <30s | 30-60s | >60s |
| Download Speed | >10 MB/s | 5-10 MB/s | <5 MB/s |
| P95 Read Time | <50ms | 50-100ms | >100ms |
| Connection Acquire | <50% | 50-70% | >70% |

## Issues Found

1. **Low Download Speed**: Average 5.15 MB/s is below target of 10 MB/s
   - 2 of 3 tests showed POOR speed (<5 MB/s)
   - Connection acquire time averaging 73% indicates connection pool contention

2. **Stall Detection**: All tests showed max read times >1 second
   - The.French.Dispatch: 9065ms stall
   - Spartacus: 5654ms stall
   - Dust.Bunny: 2306ms stall

3. **Health Check Timeouts**: All health checks timed out due to large file sizes
   - Tests require significantly more time for full segment validation
   - Segment throughput is healthy (17-21 seg/s)

4. **Import Bottleneck**: "Fetch first segments" step dominates import time
   - Consistently 62-95% of total import time
   - Provider latency appears to be the limiting factor

## Recommendations

1. **Increase Connections**: Consider increasing from 20 to 30-40 connections to improve throughput
   - Connection acquire time (73% avg) suggests pool saturation
   - CLAUDE.md recommends up to 40 connections for testing

2. **Extend Health Check Timeout**: Health checks on large files need 20-30+ minutes
   - Current 2-3 minute timeout is insufficient for 20-80GB files
   - Consider adding a `--segments=N` option to limit health check scope

3. **Monitor Provider Performance**: High connection acquire times may indicate:
   - Provider throttling or connection limits
   - Network latency to provider servers
   - Consider testing individual providers with `--provider=N` flag

4. **Buffer Tuning**: P95 read time of 140ms on NZB 3 suggests buffer starvation
   - Increase stream buffer size in Settings > WebDAV
   - Current recommendation: connections * 5 segments minimum

---

## Raw Test Data

### Provider Configuration
```
[0] reader.xsnews.nl (45 conn)
[1] news-eu.usenetexpress.com (45 conn)
[2] news.newshosting.com (90 conn)
[3] eunews.frugalusenet.com (90 conn)
Total: 270 connections available
```

### Timing Breakdown (Fetch Phase)
| NZB | Total Fetch | Conn Acquire | Network Read | Avg/Segment |
|-----|-------------|--------------|--------------|-------------|
| French.Dispatch | 211,755ms | 179,290ms (84%) | 71,307ms (33%) | 1,224ms |
| Spartacus | 273,697ms | 179,658ms (65%) | 110,879ms (40%) | 1,094ms |
| Dust.Bunny | 100,101ms | 70,970ms (70%) | 32,590ms (32%) | 1,251ms |

---
*Report generated automatically by /pipeline-test*
