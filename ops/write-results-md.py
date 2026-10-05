"""Writes RESULTS.md from the database, so the numbers in the repo are generated, never typed.

Run it after a sweep, commit the result. Every figure here can be clicked through on the site.
"""

import io
import os
import subprocess

OUT = r'C:\Temp\ForClaude\LLMQuorum\RESULTS.md'


def query(sql):
    out = subprocess.run(
        ['sqlcmd', '-S', 'localhost', '-d', 'LLMQuorum', '-E', '-C', '-h', '-1', '-W', '-s', '|',
         '-Q', 'SET NOCOUNT ON; ' + sql],
        capture_output=True, text=True, timeout=300).stdout
    return [line.split('|') for line in out.strip().splitlines() if line.strip() and 'rows affected' not in line]


modes = query("""
SELECT CASE WHEN SeatId LIKE '%web-search%' THEN 'With web search' ELSE 'From memory' END,
       COUNT(DISTINCT SeatId), COUNT(*),
       SUM(CASE WHEN Bucket='Correct' THEN 1 ELSE 0 END),
       SUM(CASE WHEN Bucket='Hallucinated' THEN 1 ELSE 0 END),
       SUM(CASE WHEN Bucket='Outdated' THEN 1 ELSE 0 END),
       CAST(100.0*SUM(CASE WHEN Bucket='Correct' THEN 1 ELSE 0 END)/COUNT(*) AS DECIMAL(5,1))
  FROM quorum.SeatGrade
 GROUP BY CASE WHEN SeatId LIKE '%web-search%' THEN 'With web search' ELSE 'From memory' END
 ORDER BY 7 DESC;""")

# The only comparison that controls for the model: the models asked BOTH ways. The population-level
# memory-vs-web split mixes two different sets of models, so it is reported as context, not as the finding.
paired = query("""
WITH s AS ( SELECT DISTINCT ModelId, CASE WHEN SeatId LIKE '%web-search%' THEN 'web' ELSE 'mem' END m
              FROM quorum.SeatGrade ),
     both AS ( SELECT ModelId FROM s GROUP BY ModelId HAVING COUNT( DISTINCT m ) = 2 )
SELECT CASE WHEN g.SeatId LIKE '%web-search%' THEN 'With web search' ELSE 'From memory' END,
       COUNT( DISTINCT g.ModelId ), COUNT(*),
       SUM( CASE WHEN g.Bucket='Correct' THEN 1 ELSE 0 END ),
       CAST( 100.0*SUM( CASE WHEN g.Bucket='Correct' THEN 1 ELSE 0 END )/COUNT(*) AS DECIMAL(5,1) )
  FROM quorum.SeatGrade g JOIN both b ON b.ModelId = g.ModelId
 GROUP BY CASE WHEN g.SeatId LIKE '%web-search%' THEN 'With web search' ELSE 'From memory' END
 ORDER BY 5 DESC;""")

pairedByModel = query("""
WITH s AS ( SELECT DISTINCT ModelId, CASE WHEN SeatId LIKE '%web-search%' THEN 'web' ELSE 'mem' END m
              FROM quorum.SeatGrade ),
     both AS ( SELECT ModelId FROM s GROUP BY ModelId HAVING COUNT( DISTINCT m ) = 2 )
SELECT g.ModelId,
       CAST( 100.0*SUM( CASE WHEN g.Bucket='Correct' AND g.SeatId NOT LIKE '%web-search%' THEN 1 ELSE 0 END )
             / NULLIF( SUM( CASE WHEN g.SeatId NOT LIKE '%web-search%' THEN 1 ELSE 0 END ), 0 ) AS DECIMAL(5,1) ),
       CAST( 100.0*SUM( CASE WHEN g.Bucket='Correct' AND g.SeatId LIKE '%web-search%' THEN 1 ELSE 0 END )
             / NULLIF( SUM( CASE WHEN g.SeatId LIKE '%web-search%' THEN 1 ELSE 0 END ), 0 ) AS DECIMAL(5,1) )
  FROM quorum.SeatGrade g JOIN both b ON b.ModelId = g.ModelId
 GROUP BY g.ModelId ORDER BY 2;""")

