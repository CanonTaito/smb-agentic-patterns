# P4 — Inbox Triage · Pattern 4: Multi-Agent Routing & Hand-off

Thirty customer emails land in `info@inkline.com.au` — quotes, "where's my job?", invoice queries,
proof changes, and six that are deliberately **two questions wearing one coat** (*"reorder the winter
hoodies… also there's an invoice I don't recognise"*). One router decides **who answers**, four
department specialists answer **inside a tool belt they're allowed to touch**, and every reply —
clarifying or not — passes a **human gate** before it would ever send.

This is the **routing / hand-off** pattern: *classify → hand to a specialist with least-privilege
tools and routing context → gate the outgoing reply*. The interesting parts are what happens when
the router is **unsure** (a clarifying reply instead of a confident half-answer), when the router
is **cheap** (an embedding classifier that costs zero LLM tokens), and when a reply is **worth real
money** (quotes ≥ $2,000 are held for the owner even in non-interactive runs).

## Run it

```bash
dotnet run --project src/Patterns.InboxTriage                    # demo: M014 rush quote → specialist → gate
dotnet run --project src/Patterns.InboxTriage -- --msg M003      # triage any message from data/inbox.jsonl
dotnet run --project src/Patterns.InboxTriage -- --msg M021 --auto   # non-interactive (gate auto-approved)
dotnet run --project src/Patterns.InboxTriage -- --router embed  # route with embeddings instead of the LLM
dotnet run --project src/Patterns.InboxTriage -- --eval          # 30-message eval → evals/results/
```

Requires local Ollama with `qwen2.5:3b` (router/specialists) and `nomic-embed-text` (embedding
router). Traces save to `runs/inbox-triage/`.

## Architecture

```mermaid
flowchart TD
    M[Customer email<br/>data/inbox.jsonl] --> R{Router<br/>LLM JSON or embedding cosine<br/>confidence vs 0.7}
    R -->|"unclear / conf < 0.7"| C[Clarifying reply<br/>one focused question set]
    R -->|quote_request| SQ[Quote specialist<br/>price_quote · lookup_job · get_today]
    R -->|order_status| SS[Status specialist<br/>lookup_job · list_jobs · lookup_customer]
    R -->|invoice_payment| SI[Accounts specialist<br/>lookup_customer · get_invoice · list_invoices]
    R -->|artwork_change| SA[Artwork specialist<br/>lookup_job · check_proof_status]
    SQ --> G
    SS --> G
    SI --> G
    SA --> G
    C --> G{Human gate<br/>quote ≥ $2,000 → ESCALATE (hold)}
    G -->|approved / edited| SENT[Reply sent]
    G -->|escalated| HOLD[HELD for owner]
    G -->|rejected| X[Not sent]
    R -.-> TR[Tracer: JSONL spans<br/>runs/inbox-triage/]
```

| Stage | What happens | Failure handling |
|---|---|---|
| **route** | LLM structured output → `{category, confidence}` (5 categories incl. `unclear`), or embedding argmax over 15 hand-written prototypes | Invalid JSON/category → retry with the validation error; embedding errors → eval records them, pipeline falls back to LLM router |
| **clarify** | `unclear` or conf < 0.7 → a short reply that shows what we understood and asks them to split/confirm | Never routes to a department — a half-answer is treated as a failure |
| **specialist** | `AgentLoop` with a **category-scoped tool belt** (least privilege: accounts can't quote, quotes can't see invoices) + a router hand-off hint | ≤6 steps, loop detection, structured tool errors |
| **gate** | Every reply passes `HumanGate`; quotes ≥ $2,000 → `EscalateAsync` (held, `ShouldProceed=false`) even with `--auto` | approve / edit / reject / escalate — only approve+edit send |

## What the evals changed in the design

Four rounds of *run eval → design breaks → harden → rerun*:

1. **The invented price** — the first demo specialist answered *"Rush cost is $200"* for 30 tees
   without ever calling `price_quote` (the engine says $511.50), then a later attempt invented
   *"5 business days"* from thin air. Prompt rules alone didn't fix it. Fixes: a **few-shot tool
   example** in the quote prompt, permission to price a **baseline assumption** (1 colour, Front)
   instead of demanding details, and a **router hand-off hint** in the task (`quote_request → price
   it before replying`) — routing context travelling with the request, which is the pattern's whole
   point. Plus an eval metric that measures what we fear: *$ figures traced to `price_quote`* —
   now **6/6**.
2. **The router that never said "unclear"** — first run: **0/6 ambiguous** messages clarified; the
   router force-routed every mixed email to a department (24/30 overall, all misses on ambiguous).
   Fix: mixed/vague **few-shot examples** in the router prompt with *"answering only half their
   email is a failure"*. Result: 3/6 clarified, **27/30 overall** — the three residual misses
   (M022/M024/M025) were left alone rather than adding their shapes to the prompt, which would be
   fitting to 30 eval cases.
3. **The embedding router that ranked everything `quote_request`** — first embedding run scored
   **7/30**, every message collapsing onto the first prototype. My first diagnosis (embedding
   anisotropy) was wrong: Ollama 0.40.7's legacy `/api/embeddings` endpoint returns
   `{"embedding":[]}` — **zero-length vectors**, so every cosine was exactly 0.0 and argmax picked
   prototype #1 every time. Fixed in core: post to **`/api/embed`**, parse `embeddings[0]`, **throw
   on empty** instead of silently returning `float[0]`, with wire-contract tests. Mean-centring
   (subtract the prototype mean before comparing) stayed as a principled improvement for
   anisotropic embedding spaces. Result: **26/30 (87%)**.
4. **The calibration gap** — embedding *ranking* is now good, but raw centred cosine rarely clears
   the LLM-tuned **0.7 clarify threshold (0/30)**. Confidence scales are not interchangeable:
   the eval now reports this explicitly, and `--router embed` demonstrates it live (correct
   argmax, clarify-heavy behaviour). Per-router threshold calibration is on the backlog.

## Eval results (real, live run)

`data/inbox.jsonl` — 30 labelled messages (M001–M030; 7 quote, 5 status, 7 invoice, 5 artwork,
**6 ambiguous**). Each message runs **once** through the full pipeline (LLM router → specialist or
clarify → gate, auto-approved); the same 30 are classified by the embedding router — same labels,
no LLM — for the head-to-head.

Latest committed run (`qwen2.5:3b` router + specialists, `nomic-embed-text` embeddings):

| Metric | Result |
|---|---|
| **LLM router accuracy** | **27/30 (90%)** — clear 24/24, ambiguous 3/6 |
| **Embedding router accuracy** | **26/30 (87%)** — zero LLM calls |
| **Handled** (right dept or clarifying) | **27/30 (90%)** |
| Ambiguous → clarifying reply | 3/6 |
| Clear → wrongly clarified (over-ask) | **0/24** |
| Replies gated | 30/30 — approved 29, **escalated 1**, rejected 0 |
| `price_quote` called on quote messages | 6/7 (M001 asks for polos — correctly declined) |
| $ figures traced to `price_quote` | **6/6** |
| Embedding scores clearing the 0.7 threshold | 0/30 (calibration gap) |
| Avg LLM calls / tokens per message | 3.3 / 2,730 (total **81,894** for 30) |

Per-category (LLM): quote 7/7 · invoice 7/7 · status 5/5 · artwork 5/5 · **unclear 3/6**.
Full case-by-case table, disagreements and raw records:
[`evals/results/inbox-triage-results.md`](../../evals/results/inbox-triage-results.md).

**The honest failures:**

- **M022** (*"attached updated artwork — confirm + when's delivery?"*) routed to `artwork_change`;
  **M024** (*"make sure Friday's batch is right, can't afford a reprint"*) → `order_status`;
  **M025** (*"staff kits — what would that look like, on our existing account?"*) → `quote_request`.
  Each answers half the email — exactly what `unclear` exists to prevent. The router commits with
  confidence 1.00, so the threshold can't catch it: **single-label routing struggles with
  multi-intent mail** (see "what I'd do differently").
- **Embedding vs LLM disagreements** — M008 (*"proof approved"*) read as `order_status` (0.354) and
  M011 (*"add 20 shirts + updated quote"*) as `unclear` (0.304). Note the embedding router
  *out-scored* the LLM on M022 (caught `unclear`) — neither router dominates.
- **0/30 embedding confidences clear 0.7** — argmax accuracy without calibrated confidence; with
  `--router embed` almost every message clarifies. Correct ranking, unusable threshold.

## Trace example

`--auto` on M005 (150 hoodies, 2-colour front + 1 back) — routed, priced by tool, **escalated**:

```text
  route: quote_request conf 1.00 (llm) — request for a quote for 150 hoodies with specific design and size requirements
  → quote_request specialist: 2 steps, tools: price_quote (Completed)
[gate] ESCALATED to owner: quote of $4,587 is at/over the $2,000 owner-approval threshold — held, not sent
        held — not sent until the owner reviews it.

================ REPLY (routed) ================
Here is your quote for 150 Hoodies with 2 colors front and 1 color back. The total cost, including GST, is $4587.00. The turnaround is 10 business days. ...

priced total: $4,587.00 (owner threshold $2,000)
route: quote_request (1.00) via llm | gate: Escalated | 3 llm calls | 2717 tokens | 3110 ms

trace run 20261022-113416-f49edb
[agent] triage M005: Quote needed: 150 hoodies 2-colour (3106ms)  router=llm category=quote_request confidence=1 clarify=False mode=routed gate=Escalated specialistSteps=2 stopReason=Completed quoteTotal=4587.00
  [span]  route: classify (899ms)  category=quote_request confidence=1
    [llm]   llm call (880ms)  jsonMode=True tools=0 durationMs=872 promptTokens=294 completionTokens=33
  [agent] agent: Reply to this customer email. From: owner@fernycoveb... (2174ms)  maxSteps=6 stopReason=Completed
    [llm]   llm call (step 1) (1043ms)  toolCalls=1
    [tool]  tool: price_quote (15ms)  args={"quantity":150,"locations":[{"colors":2,"location":"Front"},{"colors":1,"location":"Back"}],"rush":false,"garment":"hoodie"} ok=True
    [llm]   llm call (step 2) (1110ms)  toolCalls=0
  [gate]  gate (1ms)  decision=Escalated shouldProceed=False quoteTotal=4587.00
total: 8 spans, llm tokens 2560 prompt + 157 completion
```

The quote matches `QuoteEngine.Quote` to the cent ($3,360 garments + $90 screens + $720 print + $417 GST = **$4,587.00**, 10 business days) — the specialist can't do arithmetic it didn't take from the tool.

## How to talk about this in an interview

- **"What is routing vs. a single agent?"** — one loop with ten tools answers *any* request, but
  every prompt pays for every tool description and any tool is reachable by accident. Here the
  router picks a specialist whose system prompt and **tool belt are scoped to its department**
  (least privilege: accounts physically can't quote, quotes can't see invoices) — smaller prompts,
  blast-radius limits, and a natural place to put per-department policy.
- **"What happens when the router is unsure?"** — it doesn't force a department. `unclear` (or
  conf < 0.7) sends a *clarifying reply that shows what we understood* — because routing a mixed
  email to one department means **answering half the email**, which the eval scores as not handled.
  The flip side is measured too: **0/24** clear messages were wrongly clarified.
