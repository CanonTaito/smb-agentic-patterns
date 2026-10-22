# Eval results — P4 Inbox Triage (pattern 4: multi-agent routing & hand-off)

- Run date: 2026-10-22 21:47 (local, live Ollama)
- Router model: `qwen2.5:3b`; embedding router: `nomic-embed-text` (cosine vs 15 hand-written prototypes)
- Method: each of the 30 messages runs ONCE through the full pipeline (LLM router → specialist with a
  category-scoped tool belt or a clarifying reply → human gate, auto-approved) and is scored against the
  inbox labels. The same 30 are also classified by the embedding router — same labels, no LLM — for a
  head-to-head routing comparison.
- Cases: 30 (data/inbox.jsonl), of which 6 are labelled ambiguous (mixed or vague on purpose).
- **Handled** = routed to the right department with a non-empty gated reply (clear messages), or a clarifying
  reply was sent (ambiguous messages). Wrong department = not handled, even if the reply sounds nice.
- Gate: every reply passes the human gate; in the eval it is auto-approved, except quotes at/over the
  $2,000 owner-approval threshold, which are escalated (held) and still count as handled.

## Aggregate

| Metric | Result |
|---|---|
| LLM router accuracy | **27/30 (90%)** |
| Embedding router accuracy | **26/30 (87%)** |
| Handled (right dept or clarifying) | **27/30 (90%)** |
| Ambiguous → clarifying reply | 3/6 |
| Clear → wrongly clarified (over-ask) | 0/24 |
| Replies gated | 30/30 (auto-approved 29, escalated 1, rejected 0) |
| Avg LLM calls per message | 3.3 |
| Avg tokens per message | 2,730 |
| Total tokens (30 messages) | 81,894 |
| quote messages that called price_quote | 6/7 |
| $ figures in quote replies traced to price_quote | 6/6 |
| embedding scores clearing the 0.7 clarify threshold | 0/30 |

## LLM router by expected category

| Expected category | n | Correct |
|---|---|---|
| artwork_change | 5 | 5 |
| invoice_payment | 7 | 7 |
| order_status | 5 | 5 |
| quote_request | 7 | 7 |
| unclear | 6 | 3 |

## Embedding vs LLM router (same 30 labels)

| Expected | LLM correct | Embedding correct |
|---|---|---|
| artwork_change | 5/5 | 4/5 |
| invoice_payment | 7/7 | 7/7 |
| order_status | 5/5 | 5/5 |
| quote_request | 7/7 | 6/7 |
| unclear | 3/6 | 4/6 |

### Where the embedding router is wrong but the LLM router is right

- **M008** "Proof approval - job 1009" (expected artwork_change): embed chose `order_status` (cosine 0.354)
- **M011** "Can we add 20 shirts to job 1007?" (expected quote_request): embed chose `unclear` (cosine 0.304)

## Per-message

| ID | Expected | Amb. | LLM route | Embed route | Mode | Handled | Gate | Tokens |
|---|---|---|---|---|---|---|---|---|
| M001 | quote_request |  | quote_request✓ | quote_request✓ | routed | ✓ | Approved | 5907 |
| M002 | order_status |  | order_status✓ | order_status✓ | routed | ✓ | Approved | 2323 |
| M003 | invoice_payment |  | invoice_payment✓ | invoice_payment✓ | routed | ✓ | Approved | 2253 |
| M004 | artwork_change |  | artwork_change✓ | artwork_change✓ | routed | ✓ | Approved | 2215 |
| M005 | quote_request |  | quote_request✓ | quote_request✓ | routed | ✓ | Escalated | 3066 |
| M006 | invoice_payment |  | invoice_payment✓ | invoice_payment✓ | routed | ✓ | Approved | 4332 |
| M007 | quote_request |  | quote_request✓ | quote_request✓ | routed | ✓ | Approved | 2980 |
| M008 | artwork_change |  | artwork_change✓ | order_status✗ | routed | ✓ | Approved | 2217 |
| M009 | order_status |  | order_status✓ | order_status✓ | routed | ✓ | Approved | 2282 |
| M010 | invoice_payment |  | invoice_payment✓ | invoice_payment✓ | routed | ✓ | Approved | 5215 |
| M011 | quote_request |  | quote_request✓ | unclear✗ | routed | ✓ | Approved | 2819 |
| M012 | artwork_change |  | artwork_change✓ | artwork_change✓ | routed | ✓ | Approved | 2240 |
| M013 | invoice_payment |  | invoice_payment✓ | invoice_payment✓ | routed | ✓ | Approved | 4318 |
| M014 | quote_request |  | quote_request✓ | quote_request✓ | routed | ✓ | Approved | 2968 |
| M015 | order_status |  | order_status✓ | order_status✓ | routed | ✓ | Approved | 2278 |
| M016 | artwork_change |  | artwork_change✓ | artwork_change✓ | routed | ✓ | Approved | 2243 |
| M017 | invoice_payment |  | invoice_payment✓ | invoice_payment✓ | routed | ✓ | Approved | 2379 |
| M018 | order_status |  | order_status✓ | order_status✓ | routed | ✓ | Approved | 2243 |
| M019 | quote_request |  | quote_request✓ | quote_request✓ | routed | ✓ | Approved | 3333 |
| M020 | invoice_payment |  | invoice_payment✓ | invoice_payment✓ | routed | ✓ | Approved | 3215 |
| M021 | unclear | ✓ | unclear✓ | unclear✓ | clarified | ✓ | Approved | 775 |
| M022 | unclear | ✓ | artwork_change✗ | unclear✓ | routed | ✗ | Approved | 2109 |
| M023 | unclear | ✓ | unclear✓ | unclear✓ | clarified | ✓ | Approved | 748 |
| M024 | unclear | ✓ | order_status✗ | artwork_change✗ | routed | ✗ | Approved | 3460 |
| M025 | unclear | ✓ | quote_request✗ | quote_request✗ | routed | ✗ | Approved | 2942 |
| M026 | quote_request |  | quote_request✓ | quote_request✓ | routed | ✓ | Approved | 3534 |
| M027 | artwork_change |  | artwork_change✓ | artwork_change✓ | routed | ✓ | Approved | 2224 |
| M028 | invoice_payment |  | invoice_payment✓ | invoice_payment✓ | routed | ✓ | Approved | 2257 |
| M029 | order_status |  | order_status✓ | order_status✓ | routed | ✓ | Approved | 2234 |
| M030 | unclear | ✓ | unclear✓ | unclear✓ | clarified | ✓ | Approved | 785 |

## Not handled

### M022 — expected unclear (ambiguous)

- LLM router said `artwork_change` (conf 1.00) → mode routed, gate Approved

### M024 — expected unclear (ambiguous)

- LLM router said `order_status` (conf 1.00) → mode routed, gate Approved

### M025 — expected unclear (ambiguous)

- LLM router said `quote_request` (conf 1.00) → mode routed, gate Approved

## Replies with a $ figure that never called price_quote

None — every dollar figure in a quote reply came out of price_quote.

