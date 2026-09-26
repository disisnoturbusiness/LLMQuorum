# Paid lane, review round 5 (2026-09-18)

The round-4 redesign has been built and deployed (226/226 tests pass). Paid seats are still disabled, no paid key is saved, and nothing has spent.
Six agents reviewed it from three angles: the code's money paths, the plan's behaviour, and OpenRouter's own docs. Each finding was then checked by a second agent that tried to refute it.

## Verdict: the design is dead. The OpenRouter docs check killed it.

Native web search (OpenAI, Google, xAI) cannot be bounded per call. Everything below comes from OpenRouter's web-search doc (openrouter.ai/docs/guides/features/server-tools/web-search.md), confirmed by me:
- Native search cost is "Passed through from the provider".
- `max_results` is "ignored with native provider search".
- `max_uses` is "forwarded only to Anthropic; other native search providers ignore it".
- `search_context_size` is also ignored for native search.

The limits doc also says OpenRouter charges a request when it finishes. So neither the key limit nor the balance can stop a call that is already running. Every brake in rounds 1-5 was an estimate, or an after-the-fact check, laid over a cost that has no ceiling. That is why each round found new holes.

## Confirmed holes (after the refute pass)

Money:
1. **One native-search call can overshoot everything.** An astra-web call billing more than about $10.82 breaks the $25 cap, more than $13.82 breaks the $28 key limit, and about $15.76 empties the balance. It would take about 700k search tokens, which fits in the 1.05M context.
2. **A timed-out call is never checked against its estimate.** OpenRouter bills the full response even after we disconnect. The "over estimate, halt" rule never fires for it, which is exactly the likeliest runaway. KeyUsageBefore is stored but never read.
3. **A halt is lost when SQL is down at that moment.** The next run then sends normally.

Stuck:
4. **Balance-margin refusal loops.** It retries every 6 hours forever and blocks every later question while cap is left.
5. **Temporary holds become permanent refusals.** A 30-minute hold on an "unknown" row can make later seats' "dollar cap" refusal permanent.
6. **No `openrouter-paid` plan rows exist yet.** Expected: the pilot plan was going to create them.

Record and minor:
7. **Interrupted calls are never reported.** A call killed mid-flight leaves a "reserved" row forever, and no note names the seat.
8. **The note's "void the row to re-ask" advice doesn't work** for Timeout/Network seats.
9. **A post-sweep crash writes the wrong note.** The item is rewritten as "no enabled seats left".
10. **Voided seats are re-sent.** Voided 404 seats go out again whenever the item reruns for another seat.
11. **A key with a reset date re-sweeps.** It triggers a refused 21-seat sweep every 6 hours instead of a quiet wait.
12. **The docs disagree on the unmet-pin code** (404 vs 503). Only 404 is voided.

Latent, affects free seats too:
13. **Partial errors are graded as answers.** OpenRouter documents non-streaming errors as `finish_reason: "error"` with partial content in `choices`. The extractor grades that as Answered. Checked the DB: 0 such rows so far, so no stored data is affected.

Refuted: /credits needing a management key (it works live with a regular key, and fails closed anyway). Also refuted: the pin check field name.

## Ways forward (Danny decides)

A. **Web seats use OpenRouter's Exa search instead of each vendor's native search.**
   - OpenRouter caps it: `max_results`, `max_total_results`, `search_context_size` (5K characters per result at `low`), and the tool-loop stops.
   - Cost: $0.007 per search, 10 results included.
   - Every call gets a real maximum cost, so the cap becomes a hard bound instead of a guess.
   - Trade-off: every model reads the same Exa results rather than its own vendor's search. Arguably a fairer test of reasoning, but a different test.
   - Sonar stays native: Perplexity search is built in and bounded by its context window.

B. **Keep native search** and treat the OpenRouter balance itself as the ceiling (up to the ~$30 on the account).
   - Needs auto top-up confirmed OFF.
   - Simpler code, but one runaway call can drain the balance and leave the free models on 402 until a top-up.
