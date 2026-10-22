# smb-agentic-patterns

Five production-shaped AI agent patterns in C#/.NET, running **fully local on Ollama** — built around a fictional screen-printing business, *Inkline Print Co.*, with eval suites and traces.

> **Status:** P1–P4 shipped — each with live eval results, traces, and its own README. P5 (Owner Briefing) and the full pattern map are next.

| # | Pattern | Project | Status |
|---|---------|---------|--------|
| 1 | Tool use / ReAct | [Ops Assistant](src/Patterns.OpsAssistant) — 7 data tools, loop detection, budgets | ✅ **18/20 eval** — [results](evals/results/ops-assistant-results.md) |
| 2 | Planning | [Weekly Ops Report](src/Patterns.WeeklyReport) — human-gated plan, hybrid self-check | ✅ **9/10 plan · 6/10 full pass** — [results](evals/results/weekly-report-results.md) |
| 3 | Reflection / critic | [Quote Generator](src/Patterns.QuoteGenerator) — rule critic, stagnation-safe revise loop | ✅ **18/20 eval** — [results](evals/results/quote-generator-results.md) |
| 4 | Routing | [Inbox Triage](src/Patterns.InboxTriage) — LLM vs embedding routers, scoped specialists, gates | ✅ **27/30 eval** — [results](evals/results/inbox-triage-results.md) |
| 5 | Multi-agent / supervisor-worker | Owner Briefing | coming soon |

## Why

Most agent demos are notebooks with no evals, no traces, and no failure handling. This repo builds each of the five core agentic design patterns **by hand** — no agent frameworks — with the engineering the guide says production demands: structured tool errors, budgets, loop detection, human approval gates, and measurable evals.

## License

MIT
