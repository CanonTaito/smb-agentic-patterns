# P3 — Quote Generator · Pattern 3: Reflection & Self-Improvement

A customer asks for a quote — *"250 hoodies, 3-colour front and back, we need them before the show"* —
and this agent drafts the customer-facing quote email, **checks its own draft against a deterministic
rule critic, and revises every violation it finds**, before a human gate decides whether it can be sent.

This is the **reflection / self-improvement** pattern: *generate → critique against verifiable rules →
revise with the violations fed back → re-check → stop when clean or rounds run out*.

The critic is **code, not the model**: totals are reconciled against the `QuoteEngine` ground truth,
and every business rule (turnaround, the four required clauses, banned phrases, no unfilled
placeholders) is a deterministic check. The model never grades itself.

## Run it

```bash
dotnet run --project src/Patterns.QuoteGenerator                    # demo job → critic → revise → owner gate
dotnet run --project src/Patterns.QuoteGenerator -- --no-reflect    # single-shot draft (reflection off)
dotnet run --project src/Patterns.QuoteGenerator -- --spec <json>   # your own job, e.g. '{"customer":"...","garment":"tee","quantity":50,"locations":[{"location":"Front","colors":2}],"rush":true}'
dotnet run --project src/Patterns.QuoteGenerator -- --auto          # non-interactive (gate auto-approved)
dotnet run --project src/Patterns.QuoteGenerator -- --eval          # 20-spec same-lineage A/B → evals/results/
```

Requires local Ollama with `qwen2.5:3b`. Traces save to `runs/quote-generator/`; every phase is traced.
Quotes over **$2,000** require owner approval at the gate (the demo job sits over it on purpose).

## Architecture

```mermaid
flowchart TD
    S[Job spec] --> D[Draft: model writes the email<br/>figures from QuoteEngine breakdown]
    Q[QuoteEngine<br/>ground truth] --> D
    Q --> C
    D --> C{Rule critic (pure code)<br/>math · turnaround · clauses · template · banned}
    C -->|pass| G{Owner gate<br/>total > $2,000?}
    C -->|violations| R[Revise: violations fed back<br/>≤3 rounds]
    R -->|same violations unfixed| N[Nudge + temperature 0.7]
    N --> R
    R --> C
    R -.->|still failing after 3| F[Final draft + residual issues reported]
    G -->|approved| F
    G -->|rejected| X[Do not send]
    D -.->|JSONL spans| TR[Tracer: runs/quote-generator/]
```

Four phases, each a traced span:

| Phase | What happens | Failure handling |
|---|---|---|
| **draft** | Model writes the email from the spec + price breakdown (temp 0.2) | Empty reply → explicit placeholder draft |
| **critique** | Deterministic rules score the draft (see below) | Violations become numbered feedback |
| **revise** | Full corrected email requested, violations listed verbatim | ≤3 rounds; if a round leaves the **same violations unfixed** → stagnation nudge + temperature 0.7 |
| **gate** | Quotes over $2,000 need owner approval before sending | Approve / reject — rejection exits non-zero |

## What the evals changed in the design

Five rounds of *run eval → design breaks → harden → rerun*:

1. **A critic that cried false alarm** — the terms rule demanded `"terms"`, but valid drafts said
   *"14-day payment term"* (singular). The model did the right thing three revisions running and kept
   being rejected: every "failure" was ours, not its. The pattern now matches `term` (covers both).
2. **The ceiling problem** — with a fair critic, first drafts passed **20/20**: reflection had nothing
   to do. Real inboxes put pressure on quotes, so 8 of 20 specs now carry **customer notes that bait a
   rule**: *"we don't do deposits"*, *"cheapest wins"*, *"our last printer always guaranteed their
   delivery dates"*. That's the adversarial input this pattern exists for.
3. **An A/B that measured draft luck** — the first eval ran every spec *twice* (independent drafts per
   mode), and nondeterministic first drafts made 4 cases "regress" with reflection. Redesigned as a
   **same-lineage ablation**: one run per spec; the first draft is scored as "off" and the final as
   "on". The lift is now exactly what reflection added.
4. **A revise loop that stagnated** — the demo draft opened with `Dear [Customer Name]`, and at
   temperature 0.2 with the same prompt each round, the model returned the **byte-identical draft three
   times** (904 → 904 → 904 chars in the trace). Two fixes: violation messages are now **actionable**
   (`replace it with the customer's name (Westlake Crossfit)`), and a **stagnation detector** arms a
   blunt warning line plus temperature 0.7 whenever a revision leaves the same violations unfixed.
5. **Two rules for defects we actually shipped in testing** — `[Customer Name]` placeholders going out
   to customers, and the engine's own price-table line (`4 colour-location(s) x 200 pcs`) copied
   verbatim into customer-facing prose.

## Eval results (real, live run)

`evals/quote-generator.cases.jsonl` — 20 job specs (Q01–Q20: tees/hoodies/caps/totes, 12–400 units,
1–3 colours, front/back, 4 rush jobs; totals $219–$12,144). **Same-lineage ablation**: each spec runs
once through the full pipeline; the first draft is the "no reflection" score, the final draft is the
"with reflection" score — same draft, same critic.

