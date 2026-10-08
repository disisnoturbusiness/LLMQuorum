---
title: I asked 63 models the same 76 questions, with and without web search
published: false
description: Every question has one short answer checkable against a primary source. The thirteen models asked both ways went from 44.4% to 95.6%. The interesting part is the comparison I almost published instead.
tags: ai, llm, dotnet, testing
canonical_url: https://llmquorum.aidataforager.com
---

The 2026 standard deduction for a single filer is $16,100. I asked 63 models what it was.

Asked from memory, 8 of 61 got it right. 24 invented a number, three of them landing on $8,300. 19 gave a figure from an earlier year. 9 refused to answer. Of the 15 seats that could search the web, 14 got it and the fifteenth errored out.

That is one question. This is what happened across 76 of them, and what I got wrong on the way.

## What you end up with

A .NET 10 service, Hangfire, and one SQL Server database. It works through a plan slowly and in order, one question per provider group per pass, waiting out daily rate limits. It ran for days without supervision. Every raw provider response is stored as it came back, and grading runs afterwards over what was stored.

76 questions. 63 models. 5,776 graded answers. $31.74 of paid API calls.

## 1. What makes a question usable

A question earns its place only if it has a short unambiguous answer and a primary source that states it: the agency, the manufacturer, the law itself. "Explain X" is not a question you can grade at this scale. "What is the USCIS premium processing fee for an H-1B filed on Form I-129" is.

54 of the 76 were picked because the answer changed in the last twelve months. The other 22 are an older set on payroll, tax, .NET and SQL Server, and were not held to that test. Some of them have not moved in years: the federal minimum wage has been $7.25 since 2009, two are arithmetic, and one is how SQL Server handles `NOT IN` against a NULL. Keeping those turned out to be useful, because a model that fails arithmetic is failing differently than a model that is a year behind.

The previous answers are recorded too, in a separate table of known-stale values. That single decision is what makes the output readable:

| Bucket | What it means |
| --- | --- |
| Correct | Matches the verified answer |
| Outdated | Matches a real figure from an earlier year |
| Hallucinated | A number that was never right |
| Refusal | Declined to answer |
| Truncated | Cut off before finishing |
| Error | The call failed |
| Unclassified | The two judges disagreed, so it was not scored |

Outdated and Hallucinated are different failures and collapsing them loses the finding. A model repeating last year's contribution limit is stale. A model inventing $8,300 is not.

## 2. A seat is a model plus how you asked it

A model that can search gets asked twice, once from memory and once with the search tool on. Those are two seats. 63 models therefore produce 76 seats, because only some of them can search.

```
SeatId = {provider}:{model}:{mode}
```

Write it down this way at the start. Everything downstream, every grade, every cost row, keys off the seat rather than the model, and the first time you want to compare modes you will be glad the schema already knows the difference.

## 3. Who grades the answers

Half of them never reach a model. 2,866 of the 5,776 grades are deterministic: the extracted value matches the verified answer, matches a recorded earlier value, or the call itself failed.

The other 2,910 go to a panel of two judges. They are asked to compare two strings, with the verified answer and the known-stale list supplied in the prompt, so it is a comparison task and not a recall task. Both judges returned a verdict on all 2,910, and they agreed on 2,867 of them, 98.5%. Where they split, the answer is bucketed Unclassified and not scored. There are 146 of those.

Judge error is real. Two things bound it. The raw response is the record, so changing a match rule re-grades all 5,776 results with no provider calls. Be precise about the limit though: re-running the extractor over raw bytes is a manual step, and changing the judge prompt or a known answer does re-ask the judges, because the rubric version hashes both. And every judge call is stored with its prompt and its reply, so a grade that looks wrong can be traced to the sentence that produced it.

**The judges are also contestants, and that is a flaw.** The two judge seats are `claude-sonnet-5` and `openai/gpt-oss-120b`, and both are models in the field, with 152 and 76 answers of their own. `claude-sonnet-5` graded 52 of its own answers, `gpt-oss-120b` 45 of its own, and `claude-sonnet-5` is a row in the headline table further down. The deterministic half is untouched by this and panel agreement is high, but a judge should not be sitting the exam. On a re-run the judges come from outside the field.

## 4. Spending real money without trusting your own code

Paid models bill per call, and native web search cannot be bounded per request. The provider passes the search cost through and charges at request finish, so a per-call estimate is a planning figure, not a brake. The money rules therefore sit outside the calling code and assume the calling code is wrong.

- A reservation row is written **before** the request goes out. Any reservation means that seat was asked that question, permanently, whatever it settled at. Nothing re-asks it automatically.
- One send per seat. The only exception is a rejection the provider documents as happening before any model ran, which provably bills nothing.
- **Reconcile.** The key's own lifetime usage at the provider may never exceed what the ledger accounts for. If it does, something billed that was not recorded, and the lane halts until a human clears it.
- The halt is written to the database *and* to a file, so it survives a database that was unreachable at the moment it had to be raised.

