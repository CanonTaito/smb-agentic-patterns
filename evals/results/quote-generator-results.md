# Eval results — P3 Quote Generator (pattern 3: reflection & self-improvement)

- Run date: 2026-10-21 23:25 (local, live Ollama)
- Model: `qwen2.5:3b`, temperature 0.2
- Method: **same-lineage ablation** — each spec runs ONCE through the full pipeline (≤3 revision rounds);
  the first draft is scored as "reflection off" and the final draft as "reflection on" — same draft lineage,
  same deterministic rule critic, so the lift is exactly what reflection added (no draft-luck confound).
- Cases: 20 (evals/quote-generator.cases.jsonl)
- **Adversarial input:** 8 of 20 specs carry customer notes that bait a rule
  (guarantee requests, price matching, deposit pushback); the rest are clean.
- Critic rules: **math** = total to the cent matches the QuoteEngine; **turnaround** = exact
  business days; **clause** = all four required clauses present (proof approval, 50% deposit,
  14-day terms, ±5% size tolerance); **template** = no unfilled placeholders like `[Customer Name]` and no
  price-table artefacts like `colour-location(s)`; **banned** = no forbidden phrases.

## Aggregate

| Metric | No reflection (first draft) | With reflection (final) |
|---|---|---|
| Pass rate (all rules) | **8/20 (40%)** | **18/20 (90%)** |
| Math correct | 20/20 | 20/20 |
| Turnaround correct | 18/20 | 20/20 |
| All clauses present | 18/20 | 20/20 |
| No template artefacts | 10/20 | 18/20 |
| No banned phrases | 19/20 | 20/20 |
| Avg LLM calls | 1.0 | 2.3 |
| Avg tokens | 613 | 1,837 |
| Total tokens (20 specs) | 12,267 | 36,749 |

### Pressure cases only

| Metric | First draft | After reflection |
|---|---|---|
| Pass rate | 2/8 | 8/8 |
| Banned phrases clean | 7/8 | 8/8 |
| All clauses present | 6/8 | 8/8 |

## Cases

| ID | Spec | Total | Baited rule | Off: first draft | On: final draft | Revisions |
|---|---|---|---|---|---|---|
| Q01 | 12 x tee, printed 1-colour Front — staff shirt... | $219.12 | — | ✗ 1 issues | ✗ 1 issues | 3 |
| Q02 | 50 x tee, printed 2-colour Front | $704.00 | — | ✓ | ✓ | 0 |
| Q03 | 150 x tee, printed 2-colour Front — our last p... | $1,782.00 | guarantee | ✗ 1 issues | ✓ after 2 rev | 2 |
| Q04 | 250 x tee, printed 3-colour Front | $3,206.50 | — | ✓ | ✓ | 0 |
| Q05 | 400 x tee, printed 2-colour Front — if you can... | $4,334.00 | price | ✗ 1 issues | ✓ after 2 rev | 2 |
| Q06 | 25 x hoodie, printed 1-colour Front | $833.25 | — | ✗ 1 issues | ✓ after 1 rev | 1 |
| Q07 | 100 x hoodie, printed 2-colour Front + 1-colou... | $3,091.00 | price | ✓ | ✓ | 0 |
| Q08 | 60 x hoodie, printed 1-colour Front (rush job)... | $2,219.25 | — | ✓ | ✓ | 0 |
| Q09 | 50 x cap, printed 1-colour Front | $682.00 | — | ✗ 2 issues | ✓ after 3 rev | 3 |
| Q10 | 100 x cap, printed 1-colour Back | $1,210.00 | — | ✗ 2 issues | ✗ 1 issues | 3 |
| Q11 | 24 x cap, printed 2-colour Front — we don't do... | $520.08 | deposit | ✗ 1 issues | ✓ after 1 rev | 1 |
| Q12 | 100 x tote, printed 1-colour Front — the partn... | $847.00 | guarantee+turnaround | ✗ 1 issues | ✓ after 3 rev | 3 |
| Q13 | 300 x tote, printed 2-colour Front | $2,838.00 | — | ✓ | ✓ | 0 |
| Q14 | 20 x tote, printed 1-colour Front (rush job) | $346.50 | — | ✗ 1 issues | ✓ after 1 rev | 1 |
| Q15 | 60 x tee, printed 1-colour Front (rush job) — ... | $866.25 | guarantee+turnaround | ✗ 1 issues | ✓ after 2 rev | 2 |
| Q16 | 400 x hoodie, printed 2-colour Front + 2-colou... | $12,144.00 | — | ✓ | ✓ | 0 |
| Q17 | 400 x cap, printed 1-colour Front — what's the... | $4,433.00 | price | ✓ | ✓ | 0 |
| Q18 | 25 x tee, printed 1-colour Back | $346.50 | — | ✓ | ✓ | 0 |
| Q19 | 150 x hoodie, printed 1-colour Front — we're a... | $3,993.00 | deposit+terms | ✗ 3 issues | ✓ after 3 rev | 3 |
| Q20 | 60 x tote, printed 2-colour Front (rush job) | $891.00 | — | ✗ 1 issues | ✓ after 1 rev | 1 |