Latest committed run against `qwen2.5:3b` (draft temp 0.2, stagnation rounds 0.7):

| Metric | No reflection (first draft) | With reflection (final) |
|---|---|---|
| Pass rate (all rules) | **8/20 (40%)** | **18/20 (90%)** |
| Math correct | 20/20 | 20/20 |
| Turnaround correct | 18/20 | 20/20 |
| All clauses present | 18/20 | 20/20 |
| No template artefacts | 10/20 | 18/20 |
| No banned phrases | 19/20 | 20/20 |
| Avg LLM calls / tokens per spec | 1.0 / 613 | 2.3 / 1,837 |

**Pressure cases only** (the 8 baited specs): pass **2/8 → 8/8**; banned phrases clean 7/8 → 8/8;
clauses present 6/8 → 8/8.

Reflection costs **3× the tokens** (12,267 → 36,749 across 20 specs) — but only when a draft is
actually wrong: the 8 specs whose first draft already passed cost zero extra calls. Full case-by-case
table, including every fixed-by-reflection case:
[`evals/results/quote-generator-results.md`](../../evals/results/quote-generator-results.md).

**The honest failures:**

- **Q15 took the bait** — a rush-job customer wrote *"has to be guaranteed"* and the first draft
  answered with `guaranteed` (banned). The critic caught it; two revisions later it was gone.
- **Q11 took the bait** — *"we don't do deposits"* made the draft echo the customer and drop the 50%
  deposit clause (plus the other two payment clauses). Caught and fixed in one revision.
- **Q01 / Q10 still fail after 3 revisions** — the model keeps re-emitting the engine's own
  `colour-location(s)` line despite being told to rewrite it. This is the 3B ceiling on rewriting its
  own source text; see "what I'd do differently".

Across the two pressure-era runs during hardening, with-reflection pass rate was 90–95% and
first-draft pass 35–40% — run-to-run variance from a 3B model is real (P2 documented the same).

## Trace example

`--auto` on the demo job (200 hoodies, rush, $8,085 — over the gate):

```text
DRAFT:
Dear [Customer Name],
...

CRITIQUE round 0: FAIL (1 issue(s))
  [template] unfilled template placeholder left in the email — replace it with the customer's name (Westlake Crossfit)
CRITIQUE: PASS after 1 revision(s)
[gate] AUTO-APPROVED: send quote to Westlake Crossfit — total $8,085.00 exceeds $2,000, owner approval required

trace run 20261021-132126-3fc97f
[agent] quote: Westlake Crossfit, 200 hoodie (7509ms)  total=8085.00 reflection=3 passed=True revisions=1 llmCalls=2 tokens=1527
  [span]  phase: draft (3835ms)  chars=978
    [llm]   llm call (3821ms)  jsonMode=False tools=0 durationMs=3781 promptTokens=405 completionTokens=223
  [span]  phase: revise round 1 (3664ms)  violations=1 chars=980 stillFailing=False stagnated=False
    [llm]   llm call (3661ms)  jsonMode=False tools=0 durationMs=3656 promptTokens=675 completionTokens=224
  [gate]  phase: gate (1ms)  decision=Approved total=8085.00
total: 6 spans, llm tokens 1080 prompt + 447 completion
```

## How to talk about this in an interview

- **"What is reflection?"** — a generate-critique-revise loop where the critique is *verifiable*:
  the total is reconciled against our own quote engine, clauses are string checks, banned words are a
  list. The model never judges its own work — that's the whole trick.
- **"Why not just prompt harder?"** — the rules were in the system prompt and the model still dropped
  the deposit clause when the customer pushed back (Q11) and still wrote `guaranteed` when asked
  directly (Q15). Prompting sets intent; the loop **verifies compliance mechanically every time**, and
  it costs ~1 extra call when it's already right.
- **"How do you avoid a self-check that rubber-stamps?"** — make the critic code. And when the critic
  itself was wrong (the `terms` vs `term` bug), the eval surfaced it as *8 failures no revision could
  fix* — a critic that's unfair to valid output is indistinguishable from a broken pipeline until you
  read the failing drafts.
- **"How do you measure that reflection helps?"** — same-lineage ablation: score the first draft and
  the final draft of the *same run*. Independent runs per mode measure draft luck, not the pattern.
- **"What breaks at scale?"** — token cost is 3× when drafts are wrong; the loop is bounded at 3
  rounds with a stagnation guard so it can't spin; residuals are *reported*, not hidden (Q01/Q10).

## What I'd do differently

- **7B for revision rounds** — drafting is fine at 3B, but rewriting its own price-table line is the
  cliff (Q01/Q10); the revise prompt could route to `qwen2.5:7b` while the draft stays on 3B.
- **Deterministic sanitisation of engine text** — `colour-location(s)` is *our* line item format;
  rendering a customer-facing variant in code before prompting would remove that failure class
  entirely instead of asking the model to rewrite it.
- **Diff-aware stagnation** — currently stagnation compares surviving violation messages; a
  token-level diff between drafts would catch "changed everything except the flagged line" too.
- **Structured output for the email skeleton** — greeting/subject/sign-off as fields, body as prose,
  would make placeholders and sign-off mechanically checkable instead of text-matching.
