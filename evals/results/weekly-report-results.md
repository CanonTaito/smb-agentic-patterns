# Eval results — P2 Weekly Report (pattern 2: planning & task decomposition)

- Run date: 2026-10-20 23:36 (local, live Ollama)
- Model: `qwen2.5:3b`, temperature 0.2; every case runs the FULL pipeline: plan → gate (auto-approved) → execute → assemble → self-verify
- Cases: 10 (evals/weekly-report.cases.jsonl)
- Scoring: **plan ok** = pipeline completed and every planned step executed successfully;
  **tool match** = every expected tool ran; **report match** = keyword anchors in the final
  (possibly repaired) report, normalised for case/spaces/underscores/commas.
  **self-check** is the agent's own verification verdict — reported as a metric, not a gate.

## Aggregate

| Metric | Value |
|---|---|
| Plan ok (all steps executed) | 9/10 (90%) |
| Step success (executions) | 38/39 (97%) |
| Steps needing args repair | 1/39 |
| Tool match | 9/10 (90%) |
| Report keyword match | 6/10 (60%) |
| Full pass (plan+tool+report) | 6/10 (60%) |
| Agent self-check passed | 6/10 (60%) |
| Avg plan steps | 3.9 |
| Avg LLM calls / tokens per case | 3.9 / 4796 |
| Total tokens | 47,963 |

## Cases

| ID | Request | Plan | Called tools | Tool | Report | Self-check | Tokens |
|---|---|---|---|---|---|---|---|
| E01 | Produce the weekly operations report for the week ending 202... | 7 steps, 1 failed | get_today, at_risk_jobs, list_jobs, recent_reviews, check_ink, check_inventory | ✗ | ✗ | ✗ | 6353 |
| E02 | Just the cash position: what is owed to us and what is overd... | 3 steps, 0 failed | get_today, list_invoices | ✓ | ✓ | ✓ | 2992 |
| E03 | Stock check report: what is running low — blanks and ink? | 5 steps, 0 failed | get_today, list_jobs, list_invoices, check_ink, check_inventory | ✓ | ✗ | ✗ | 6494 |
| E04 | Which jobs are at risk this week, and which are already late... | 4 steps, 0 failed | get_today, list_jobs, at_risk_jobs, list_invoices | ✓ | ✓ | ✓ | 4455 |
| E05 | Summarise recent customer feedback — anything we should worr... | 2 steps, 0 failed | get_today, recent_reviews | ✓ | ✓ | ✓ | 2420 |
| E06 | How have we been trending on revenue and turnaround? Weekly ... | 4 steps, 0 failed | get_today, list_jobs, list_invoices, weekly_sales | ✓ | ✓ | ✗ | 6528 |
| E07 | What needs chasing first thing Monday — money owed and jobs ... | 4 steps, 0 failed | get_today, list_jobs, list_invoices, at_risk_jobs | ✓ | ✗ | ✓ | 5885 |
| E08 | Production status report: which jobs are on the presses and ... | 2 steps, 0 failed | get_today, list_jobs | ✓ | ✓ | ✗ | 4973 |
| E09 | List all customer reviews rated 3 or below, with the custome... | 2 steps, 0 failed | get_today, recent_reviews | ✓ | ✓ | ✓ | 2316 |
| E10 | Which week was our best for revenue, and what did we ship th... | 6 steps, 0 failed | get_today, list_jobs, list_invoices, weekly_sales | ✓ | ✗ | ✓ | 5547 |

## Failed cases

### E01 — Produce the weekly operations report for the week ending 2026-10-18 covering jobs at risk, cash owed, stock levels for blanks and ink, and recent customer feedback.