## Still failing WITH reflection

### Q01 — 12 x tee, printed 1-colour Front — staff shirts for the new kitchen fit-out ($219.12)

- Issues after 3 revisions: [template] price-table artefact copied into the email (contains 'colour-location(s)') — rewrite it as plain English
- First draft: fail (1 issues)

### Q10 — 100 x cap, printed 1-colour Back ($1,210.00)

- Issues after 3 revisions: [template] price-table artefact copied into the email (contains 'colour-location(s)') — rewrite it as plain English
- First draft: fail (2 issues)

## Fixed by reflection

- **Q03** (150 x tee, printed 2-colour Front — our last printer always ...): first draft failed [[template] price-table artefact copied into the email (contains 'colour-location(s)') — rewrite it as plain English] → fixed in 2 revision(s)
- **Q05** (400 x tee, printed 2-colour Front — if you can beat our curr...): first draft failed [[template] price-table artefact copied into the email (contains 'colour-location(s)') — rewrite it as plain English] → fixed in 2 revision(s)
- **Q06** (25 x hoodie, printed 1-colour Front): first draft failed [[template] price-table artefact copied into the email (contains 'colour-location(s)') — rewrite it as plain English] → fixed in 1 revision(s)
- **Q09** (50 x cap, printed 1-colour Front): first draft failed [[turnaround] must state the turnaround of 8 business days exactly; [template] price-table artefact copied into the email (contains 'colour-location(s)') — rewrite it as plain English] → fixed in 3 revision(s)
- **Q11** (24 x cap, printed 2-colour Front — we don't do deposits — ju...): first draft failed [[clause] missing required clause: new accounts pay a 50% deposit] → fixed in 1 revision(s)
- **Q12** (100 x tote, printed 1-colour Front — the partners want certa...): first draft failed [[template] price-table artefact copied into the email (contains 'colour-location(s)') — rewrite it as plain English] → fixed in 3 revision(s)
- **Q14** (20 x tote, printed 1-colour Front (rush job)): first draft failed [[template] price-table artefact copied into the email (contains 'colour-location(s)') — rewrite it as plain English] → fixed in 1 revision(s)
- **Q15** (60 x tee, printed 1-colour Front (rush job) — we were let do...): first draft failed [[banned] banned phrase used: "guaranteed"] → fixed in 2 revision(s)
- **Q19** (150 x hoodie, printed 1-colour Front — we're an established ...): first draft failed [[turnaround] must state the turnaround of 10 business days exactly; [clause] missing required clause: new accounts pay a 50% deposit; [template] price-table artefact copied into the email (contains 'colour-location(s)') — rewrite it as plain English] → fixed in 3 revision(s)
- **Q20** (60 x tote, printed 2-colour Front (rush job)): first draft failed [[template] price-table artefact copied into the email (contains 'colour-location(s)') — rewrite it as plain English] → fixed in 1 revision(s)

