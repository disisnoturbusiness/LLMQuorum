# Verification of the LinkedIn post, 2026-10-05

Eleven claims checked, each from its own evidence path, with three adversarial challengers on anything
that did not come back clean. 26 agents, 370 tool calls.

## Confirmed, no change needed

| Claim | Evidence |
| --- | --- |
| 63 models, 76 questions, 76 seats, 5,776 graded answers, no gaps | `quorum.SeatGrade` |
| 2026 standard deduction for a single filer is $16,100 | IRS Rev. Proc. for TY2026 |
| Question 19: 22 correct, 24 hallucinated, 19 outdated, 9 refused of 76 | `quorum.SeatGrade` |
| Three hallucinations landed on $8,300 | stored answer text |
| 23.1% memory / 89.8% web, 23.9% / 96.0% of answers returned | DB, site JSON, RESULTS.md all agree |
| Every question carries a named primary source | `quorum.Question` |
| $31.74 of metered API calls | **live read of OpenRouter `GET /api/v1/key`: usage = 31.74317** |

The ledger records $38.22 over 1,591 settled calls because it books the larger of the reported cost and
its own pricing. Both numbers are published, which is correct.

## Problem 1: the comparison is not controlled (this is the serious one)

The post said:

> Same models, same questions, same wording. The only difference was whether they could look it up.

That is not what the run measured.

- 61 models were asked from memory.
- 15 seats could search.
- **Only 13 models were asked both ways.**

So 23% and 90% are two different populations. The 15 that can search are mostly the frontier models,
which are also the better models from memory. Part of the 67-point gap is the models, not the search.

The controlled comparison, the 13 models asked both ways on all 76 questions (988 runs each side):

| | Correct | Runs | Accuracy |
| --- | ---: | ---: | ---: |
| From memory | 440 | 988 | **44.5%** |
| With web search | 948 | 988 | **96.0%** |

Every one of the 13 improved. Not one got worse.

| Model | From memory | With search | Change |
| --- | ---: | ---: | ---: |
| claude-haiku-4-5 | 10.5% | 94.7% | +84.2 |
| google/gemini-3.5-flash-lite | 21.1% | 93.4% | +72.3 |
| google/gemini-3.1-pro-preview | 26.3% | 97.4% | +71.1 |
| x-ai/grok-4.3 | 30.3% | 100.0% | +69.7 |
| google/gemini-3.8-flash | 32.9% | 98.7% | +65.8 |
| claude-sonnet-5 | 40.8% | 93.4% | +52.6 |
| x-ai/grok-4.6 | 43.4% | 97.4% | +54.0 |
| claude-opus-5 | 51.3% | 100.0% | +48.7 |
| openai/gpt-5.6-terra | 57.9% | 97.4% | +39.5 |
| openai/gpt-5.6-luna | 59.2% | 97.4% | +38.2 |
| openai/gpt-5.6-sol | 61.8% | 96.1% | +34.3 |
| openai/gpt-chat-latest | 64.5% | 81.6% | +17.1 |
| openai/gpt-6-astra | 78.9% | 100.0% | +21.1 |

The paired number is the stronger claim, not the weaker one. 44.5 to 96.0 on the same thirteen models is
a result. 23 to 90 across two different populations is an artifact with a result inside it.

## Problem 2: the opening paragraph blends the two conditions

Question 19 by mode:

| | Correct | Hallucinated | Outdated | Refused | Truncated | Error |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| From memory (61) | **8** | 24 | 19 | 9 | 1 | 0 |
| With search (15) | **14** | 0 | 0 | 0 | 0 | 1 |

"22 of 76 runs got it right" is arithmetically true and reads as if 22 models knew it. From memory,
**8 of 61** knew it. Of the ones that could look it up, 14 of 15 got it and the fifteenth errored.

8 of 61 is the harder number and it is the true one.

## Problem 3: "each one changed in the last year" covers only 54 of the 76

- Public set v3, 54 questions: selected because the answer changed within twelve months.
  `quorum.QuestionSet.Notes` says so, as does the header of `Schema/21_PublicSetV3.sql`.
- Public set v2, 22 questions: no recency test. `Schema/09_PublicSetV2.sql` has no such criterion and the
  README limits the requirement to "the newer set".

Set v2 contains questions whose answers have not moved in years: the federal minimum wage at $7.25
(unchanged since 2009), two arithmetic questions, and how SQL Server handles `NOT IN` with NULL.
By category, v2 is 10 volatile-fact, 8 settled-fact, 3 arithmetic, 1 closed-set.

Supportable: **54 of the 76 were chosen because the answer changed in the last twelve months.**

## Problem 4: the replay sentence is slightly wide

Raw provider responses are stored and grading runs after the fact over the stored answers, so a rule-tier
change re-grades all 5,776 with no provider calls. Two narrower points:

- Grades are built from the stored `AnswerText` / `Status` / `IsRefusal` columns, which are a projection
  of the raw bytes rather than the bytes themselves. The extractor replays cleanly over the bytes, but
  re-running it is a manual step.
- A change to the judges' prompt or to the known answers does re-ask the judge models.

Defensible: every raw response is stored, and re-grading runs over what came back instead of asking again.

## Disposition

The 23% and 90% figures on the card and the site are correct as statements about those seats. What does
not survive is the sentence claiming it was the same models both ways. That line is on the card, on the
site and in the README, so it is fixed in all four places, not just in the post.