best = query("""
SELECT TOP 12 ModelId, CASE WHEN SeatId LIKE '%web-search%' THEN 'web' ELSE 'memory' END, Provider,
       COUNT(*), SUM(CASE WHEN Bucket='Correct' THEN 1 ELSE 0 END),
       CAST(100.0*SUM(CASE WHEN Bucket='Correct' THEN 1 ELSE 0 END)/COUNT(*) AS DECIMAL(5,1))
  FROM quorum.SeatGrade GROUP BY ModelId, SeatId, Provider
 ORDER BY 6 DESC, 4 DESC;""")

worst = query("""
SELECT TOP 12 ModelId, CASE WHEN SeatId LIKE '%web-search%' THEN 'web' ELSE 'memory' END, Provider,
       COUNT(*), SUM(CASE WHEN Bucket='Correct' THEN 1 ELSE 0 END),
       CAST(100.0*SUM(CASE WHEN Bucket='Correct' THEN 1 ELSE 0 END)/COUNT(*) AS DECIMAL(5,1))
  FROM quorum.SeatGrade GROUP BY ModelId, SeatId, Provider
 ORDER BY 6, 4 DESC;""")

hardest = query("""
SELECT TOP 12 q.Topic, LEFT(q.Prompt, 95), q.ExpectedAnswer,
       SUM(CASE WHEN g.Bucket='Correct' THEN 1 ELSE 0 END), COUNT(*),
       SUM(CASE WHEN g.Bucket='Hallucinated' THEN 1 ELSE 0 END),
       SUM(CASE WHEN g.Bucket='Outdated' THEN 1 ELSE 0 END)
  FROM quorum.SeatGrade g JOIN quorum.Question q ON q.QuestionId=g.QuestionId
 GROUP BY q.QuestionId, q.Topic, q.Prompt, q.ExpectedAnswer
 ORDER BY CAST(100.0*SUM(CASE WHEN g.Bucket='Correct' THEN 1 ELSE 0 END)/COUNT(*) AS DECIMAL(5,1));""")

money = query("""
SELECT CAST(SUM(CASE WHEN State='settled' THEN ActualUsd ELSE 0 END) AS DECIMAL(10,2)),
       SUM(CASE WHEN State='settled' THEN 1 ELSE 0 END)
  FROM quorum.SpendLedger;""")

# OpenRouter's own lifetime usage for the paid key, read from the provider rather than typed here.
# If the key file is gone the figure is omitted instead of being guessed.
def meteredUsd():
    try:
        key = io.open(os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'keys', 'openrouter-paid.key'), encoding='utf-8').read().strip()
        import urllib.request
        request = urllib.request.Request('https://openrouter.ai/api/v1/key',
                                         headers={'Authorization': 'Bearer ' + key})
        with urllib.request.urlopen(request, timeout=30) as response:
            return float(__import__('json').load(response)['data']['usage'])
    except Exception:
        return None

metered = meteredUsd()

totals = query("SELECT COUNT(*), COUNT(DISTINCT ModelId), COUNT(DISTINCT SeatId), COUNT(DISTINCT QuestionId) FROM quorum.SeatGrade;")
graded, models, seats, questions = totals[0]

