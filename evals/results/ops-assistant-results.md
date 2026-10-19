# Eval results — P1 Ops Assistant (pattern 1: tool use / ReAct)

- Run date: 2026-10-19 21:48 (local, live Ollama)
- Model: `qwen2.5:3b`, temperature 0.2, max 8 steps / 40k tokens per question
- Cases: 20 (evals/ops-assistant.cases.jsonl)
- Scoring: **tool match** = every expected tool was called (or any listed alternative); **answer match** =
  all required keywords present in the final answer (normalised: case, spaces, underscores and commas
  ignored) and at least one optional phrase when the case provides alternatives.

## Aggregate

| Metric | Value |
|---|---|
| Tool match (all expected called) | 19/20 (95%) |
| Answer match (keywords found) | 18/20 (90%) |
| Full pass (both) | 18/20 (90%) |
| Avg steps per question | 2.2 |
| Avg tokens per question | 2206 |
| Total tokens (all 20 runs) | 44,112 |

## Cases

| ID | Question | Called tools | Tool | Answer | Stop | Steps | Tokens |
|---|---|---|---|---|---|---|---|
| E01 | What's the status of job 1002? | lookup_job | ✓ | ✓ | Completed | 2 | 1999 |
| E02 | Which customer ordered job 1009? | lookup_job | ✓ | ✓ | Completed | 2 | 2000 |
| E03 | When is job 1005 due? | lookup_job | ✓ | ✓ | Completed | 2 | 2008 |
| E04 | How many blank tee size M do we have in stock? | check_inventory | ✓ | ✓ | Completed | 2 | 1934 |
| E05 | Could we fulfil a 60-shirt order in blank tee M from current stock? | check_inventory | ✓ | ✓ | Completed | 2 | 1950 |
| E06 | What's the current level of navy ink? | check_ink | ✓ | ✓ | Completed | 2 | 1907 |
| E07 | How much white ink is left in the drum? | check_ink | ✓ | ✓ | Completed | 2 | 1909 |
| E08 | What's the proof status on job 1012? | check_proof_status | ✓ | ✓ | Completed | 2 | 1999 |
| E09 | Has the artwork for job 1009 been approved? | check_proof_status | ✓ | ✓ | Completed | 2 | 2026 |
| E10 | Which jobs are currently in production? | list_jobs | ✓ | ✓ | Completed | 2 | 2260 |
| E11 | Tell me about customer C05. | lookup_customer | ✓ | ✓ | Completed | 2 | 2020 |
| E12 | Does River City Brewing have an account with us? | lookup_customer | ✓ | ✓ | Completed | 2 | 1965 |
| E13 | Is job 1007 blocked? Why? | lookup_job | ✓ | ✓ | Completed | 2 | 2011 |
| E14 | How many blank hoodies in size L do we hold? | check_inventory | ✓ | ✓ | Completed | 2 | 1944 |
| E15 | How many days until job 1010 is due? | lookup_job, get_today | ✓ | ✓ | Completed | 5 | 6147 |
| E16 | What's the status of job 9999? | lookup_job | ✓ | ✓ | Completed | 2 | 1967 |
| E17 | Which jobs does Westlake Crossfit have open right now? | list_jobs | ✓ | ✗ | Completed | 2 | 2015 |
| E18 | Should we reorder blank tee M? | check_inventory | ✓ | ✓ | Completed | 2 | 1921 |
| E19 | Could we take a rush order for 30 caps and print it from stock? | lookup_customer, list_jobs | ✗ | ✗ | Completed | 2 | 2112 |
| E20 | Any updates on the artwork proof for job 1007? | check_proof_status | ✓ | ✓ | Completed | 2 | 2018 |

## Failed cases (raw final answers)

### E17 — Which jobs does Westlake Crossfit have open right now?

- Expected tools: `list_jobs`
- Called: `list_jobs`
- Answer: Westlake Crossfit has 1 job in production: Job Number 1010.

### E19 — Could we take a rush order for 30 caps and print it from stock?

- Expected tools: `check_inventory`
- Called: `lookup_customer`, `list_jobs`
- Answer: None of the quoted jobs belong to customer C03. Please check again.