- **"LLM router or embedding router?"** — measured, not vibed: **90% vs 87%** on the same labels.
  Embeddings cost zero LLM tokens — one small embedding call per message, no chat — but the LLM
  router is the one that emits a **confidence you can threshold** (and its 3 wins on ambiguous mail
  are why it drives the pipeline). The embedding router's real finding is calibration: great
  ranking, **0/30** scores clear the 0.7 threshold — accuracy and confidence are different
  properties.
- **"How do you stop a wrong route from doing damage?"** — three layers: structured output with
  validation+retries at the boundary; least-privilege belts so a wrong specialist *can't* touch
  the wrong data; and the human gate on every outgoing reply, with **$2,000 quotes escalated even
  in `--auto`** (M005 proves it live — held, not sent).
- **"How do you know the agents aren't making numbers up?"** — you measure it: an eval metric
  checks every $ figure in a quote reply traces to a `price_quote` call (**6/6**), because the
  first version of this agent *did* invent "$200" before the eval existed.

## What I'd do differently

- **Calibrate per-router thresholds** — 0.7 means "fairly sure" for an LLM and "basically never"
  for centred cosine. Fit thresholds per router on held-out data (or use top-vs-runner-up margin
  for embeddings) instead of sharing one constant.
- **Route intents, not messages** — the 3 misses are multi-intent mail. The real design is
  *decompose → route each intent → merge replies*, which fixes M022/M024/M025 at the architecture
  level instead of by adding eval shapes to a prompt.
- **Enforce grounding in code, not just measure it** — a gateway check ("reply contains `$` but
  no successful `price_quote` → hold") would make fabricated prices impossible rather than merely
  counted.
- **Prototype banks don't scale** — 15 hand-written sentences were fine for 5 categories; at 30+
  categories the embedding router wants labelled exemplars (or a cross-encoder rerank) per class.
- **Bigger router for the `unclear` class** — the misses all commit at confidence 1.00; the
  `unclear` decision may need its own specialist check (a second "is this really single-intent?"
  pass) rather than one flat softmax-style classification.