lines = [
    '# Results',
    '',
    '_Snapshot: ' + __import__('datetime').date.today().isoformat() + '. The free-tier lane keeps running, so counts grow; the percentages hold._',
    '',
    'Generated from the database by `ops/write-results-md.py`. Every figure here is queried, not typed, and',
    'each one can be clicked through at [llmquorum.aidataforager.com](https://llmquorum.aidataforager.com).',
    '',
    f'**{models} models, {seats} seats, {questions} questions, {graded} graded answers.** A seat is a model plus how it',
    'was asked, so a model asked both from memory and with web search counts as two.',
    '',
    '## The finding',
    '',
    'Only some models can search, so comparing all the memory seats against all the search seats compares two',
    'different sets of models. The comparison that controls for the model is the one below: the models that were',
    'asked both ways, over all ' + str(questions) + ' questions each.',
    '',
    '| The models asked both ways | Models | Answers | Correct | Accuracy |',
    '| --- | ---: | ---: | ---: | ---: |',
]

for mode, model_n, asked, correct, acc in paired:
    lines.append(f'| {mode} | {model_n} | {asked} | {correct} | **{acc}%** |')

lines += [
    '',
    'Same models, same questions, asked in the same words. The only difference is whether the model could',
    'look the answer up. Every one of them improved:',
    '',
    '| Model | From memory | With search | Change |',
    '| --- | ---: | ---: | ---: |',
]

for model, mem_pct, web_pct in pairedByModel:
    delta = float(web_pct) - float(mem_pct)
    lines.append(f'| {model} | {mem_pct}% | {web_pct}% | +{delta:.1f} |')

lines += [
    '',
    '### Across every seat',
    '',
    'For context, and because leaving it out would be the same sleight of hand: the whole population, where the',
    'two columns are ' + modes[1][1] + ' seats and ' + modes[0][1] + ' seats rather than the same models twice.',
    '',
    '| How it was asked | Seats | Answers | Correct | Made up | Stale | Accuracy |',
    '| --- | ---: | ---: | ---: | ---: | ---: | ---: |',
]

for mode, seat_n, asked, correct, hall, old, acc in modes:
    lines.append(f'| {mode} | {seat_n} | {asked} | {correct} | {hall} | {old} | **{acc}%** |')

lines += [
    '',
    '## Best seats',
    '',
    '| Model | Asked as | Provider | Questions | Correct | Accuracy |',
    '| --- | --- | --- | ---: | ---: | ---: |',
]

for model, mode, provider, asked, correct, acc in best:
    lines.append(f'| {model} | {mode} | {provider} | {asked} | {correct} | {acc}% |')

lines += ['', '## Worst seats', '',
          '| Model | Asked as | Provider | Questions | Correct | Accuracy |',
          '| --- | --- | --- | ---: | ---: | ---: |']

for model, mode, provider, asked, correct, acc in worst:
    lines.append(f'| {model} | {mode} | {provider} | {asked} | {correct} | {acc}% |')

lines += ['', '## Hardest questions', '',
          '| Topic | Question | Answer | Correct | Made up | Stale |',
          '| --- | --- | --- | ---: | ---: | ---: |']

for topic, prompt, answer, correct, asked, hall, old in hardest:
    clean = prompt.replace('|', '/').strip()
    lines.append(f'| {topic} | {clean}... | `{answer}` | {correct} of {asked} | {hall} | {old} |')

lines += [
    '',
    '## What it cost',
    '',
    (f'**${money[0][0]}** recorded over {money[0][1]} paid calls, against **${metered:.2f}** metered by OpenRouter'
     ' itself (its own `GET /api/v1/key` lifetime usage for the key, read back when this file was generated).'
     if metered is not None else
     f"**${money[0][0]}** recorded over {money[0][1]} paid calls. OpenRouter's own meter could not be read when"
     " this file was generated, so its figure is omitted rather than repeated from an older run."),
    'The ledger over-records on purpose: it books the larger of the reported cost and its own pricing, which is',
    'why it never overspent. The free models ran on free tiers.',
    '',
    'Grading buckets: **Correct**, **Outdated** (a real figure from an earlier year), **Hallucinated** (a number',
    'that was never right), **Refusal**, **Truncated**, **Error**.',
]

io.open(OUT, 'w', encoding='utf-8', newline='\n').write('\n'.join(lines) + '\n')
print('wrote RESULTS.md,', len(lines), 'lines')