The ledger books the larger of the reported cost and its own pricing, so it over-records on purpose. It recorded $38.22 over 1,591 calls. The provider's own meter says $31.74. Over-recording is the correct direction to be wrong in.

## 5. The number I almost published

Here is the headline I had written, with a card made for it and a post drafted:

> From memory the models were right 23% of the time. With web search, 90%. Same models, same questions. The only difference was whether they could look it up.

Every number in that is correct. The sentence is still false.

61 models were asked from memory. 15 seats could search. **Only 13 models were asked both ways.** So 23.1% and 89.6% describe two different populations, and the 15 that can search are mostly the frontier models, which are also the better models from memory. Part of that 66-point gap is the models, not the search.

The comparison that controls for the model is the 13 that were asked both ways, over all 76 questions, 988 answers each side:

| | Correct | Answers | Accuracy |
| --- | ---: | ---: | ---: |
| From memory | 439 | 988 | **44.4%** |
| With web search | 945 | 988 | **95.6%** |

Every one of the 13 improved.

| Model | From memory | With search | Change |
| --- | ---: | ---: | ---: |
| claude-haiku-4-5 | 10.5% | 94.7% | +84.2 |
| gemini-3.5-flash-lite | 21.1% | 93.4% | +72.3 |
| gemini-3.1-pro-preview | 26.3% | 97.4% | +71.1 |
| grok-4.3 | 30.3% | 100.0% | +69.7 |
| gemini-3.8-flash | 32.9% | 98.7% | +65.8 |
| grok-4.6 | 43.4% | 97.4% | +54.0 |
| claude-sonnet-5 | 40.8% | 93.4% | +52.6 |
| claude-opus-5 | 51.3% | 100.0% | +48.7 |
| gpt-5.6-terra | 57.9% | 97.4% | +39.5 |
| gpt-5.6-luna | 59.2% | 97.4% | +38.2 |
| gpt-5.6-sol | 60.5% | 94.7% | +34.2 |
| gpt-6-astra | 78.9% | 98.7% | +19.8 |
| gpt-chat-latest | 64.5% | 80.3% | +15.8 |

One more check, because three of those 13 run through a CLI that takes a system prompt, and theirs changed between modes as well as the tool. Drop those three and the remaining ten go from 47.5% to 95.5%. The result does not rest on the wording.

I had the 23 to 90 version on a social card and most of the way into a post before I checked the denominator.

Every individual number was already checked. Verifying each figure does not verify the claim built out of them, and the question that catches it is dull: what is the denominator on each side, and are the two sides the same population?

## 6. The snapshot that disagreed with the database

There is a second one, and it is worse, because the first mistake at least got caught before anything shipped.

The published site is a frozen snapshot: four JSON files exported from the database, served with no connection string behind them. Good design, one sharp edge. I exported at 19:10 UTC. A re-grade I had kicked off ran from 19:55 to 21:06. Every figure I published that evening was a run in progress, and by the next morning the site, a social card, a LinkedIn post and a Hacker News comment all disagreed with the database they invited the reader to go and check. By a tenth of a point, on a page that says every number here is clickable.

Nothing looked wrong at export time, because nothing was wrong yet.

The fix is not a note to self. The export script now refuses to run:

```powershell
if( $quietFor -lt $QuietMinutes )
{
    throw ( "export-site: the last grade landed $quietFor minutes ago and the gate wants " +
            "$QuietMinutes minutes of quiet. Grading is probably still in flight, and a snapshot " +
            'taken now will disagree with the database the moment it finishes.' )
}

if( $openWork -gt 0 )
{
    throw "export-site: $openWork plan items are still open, so more answers are coming."
}
```

If you publish a snapshot of a live system, the staleness check belongs in the thing that takes the snapshot. A rule you have to remember is a rule you will skip on the evening you are in a hurry to publish.

## 7. Things worth poking at

- **Judge error, and judges in the field.** Covered in section 3. The second one is a design mistake, not a rounding error.
- **One seat dragged the population average down.** A free-tier model's search tool failed on 66 of its 76 calls. That may be my configuration as much as their model, but the calls errored either way. It is why the published figures come in two bases: counting every call attempted, and counting only answers that came back. Both are on the site.
- **Two seats are search-only** and therefore not in the paired comparison at all.
- **Subscription seats are not free.** Seven seats across four models run on a flat-rate plan rather than per-call billing, so they are not in the $31.74. A table showing them at $0.00 next to metered seats is lying by omission, so they are labelled.

## What I would do differently

Model the seat before you write anything else. Store raw responses from the first call, not the first time you wish you had them. Record the previous answers along with the current one, because wrong and a year behind are different numbers and you only get to tell them apart if you decided to up front. Pick judges from outside the field.

And write the denominator next to every percentage, in the schema, where you cannot avoid looking at it.

Code, raw data and all 5,776 graded answers: [github.com/disisnoturbusiness/LLMQuorum](https://github.com/disisnoturbusiness/LLMQuorum)

Every number here is clickable at [llmquorum.aidataforager.com](https://llmquorum.aidataforager.com).
