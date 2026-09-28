# Results

_Snapshot: 2026-09-28. The free-tier lane keeps running, so counts grow; the percentages hold._

Generated from the database by `ops/write-results-md.py`. Nothing here is typed by hand, and every
figure can be clicked through at [llmquorum.aidataforager.com](https://llmquorum.aidataforager.com).

**63 models, 76 seats, 76 questions, 5418 graded answers.** A seat is a model plus how it
was asked, so a model asked both from memory and with web search counts as two.

## The finding

| How it was asked | Seats | Answers | Correct | Made up | Stale | Accuracy |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| With web search | 15 | 1133 | 1024 | 19 | 18 | **90.4%** |
| From memory | 61 | 4285 | 1026 | 1857 | 811 | **23.9%** |

Same models, same questions, asked in the same words. The only difference is whether the model could
look the answer up.

## Best seats

| Model | Asked as | Provider | Questions | Correct | Accuracy |
| --- | --- | --- | ---: | ---: | ---: |
| claude-opus-5 | web | claude-sub | 76 | 76 | 100.0% |
| openai/gpt-6-astra | web | openrouter-paid | 76 | 76 | 100.0% |
| x-ai/grok-4.3 | web | openrouter-paid | 76 | 76 | 100.0% |
| google/gemini-3.8-flash | web | openrouter-paid | 76 | 75 | 98.7% |
| google/gemini-3.1-pro-preview | web | openrouter-paid | 76 | 74 | 97.4% |
| openai/gpt-5.6-luna | web | openrouter-paid | 76 | 74 | 97.4% |
| openai/gpt-5.6-terra | web | openrouter-paid | 76 | 74 | 97.4% |
| x-ai/grok-4.6 | web | openrouter-paid | 76 | 74 | 97.4% |
| openai/gpt-5.6-sol | web | openrouter-paid | 76 | 73 | 96.1% |
| claude-haiku-4-5 | web | claude-sub | 76 | 72 | 94.7% |
| perplexity/sonar | web | openrouter-paid | 76 | 72 | 94.7% |
| claude-sonnet-5 | web | claude-sub | 76 | 71 | 93.4% |

## Worst seats

| Model | Asked as | Provider | Questions | Correct | Accuracy |
| --- | --- | --- | ---: | ---: | ---: |
| groq/compound-mini | web | groq | 69 | 4 | 5.8% |
| command-r7b-12-2024 | memory | cohere | 76 | 6 | 7.9% |
| glm-4.6v-flash | memory | zai | 76 | 6 | 7.9% |
| @cf/ibm-granite/granite-4.0-h-micro | memory | cloudflare | 57 | 5 | 8.8% |
| claude-haiku-4-5 | memory | claude-sub | 76 | 8 | 10.5% |
| poolside/laguna-xs-2.1:free | memory | openrouter | 76 | 8 | 10.5% |
| @cf/meta/llama-3.2-3b-instruct | memory | cloudflare | 57 | 6 | 10.5% |
| allam-2-7b | memory | groq | 69 | 8 | 11.6% |
| command-a-plus-05-2026 | memory | cohere | 76 | 9 | 11.8% |
| poolside/laguna-s-2.1:free | memory | openrouter | 76 | 9 | 11.8% |
| command-a-reasoning-08-2025 | memory | cohere | 76 | 10 | 13.2% |
| command-r-08-2024 | memory | cohere | 76 | 10 | 13.2% |

## Hardest questions

| Topic | Question | Answer | Correct | Made up | Stale |
| --- | --- | --- | ---: | ---: | ---: |
| State withholding certificates | List the marital status options printed on the current Georgia Form G-4 employee withholding ce... | `Single; Married Filing Separate or Married Filing Joint, both spouses working; Married Filing Joint, one spouse working; Head of Household` | 9 of 76 | 52 | 9 |
| State withholding certificates | List the marital status options printed on the current Indiana Form WH-4 employee withholding c... | `None` | 11 of 76 | 57 | 0 |
| Supplements | On what date did DEA's temporary order placing mitragynine pseudoindoxyl, MGM-15, and MGM-16 in... | `August 26, 2026` | 13 of 76 | 51 | 0 |
| USCIS fees | What is the USCIS premium processing fee (Form I-907) for an H-1B petition filed on Form I-129... | `$2,965` | 13 of 76 | 9 | 49 |
| FDA devices | What is the standard (non-small-business) FDA medical device user fee for a 510(k) premarket no... | `$28,653` | 13 of 76 | 39 | 4 |
| Aviation and drones | In the FAA's final rule 'Requirements for Interference-Tolerant Radio Altimeter Systems,' by wh... | `December 30, 2030` | 13 of 76 | 50 | 7 |
| Employer compliance 2026 | What is the IRS standard mileage rate for business use of a car for miles driven on or after Ju... | `76` | 14 of 76 | 26 | 18 |
| Memory prices | What is Raspberry Pi's current official list price for the 16GB Raspberry Pi 5 board? Give only... | `$305` | 14 of 76 | 42 | 13 |
| Consumer product safety | Under the CPSC federal toy safety standard at 16 CFR 1250.4, what is the maximum amount of extr... | `325 micrograms (325 ug)` | 14 of 76 | 50 | 3 |
| DOL wage and hour | As of today, what is the Executive Order 13658 minimum hourly wage that must be paid to workers... | `$13.65` | 15 of 76 | 49 | 7 |
| New federal deductions (One Big Beautiful Bill Act) | Schedule 1-A (Form 1040) was introduced for tax year 2025. List the deductions it is used to cl... | `No tax on tips; No tax on overtime; No tax on car loan interest; Enhanced deduction for seniors` | 16 of 76 | 40 | 0 |
| GPUs | What is the maximum frame-rate multiplier of NVIDIA's DLSS Multi Frame Generation today? Give o... | `6X` | 16 of 76 | 12 | 44 |

## What it cost

**$38.22** recorded over 1591 paid calls, against $31.74 metered by OpenRouter itself.
The ledger over-records on purpose: it books the larger of the reported cost and its own pricing, which is
why it never overspent. The free models ran on free tiers.

Grading buckets: **Correct**, **Outdated** (a real figure from an earlier year), **Hallucinated** (a number
that was never right), **Refusal**, **Truncated**, **Error**.