- Expected tools: `at_risk_jobs`, `list_invoices`, `check_inventory`, `check_ink`, `recent_reviews`
- Called: `get_today`, `at_risk_jobs`, `list_jobs`, `recent_reviews`, `check_ink`, `check_inventory`
- Missing keywords: `1011`, `3101` required / any of `navy`, `tee m`, `reorder`, `low`
- Self-check: ok=False, missing=[business_date, jobs_at_risk, all_print_jobs, customer_reviews], issues=[Ink Levels - Navy: 450 ml, Low: true should be Low: false]
- Report: ## Cash Owed The section is unavailable because the provided data does not include valid status options for the `list_invoices` tool.  ## Ink Levels The data shows the following ink levels: - Black: 4500 ml, Low: false - Charcoal: 2400 ml, Low: false - Forest Green: 1800 ml, Low: false - Mustard: 1350 ml, Low: false - Navy: 450 ml, Low: false - Red: 2200 ml, Low: false - Teal: 900 ml, Low: false - White: 6000 ml, Low: false  ## Blank Stock Levels The section is available and the data shows the following stock levels for different garment types and sizes: - Cap (OneSize): 60 pieces, ReorderLevel: 25, Low: false - Hoodie (L): 50 pieces, ReorderLevel: 20, Low: false - Hoodie (M): 45 pieces, Reo...

### E03 — Stock check report: what is running low — blanks and ink?

- Expected tools: `check_inventory`, `check_ink`
- Called: `get_today`, `list_jobs`, `list_invoices`, `check_ink`, `check_inventory`
- Missing keywords: `450`, `15` required
- Self-check: ok=False, missing=[], issues=[No list of at-risk jobs found]
- Report: ## Get Today - The business date for the frozen dataset snapshot is `2026-10-18`. Relative dates should be computed from this date.  ## Job Status - No print jobs are due within 7 days of the business date.  ## Invoice Status - There are 3 unpaid invoices:   - Invoice ID: `INV-3103`, Job Number: `1004`, Customer ID: `C05`, Customer Name: `Ferny Grove Bakery`, Amount: $590.00, Due Date: `2026-10-23`.   - Invoice ID: `INV-3104`, Job Number: `1008`, Customer ID: `C05`, Customer Name: `Ferny Grove Bakery`, Amount: $638.40, Due Date: `2026-10-30`.   - Invoice ID: `INV-3105`, Job Number: `1011`, Customer ID: `C04`, Customer Name: `Brisbane Makerspace`, Amount: $173.20, Due Date: `2026-10-30`.  ## ...

### E07 — What needs chasing first thing Monday — money owed and jobs running late?

- Expected tools: `list_invoices`, `at_risk_jobs`
- Called: `get_today`, `list_jobs`, `list_invoices`, `at_risk_jobs`
- Missing keywords: `3101` required / any of `1002`, `late`
- Self-check: ok=True, missing=[], issues=[]
- Report: ## Business Date The current business date for the report generation is **2026-10-18**.  ## Jobs The list of print jobs ordered by their due date is as follows: - Job Number: 1002, Customer: Northside Cafe, Description: Cafe aprons - charcoal print, Quantity: 40, Status: in_production, Quoted Total: $684.0, Due Date: 2026-10-17, Rush: false, Created At: 2026-10-01 - Job Number: 1010, Customer: Ferny Grove Bakery, Description: Promo caps, Quantity: 30, Status: in_production, Quoted Total: $376.5, Due Date: 2026-10-24, Rush: false, Created At: 2026-10-07 - Job Number: 1005, Customer: Westlake Crossfit, Description: Training tees - new member intake, Quantity: 120, Status: in_production, Quoted...

### E10 — Which week was our best for revenue, and what did we ship that week?

- Expected tools: `weekly_sales`
- Called: `get_today`, `list_jobs`, `list_invoices`, `weekly_sales`
- Missing keywords: `11280` required / any of `2026-10-05`, `5 october`, `october 5`, `10-05`, `5 oct`
- Self-check: ok=True, missing=[], issues=[]
- Report: ## Revenue  ### Best Week for Revenue The best week for revenue was the week starting on 2026-10-12. The total revenue for this week was $6690.25.  ### Details of Print Jobs and Invoices for the Best Week #### Print Jobs - **Job Number:** 1002   - **Customer:** Northside Cafe   - **Description:** Cafe aprons - charcoal print   - **Quantity:** 40   - **Print Specification:** Front 2-colour (mustard + white)   - **Status:** in_production   - **Due Date:** 2026-10-17   - **Created At:** 2026-10-01  - **Job Number:** 1010   - **Customer:** Ferny Grove Bakery   - **Description:** Promo caps   - **Quantity:** 30   - **Print Specification:** Front 1-colour (forest)   - **Status:** in_production   -...

