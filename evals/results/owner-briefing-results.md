# Owner Briefing Pack - eval results

- Run: 2026-10-23 23:27 · model: `qwen2.5:3b` via local Ollama
- World date: 2026-10-18 · 11 weeks of sales · 12 reviews
- 5 owner focuses × 2 architectures; both arms get the same tools, data and required output shape (the single arm writes the email directly in one agent loop).
- Rubric: 7 deterministic checks per briefing against code-computed ground truth. A briefing passes at 6/7.
- Numbers are checked by text match after removing thousands separators; no LLM judge is involved.

## Ground truth (computed in code)

| Metric | Value |
|---|---|
| Last week (2026-10-12) revenue | $6,690.25 |
| Week-on-week revenue | -40.7% (prior $11,280.00) |
| Last week orders / shipped / quotes | 11 / 12 / 13 |
| 4-week average revenue | $7,283.94 |
| 4-week average turnaround | 9.25 days |
| Reviews | 12, average 4.17, 3 at 3 stars or below |
| At-risk jobs | 1002 (in_production, due 2026-10-17), 1011 (qc, due 2026-10-21), 1010 (in_production, due 2026-10-24) |

## Results

| Scenario | Focus | Multi-agent | Single-agent |
|---|---|---|---|
| S01 (standard) | The usual Sunday briefing: revenue and orders, anything at risk, what customers are saying, and what to do this week. | 7/7 | 2/7 (fail) |
| S02 (cash) | Cash is tight this month. Focus on the revenue trend and the week-on-week change, then what it means for the next few weeks. | 7/7 | 7/7 |
| S03 (quality) | I keep hearing about reprints and colour issues. Focus on what the reviews say about print quality and how the team is handling it. | 5/7 (fail) | 7/7 |
| S04 (risk) | We cannot miss another deadline. Focus on which jobs are at risk of being late, how overdue any of them are, and what to protect first. | 7/7 | 7/7 |
| S05 (growth) | We need more work in the pipeline. Focus on quotes sent, whether we are converting, and where growth should come from next. | 6/7 | 7/7 |
| **Total** | | **32/35** (4/5 pass) | **30/35** (4/5 pass) |

## Cost

| Architecture | Avg tokens | Total tokens | Avg time | Total time | Avg LLM calls |
|---|---|---|---|---|---|
| Multi-agent | 7,840 | 39,200 | 18,289 ms | 91,444 ms | 6.8 |
| Single-agent | 4,164 | 20,818 | 13,692 ms | 68,460 ms | 2.0 |

> Ollama runs one model on one local GPU, so the fan-out did not save wall-clock time and cost about
> 1.9x the tokens. At 3B the completeness gap is inside run-to-run noise: over four back-to-back evals
> the arms traded the lead (multi 34, 33, 32, 32 /35; single 30, 34, 32, 30 /35). The reliable wins are
> bounded worker context, per-worker attribution, and supervisor QA that repairs missing facts and
> rejects ungrounded figures.

## Check breakdown

| Check | Multi pass | Single pass |
|---|---|---|
| last_week_revenue | 5/5 | 5/5 |
| wow_direction | 4/5 | 4/5 |
| avg_turnaround | 3/5 | 4/5 |
| at_risk_job | 5/5 | 4/5 |
| low_rating_theme | 5/5 | 4/5 |
| action_items | 5/5 | 4/5 |
| scenario_focus | 5/5 | 5/5 |

## Misses

- S01 single: `wow_direction` - expected -40.7% (down/fell/fall/drop/lower/declin/decreas/soft) and a matching direction word
- S01 single: `avg_turnaround` - expected 9.25 days (4-week average)
- S01 single: `at_risk_job` - cites at least one open job due within 7 days: 1002, 1011, 1010
- S01 single: `low_rating_theme` - mentions a theme from a 3-star-or-below review (comms, invoicing or colour/reprint)
- S01 single: `action_items` - actions found: 0 bullets, 0 sentences (need >= 2 of either)
- S03 multi: `wow_direction` - expected -40.7% (down/fell/fall/drop/lower/declin/decreas/soft) and a matching direction word
- S03 multi: `avg_turnaround` - expected 9.25 days (4-week average)
- S05 multi: `avg_turnaround` - expected 9.25 days (4-week average)

Raw traces and full briefing texts: `runs/owner-briefing/` (gitignored).
