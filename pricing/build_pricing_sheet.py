# Builds the 22-question API cost sheet from verified prices (rounds 1-2, 2026-09-18)
# and token counts measured in LLMQuorum's own SQL data. Writes .xlsx (no external libs) + .json.
# v3: rebuilt after audit 2. No vendor publishes how many search-content tokens these models bill
# per search, so web cost is a band (low / mid / worst), not a single typical number.
import json, zipfile
from xml.sax.saxutils import escape

OUT = r'C:\Temp\ForClaude\LLMQuorum\pricing\api-pricing-22q-2026-09-18'
Q = 22

# ---------------------------------------------------------------- usage assumptions
MEM_IN = 300                    # prompt per answer. Measured API prompts average 33-109 tokens per model.
OUT_R_TYP, OUT_R_WORST = 1_500, 8_000   # reasoning models, visible + thinking, per answer (measured avg 1,020, max 3,966)
OUT_I_TYP, OUT_I_WORST = 300, 1_500     # non-reasoning models, per answer (measured avg 19, max 606)
SEARCH_TYP = 26                 # Claude web seats, Q9-30: per-question mean searches, summed (95 searches / 82 answers)
SEARCH_WORST = 110              # 5 per answer, the most any measured answer used, applied to all 22
CONTENT_LOW = 8_000             # per search. OpenAI's fixed block for gpt-4o-mini / gpt-4.1-mini (not these models)
CONTENT_MID = 15_732            # per search. Our measured mean, single-search answers on Claude's search (n=56)
CONTENT_WORST = 1_448_382       # total. Per-question max of what Claude's search agent read, summed (re-reads, 2 whole PDFs)
WEB_IN_LOW = Q * MEM_IN + SEARCH_TYP * CONTENT_LOW       # 214,600
WEB_IN_MID = Q * MEM_IN + SEARCH_TYP * CONTENT_MID       # 415,632
WEB_IN_WORST = Q * MEM_IN + CONTENT_WORST                # 1,454,982
# Perplexity Agent API: content billed as input but capped per call by a budget.
# Presets low/medium/high set 2,000 tokens; search_context_size "high" = 4,000.
PPLX_IN_TYP = Q * (MEM_IN + 1.2 * 2_000)
PPLX_IN_WORST = Q * (MEM_IN + 5 * 4_000)

def tok(pin, pout, n_in, n_out):
    return (n_in * pin + n_out * pout) / 1e6

def money(x):
    return '$%.4f' % x if x < 0.01 else '$%.3f' % x

def price_row(group, route, model, what, pin, pout, reasoning, fee, content, cash, notes, conf, free_pool=False):
    """fee = $ per search. content: 'billed' | 'probably not billed' | 'billed, capped per call'."""
    ot, ow = (OUT_R_TYP, OUT_R_WORST) if reasoning else (OUT_I_TYP, OUT_I_WORST)
    mem_t = tok(pin, pout, Q * MEM_IN, Q * ot)
    mem_w = tok(pin, pout, Q * MEM_IN, Q * ow)
    fee_eff = 0.0 if free_pool else fee
    if content == 'billed':
        in_lo, in_mid, in_w = WEB_IN_LOW, WEB_IN_MID, WEB_IN_WORST
    elif content == 'probably not billed':
        in_lo = in_mid = Q * MEM_IN
        in_w = WEB_IN_WORST                              # worst case bills it anyway
    else:
        in_lo = in_mid = PPLX_IN_TYP
        in_w = PPLX_IN_WORST
    web_lo = tok(pin, pout, in_lo, Q * ot) + SEARCH_TYP * fee_eff
    web_mid = tok(pin, pout, in_mid, Q * ot) + SEARCH_TYP * fee_eff
    web_w = tok(pin, pout, in_w, Q * ow) + SEARCH_WORST * fee_eff
    search = money(fee) + ' per search' + (' after 5,000/mo free' if free_pool else '')
    return dict(group=group, route=route, model=model, what=what, pin=pin, pout=pout, search=search, content=content,
                mem_t=mem_t, web_lo=web_lo, web_mid=web_mid, mem_w=mem_w, web_w=web_w,
                tot_lo=mem_t + web_lo, tot_mid=mem_t + web_mid, tot_w=mem_w + web_w,
                cash=cash, notes=notes, conf=conf)

def fixed_row(group, route, model, what, mem_t, web_t, mem_w, web_w, cash, notes, conf, search='', content='', pin=None, pout=None):
    def add(a, b):
        return None if a is None and b is None else (a or 0) + (b or 0)
    return dict(group=group, route=route, model=model, what=what, pin=pin, pout=pout, search=search, content=content,
                mem_t=mem_t, web_lo=web_t, web_mid=web_t, mem_w=mem_w, web_w=web_w,
                tot_lo=add(mem_t, web_t), tot_mid=add(mem_t, web_t), tot_w=add(mem_w, web_w),
                cash=cash, notes=notes, conf=conf)

OA_CASH = '$5 min prepay. Auto-reload is ON by default: turn it off. Credits expire in 1 year, no refunds.'
GG_CASH = '$5 min prepay (paid tier needed for 3.x search). Credits expire in 12 months, no refunds; keys stop when the balance hits $0 (billing can lag about 10 minutes).'
OR_CASH = '$5.80 ($5 + $0.80 card fee). $10.80 also lifts free-model cap 50 to 1,000/day. Refunds only within 24 hours.'
XAI_CASH = 'Prepaid; manual minimum not stated. No refunds.'
OA_FLEX = ' Flex/Batch tier halves token prices (not the search fee).'
OA_RSN = 'Reasoning default medium, can be set to none.'
STARLINK = ' API from your Starlink connection untested.'
OR_ROUTE = ' Endpoint prices can differ by provider; pin the provider.'
XAI_STEP = ' xAI re-bills the growing context at every agent step (prompt caching offsets part of it); the web columns count content once, so real web cost can be higher. $0.05 fee per request blocked for usage-guideline violations.'

rows = [
 # ---------------------------------------------------------------- ChatGPT = OpenAI API
 price_row('ChatGPT', 'OpenAI API', 'gpt-5.6-luna', 'ChatGPT Free model', 0.20, 1.20, True, 0.01, 'billed', OA_CASH,
           OA_RSN + ' Cheaper web-capable options exist: gpt-5-nano $0.05/$0.40, gpt-5.4-nano $0.20/$1.25.' + OA_FLEX, 'confirmed'),
 price_row('ChatGPT', 'OpenAI API', 'gpt-5.6-terra', 'On Plus and Pro; limited on Free/Go', 2.00, 12.00, True, 0.01, 'billed', OA_CASH, OA_RSN + OA_FLEX, 'confirmed'),
 price_row('ChatGPT', 'OpenAI API', 'gpt-5.6-sol (promo)', 'ChatGPT Plus "GPT-5.6"', 4.00, 20.00, True, 0.01, 'billed', OA_CASH,
           OA_RSN + ' Promo price guaranteed only through Nov 21, 2026.' + OA_FLEX, 'confirmed'),
 price_row('ChatGPT', 'OpenAI API', 'gpt-5.6-sol (regular)', 'same, after promo', 5.00, 30.00, True, 0.01, 'billed', OA_CASH,
           'openai.com/api/pricing shows $5/$30; the docs pricing page shows the $4/$20 promo.', 'confirmed'),
 price_row('ChatGPT', 'OpenAI API', 'gpt-6-astra', 'Flagship; on Plus and Pro', 10.00, 50.00, True, 0.01, 'billed', OA_CASH,
           'Reasoning cannot be turned off (lowest is low).' + OA_FLEX, 'confirmed'),
 price_row('ChatGPT', 'OpenAI API', 'chat-latest', 'Instant model used in ChatGPT', 5.00, 30.00, False, 0.01, 'billed', OA_CASH,
           'Not a reasoning model. No Batch tier. Tier-1 limit of 30,000 tokens/minute can slow a web run.', 'confirmed'),
 # ---------------------------------------------------------------- Gemini = Google AI Studio API
 price_row('Gemini', 'Google Gemini API', 'gemini-3.8-flash', 'Newest Flash (app default: inferred)', 0.75, 3.75, True, 0.014, 'probably not billed', GG_CASH,
           'Price doubles Jan 1, 2027 ($1.50/$7.50). Thinking cannot be turned off.' + STARLINK,
           'prices confirmed; app mapping and content rule inferred', free_pool=True),
 price_row('Gemini', 'Google Gemini API', 'gemini-3.1-pro-preview', 'Top Pro model (app mapping inferred)', 2.00, 12.00, True, 0.014, 'probably not billed', GG_CASH,
           'No free tier. Thinks at "high" by default.' + STARLINK,
           'prices confirmed; app mapping and content rule inferred', free_pool=True),
 price_row('Gemini', 'Google Gemini API', 'gemini-3.5-flash-lite', 'Newest Flash-Lite', 0.30, 2.50, True, 0.014, 'probably not billed', GG_CASH,
           'Thinking default minimal. Older gemini-3.1-flash-lite is cheaper ($0.25/$1.50; shuts down May 7, 2027).' + STARLINK,
           'prices confirmed; content rule inferred', free_pool=True),
 fixed_row('Gemini', 'Google Gemini API, FREE tier', 'gemini-2.5-flash', 'Older model, free search', 0.0, 0.0, 0.0, 0.0, '$0',
           'Free tier includes 500 grounded prompts/day (shared with 2.5 Flash-Lite). Free-tier data is used to improve Google products.' + STARLINK,
           'confirmed', search='free, 500 prompts/day', content='free tier'),
 # ---------------------------------------------------------------- Copilot
 fixed_row('Copilot', 'GitHub Copilot FREE (CLI)', 'gpt-5.6-luna via auto', 'What Copilot Free gives you', 0.0, None, 0.0, None, '$0',
           'Tested live 2026-09-18: works, auto picked Luna. GitHub lists Free as "Haiku 4.5, GPT-5 mini, and more"; the monthly allowance is not published. No web search in script mode.',
           'tested live; allowance not published', search='none', content='no web search on this route'),
 fixed_row('Copilot', 'GitHub Copilot Pro', 'Luna, Terra, Gemini 3.8 Flash, Grok 4.6, Sonnet 5, Haiku 4.5', 'Copilot paid plan',
           None, None, None, None, '$10/month (1,500 credits = $15 at list prices). Unused credits are forfeited each month.',
           'Credits burn at the same per-token prices as the APIs, so 22 memory answers on these models use a small slice of the month. NO Sol or Astra on Pro. Web search only via /research, which runs its own Haiku agent: not a clean test.',
           'confirmed', search='none in script mode', content='no web search on this route'),
 fixed_row('Copilot', 'GitHub Copilot Pro+', 'adds gpt-5.6-sol, gpt-6-astra', 'Copilot plan with the flagships',
           None, None, None, None, '$39/month (7,000 credits). Unused credits are forfeited each month.', 'Same web-search limits as Pro. A Max plan ($100/month) also exists.', 'confirmed',
           search='none in script mode', content='no web search on this route'),
 price_row('Copilot', 'Azure OpenAI (Microsoft)', 'gpt-5.6-sol on Azure', 'Microsoft-hosted OpenAI', 4.00, 20.00, True, 0.014, 'billed',
           'Postpaid, no minimum (your Azure sub)',
           'Azure lists $4/$20 today; whether it follows OpenAI\'s Nov 21 promo end is not stated. Search = Responses API web_search, billed as Grounding with Bing $14/1k (the Agent Service Bing tool is unverified for this model). Azure \'Fl\' meters run about half price (probably Flex; inferred). Data Zone +10%. Not checked: whether your sub can deploy it.',
           'prices confirmed; content billing inferred'),
 price_row('Copilot', 'Azure OpenAI (Microsoft)', 'gpt-6-astra on Azure', 'Microsoft-hosted OpenAI', 10.00, 50.00, True, 0.014, 'billed',
           'Postpaid, no minimum (your Azure sub)', 'Search, Data Zone and deployment notes same as above.', 'prices confirmed; content billing inferred'),
 # ---------------------------------------------------------------- OpenRouter (already in use)
 price_row('OpenRouter', 'OpenRouter pass-through', 'openai/gpt-5.6-luna', 'same model, one account', 0.20, 1.20, True, 0.01, 'billed', OR_CASH,
           'No markup on tokens; OpenAI\'s own search passed through.' + OR_ROUTE, 'prices confirmed; endpoints not checked for this model'),
 price_row('OpenRouter', 'OpenRouter pass-through', 'openai/gpt-5.6-sol', 'same model, one account', 4.00, 20.00, True, 0.01, 'billed', OR_CASH,
           'Row uses OpenAI\'s current promo price ($4/$20 through Nov 21, 2026; regular $5/$30). OpenRouter\'s live endpoints: $2/$10 via OpenAI (50% flag, no end date), $4.40/$22 Bedrock, $5/$30 Azure, $5.50/$33 Azure US. Pin the OpenAI provider.',
           'promo price confirmed; endpoint prices vary'),
 price_row('OpenRouter', 'OpenRouter pass-through', 'openai/gpt-6-astra', 'same model, one account', 10.00, 50.00, True, 0.01, 'billed', OR_CASH,
           'Same $10/$50 on the OpenAI and Azure endpoints.', 'confirmed'),
 price_row('OpenRouter', 'OpenRouter pass-through', 'google/gemini-3.8-flash', 'same model, one account', 0.75, 3.75, True, 0.014, 'billed', OR_CASH,
           'OpenRouter bills search content as tokens on every engine. It lists Google search at $14/1k per call; Google\'s 5,000 free pool does not appear to pass through (inferred).',
           'prices confirmed; no free pool inferred'),
 price_row('OpenRouter', 'OpenRouter pass-through', 'google/gemini-3.1-pro-preview', 'same model, one account', 2.00, 12.00, True, 0.014, 'billed', OR_CASH,
           'Same as above.', 'Google\'s prices; OpenRouter pass-through not checked for this model; no free pool inferred'),
 fixed_row('OpenRouter', 'OpenRouter, our FREE seats + Exa search', 'current :free models', 'add web search to what we run now',
           0.0, SEARCH_TYP * 0.007, 0.0, SEARCH_WORST * 0.007, OR_CASH,
           'Per model seat. Free-model tokens stay $0; Exa search $0.007 per request (up to 10 results). That the search tool works on :free models is inferred, not stated.',
           'price confirmed; free-model use inferred', search='$0.007 per search', content='billed at $0 (free model)'),
 # ---------------------------------------------------------------- other providers we use now
 fixed_row('In use now', 'Groq', 'gpt-oss-120b browser_search, or compound', 'we already run a Groq web seat free', 0.0, 0.0, 0.0, 0.0, '$0 on free plan',
           'Free plan: gpt-oss-120b 1,000 requests/day; compound 250/day. Paid search price is unpublished (groq.com/pricing returns a 308 to the homepage). Paid gpt-oss-120b tokens $0.15/$0.60; compound also calls models with no public price.',
           'token price confirmed; search price unpublished', search='unpublished', content='unpublished', pin=0.15, pout=0.60),
 fixed_row('In use now', 'Z.ai', 'free GLM Flash models + web search', 'add web search to our free Z.ai seats', 0.0, SEARCH_TYP * 0.01, 0.0, SEARCH_WORST * 0.01,
           'Prepaid; minimum not stated', 'Per model seat. Search $0.01 per use. That it works with the free Flash models is inferred. GLM-5.3 flagship is $1.40/$4.40 if wanted.',
           'price confirmed; minimum unverified', search='$0.01 per search', content='not stated'),
 fixed_row('In use now', 'Cohere', 'Command A+', '', None, None, None, None, 'n/a', 'No hosted web search (connectors deprecated Sept 15, 2025).', 'confirmed',
           search='none', content='n/a'),
 fixed_row('In use now', 'Cloudflare', 'Workers AI models', '', None, None, None, None, 'n/a',
           'AI Gateway\'s web_search tool only works for OpenAI/Anthropic/xAI/Alibaba models, not Workers AI (it can also proxy Perplexity and Parallel search APIs directly). No Cloudflare fee. Our Cloudflare seats run inside Workers AI\'s free 10,000 neurons/day.', 'confirmed',
           search='none for our seats', content='n/a'),
 fixed_row('In use now', 'Claude (your subscription)', 'claude-opus-5', 'already in the benchmark', 0.23, 2.66, 0.24, 2.77, '$0 (in your plan)',
           'MEASURED from our 22 answers at API list prices (Claude Code\'s own billed cost). Low/mid = per-question mean, worst = per-question max, each summed over 22.',
           'measured', search='$0.010 per search (API)', content='billed', pin=5.00, pout=25.00),
 fixed_row('In use now', 'Claude (your subscription)', 'claude-sonnet-5', 'already in the benchmark', 0.04, 1.94, 0.05, 2.11, '$0 (in your plan)',
           'MEASURED, same method.', 'measured', search='$0.010 per search (API)', content='billed', pin=2.00, pout=10.00),
 # ---------------------------------------------------------------- optional adds
 price_row('Optional', 'Perplexity Agent API', 'perplexity/sonar + web_search', 'Perplexity search', 0.25, 2.50, False, 0.0025, 'billed, capped per call',
           'Prepaid; minimum not stated',
           'Sonar API support ends Sept 27, 2026; the Agent API replaces it. Perplexity maps old Sonar to its "fast" preset (gpt-5.6-luna at 2x priority price); this row prices the perplexity/sonar model on the same API. You set the content budget per call: search_context_size low 300 / medium 1,000 / high 4,000, or an explicit max. Low/mid use 2,000, what Perplexity\'s own presets set on web_search; worst uses 4,000.',
           'confirmed (minimum unverified)'),
 price_row('Optional', 'xAI API', 'grok-4.3', 'Grok; reasoning default low, can be off', 1.25, 2.50, True, 0.005, 'billed', XAI_CASH,
           'Batch 20% off.' + XAI_STEP, 'prices confirmed; content billing inferred'),
 price_row('Optional', 'xAI API', 'grok-4.6', 'Grok flagship', 2.00, 6.00, True, 0.005, 'billed', XAI_CASH,
           'Reasoning cannot be turned off, default high. No Batch. US regional endpoint 1.1x.' + XAI_STEP, 'prices confirmed; content billing inferred'),
]

def bundle(name, route_prefix, names, cash):
    sel = [r for r in rows if r['route'].startswith(route_prefix) and r['model'] in names]
    assert len(sel) == len(names), (name, [r['model'] for r in sel])
    return dict(name=name, models=', '.join(r['model'] for r in sel), cash=cash,
                lo=sum(r['tot_lo'] for r in sel), mid=sum(r['tot_mid'] for r in sel), w=sum(r['tot_w'] for r in sel))

OA3 = {'gpt-5.6-luna', 'gpt-5.6-sol (promo)', 'gpt-6-astra'}
OR5 = {'openai/gpt-5.6-luna', 'openai/gpt-5.6-sol', 'openai/gpt-6-astra', 'google/gemini-3.8-flash', 'google/gemini-3.1-pro-preview'}
combos = [
 bundle('OpenAI account, no Astra', 'OpenAI API', OA3 - {'gpt-6-astra'}, '$5 min prepay'),
 bundle('OpenAI account, with Astra', 'OpenAI API', OA3, '$5 min prepay'),
 bundle('Google account', 'Google Gemini API', {'gemini-3.8-flash', 'gemini-3.1-pro-preview'}, '$5 min prepay'),
 bundle('OpenRouter, no Astra', 'OpenRouter pass', OR5 - {'openai/gpt-6-astra'}, '$5.80 min; $10.80 also lifts free-model cap'),
 bundle('OpenRouter, with Astra', 'OpenRouter pass', OR5, '$5.80 min; $10.80 also lifts free-model cap'),
]

# ---------------------------------------------------------------- xlsx writer (no dependencies)
def col(n):
    s = ''
    while n:
        n, r = divmod(n - 1, 26); s = chr(65 + r) + s
    return s

def cell(ref, v, style=0):
    if v is None or v == '':
        return '<c r="%s" s="%d"/>' % (ref, style)
    if isinstance(v, (int, float)):
        return '<c r="%s" s="%d"><v>%r</v></c>' % (ref, style, round(float(v), 6))
    return '<c r="%s" s="%d" t="inlineStr"><is><t xml:space="preserve">%s</t></is></c>' % (ref, style, escape(str(v)))

def sheet_xml(table, widths, styles_by_col=None, header_rows=1):
    out = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>',
           '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">',
           '<sheetViews><sheetView workbookViewId="0"><pane ySplit="%d" topLeftCell="A%d" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>' % (header_rows, header_rows + 1),
           '<cols>' + ''.join('<col min="%d" max="%d" width="%s" customWidth="1"/>' % (i + 1, i + 1, w) for i, w in enumerate(widths)) + '</cols>',
           '<sheetData>']
    for r, row in enumerate(table, 1):
        cells = []
        for c, v in enumerate(row, 1):
            if isinstance(v, tuple):
                v, st = v
            elif r <= header_rows:
                st = 1
            else:
                st = (styles_by_col or {}).get(c, 3)
            cells.append(cell('%s%d' % (col(c), r), v, st))
        out.append('<row r="%d">%s</row>' % (r, ''.join(cells)))
    out.append('</sheetData></worksheet>')
    return '\n'.join(out)

STYLES = '''<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
<numFmts count="1"><numFmt numFmtId="164" formatCode="&quot;$&quot;#,##0.00"/></numFmts>
<fonts count="3"><font><sz val="10"/><name val="Calibri"/></font><font><b/><sz val="10"/><name val="Calibri"/></font><font><b/><sz val="12"/><name val="Calibri"/></font></fonts>
<fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFDDEBF7"/><bgColor indexed="64"/></patternFill></fill></fills>
<borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
<cellXfs count="6">
<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
<xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment wrapText="1" vertical="top"/></xf>
<xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyAlignment="1"><alignment vertical="top"/></xf>
<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment wrapText="1" vertical="top"/></xf>
<xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0" applyFont="1"/>
<xf numFmtId="164" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1" applyNumberFormat="1" applyAlignment="1"><alignment vertical="top"/></xf>
</cellXfs></styleSheet>'''

def write_xlsx(path, sheets):
    with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED) as z:
        z.writestr('[Content_Types].xml',
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
            '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>'
            '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>'
            '<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>'
            + ''.join('<Override PartName="/xl/worksheets/sheet%d.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>' % (i + 1) for i in range(len(sheets)))
            + '</Types>')
        z.writestr('_rels/.rels', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>')
        z.writestr('xl/workbook.xml', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>'
            + ''.join('<sheet name="%s" sheetId="%d" r:id="rId%d"/>' % (escape(n), i + 1, i + 1) for i, (n, _) in enumerate(sheets)) + '</sheets></workbook>')
        z.writestr('xl/_rels/workbook.xml.rels', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            + ''.join('<Relationship Id="rId%d" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet%d.xml"/>' % (i + 1, i + 1) for i in range(len(sheets)))
            + '<Relationship Id="rId%d" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>' % (len(sheets) + 1))
        z.writestr('xl/styles.xml', STYLES)
        for i, (_, xml) in enumerate(sheets):
            z.writestr('xl/worksheets/sheet%d.xml' % (i + 1), xml)

# ---------------------------------------------------------------- sheet 1: cost for 22 questions
hdr = ['Group', 'Route', 'Model', 'What it is', 'Input $/1M', 'Output $/1M', 'Web search fee', 'Search content',
       '22 Qs from memory', '22 Qs web: low', '22 Qs web: mid', 'TOTAL 44 calls: low', 'TOTAL 44 calls: mid', 'TOTAL 44 calls: worst',
       'Cash to start', 'Notes', 'Confidence']
t1 = [hdr]
for r in rows:
    t1.append([r['group'], r['route'], r['model'], r['what'], r['pin'], r['pout'], r['search'], r['content'],
               r['mem_t'], r['web_lo'], r['web_mid'],
               (r['tot_lo'], 5) if r['tot_lo'] is not None else None, (r['tot_mid'], 5) if r['tot_mid'] is not None else None, r['tot_w'],
               r['cash'], r['notes'], r['conf']])
t1.append([])
t1.append([('Bundles', 4)])
t1.append(['Bundle', 'Models', '', '', '', '', '', '', '', '', '', 'TOTAL: low', 'TOTAL: mid', 'TOTAL: worst', 'Cash to start'])
for b in combos:
    t1.append([b['name'], b['models'], '', '', '', '', '', '', '', '', '', (b['lo'], 5), (b['mid'], 5), b['w'], b['cash']])
money_cols = {5: 2, 6: 2, 9: 2, 10: 2, 11: 2, 12: 5, 13: 5, 14: 2}
s1 = sheet_xml(t1, [10, 22, 26, 24, 8, 8, 16, 14, 10, 10, 10, 11, 11, 11, 30, 60, 20], money_cols)

# ---------------------------------------------------------------- sheet 2: assumptions and checks
t2 = [['Item', 'Value', 'Where it comes from'],
 ['Questions', Q, 'Public set v2. Each model answers twice: once from memory, once with web search = 44 calls.'],
 ['Low / mid / worst', 'the three cost columns', 'Low and mid differ only in search content per search. Worst uses worst output, 5 searches per answer and our worst observed content.'],
 ['Memory answer, input tokens', MEM_IN, 'Measured API prompts average 33-109 tokens per model; 300 is a round, high figure.'],
 ['Reasoning model output per answer, low/mid', OUT_R_TYP, 'Measured reasoning seats average 1,020 tokens (visible + thinking).'],
 ['Reasoning model output per answer, worst', OUT_R_WORST, 'About 2x our highest measured (3,966). Flagships at high effort are the big unknown.'],
 ['Non-reasoning output per answer, low-mid / worst', '%d / %d' % (OUT_I_TYP, OUT_I_WORST), 'Measured non-reasoning seats average 19 tokens, max 606.'],
 ['Searches for all 22, low/mid', SEARCH_TYP, 'Our Claude web seats: 95 searches in 82 answers (per-question mean, summed = 26.0).'],
 ['Searches for all 22, worst', SEARCH_WORST, '5 per answer, the most any measured answer used, applied to every answer.'],
 ['Search content per search: LOW', CONTENT_LOW, 'OpenAI bills gpt-4o-mini and gpt-4.1-mini search content as a fixed 8,000 input tokens per call. Those are not the models priced here; no vendor publishes a figure for these models.'],
 ['Search content per search: MID', CONTENT_MID, 'Our measured mean for answers with exactly one search on Claude\'s search (56 answers). The median is 11,608, so the mean runs 35% higher; it also includes the search agent\'s own short prompt. Mid leans high.'],
 ['Search content, all 22: WORST', CONTENT_WORST, 'Per-question max of everything Claude\'s search agent read, summed. Includes its re-reads and two whole PDFs (Georgia G-4, W-4).'],
 ['Web input for all 22: low / mid / worst', '%s / %s / %s' % (format(WEB_IN_LOW, ','), format(WEB_IN_MID, ','), format(WEB_IN_WORST, ',')), '22 x 300 prompt + content above.'],
 ['OpenAI search content', 'billed (conflict)', 'OpenAI\'s two official pages disagree: the docs pricing page says search content is billed at model rates; openai.com/api/pricing says it is free. Sheet assumes billed. If free, OpenAI web cost drops to about the memory cost plus $0.26.'],
 ['Gemini direct: search content', 'probably not billed', 'Google says grounding content is excluded from token billing, stated page-wide, not on each model row. Low/mid assume not billed; worst bills it.'],
 ['Gemini 3.x direct: search fee', '$0 at our volume', '5,000 free searches/month on the PAID tier, shared across 3.x models; then $14 per 1,000. Not on the free tier.'],
 ['OpenRouter: search content', 'billed', 'OpenRouter: search pricing is in addition to token costs for the search result content, on every engine.'],
 ['Perplexity: search content', 'billed, capped per call', 'Billed as input for the tokens actually used, up to a per-call budget you set: search_context_size low 300 / medium 1,000 / high 4,000, or an explicit max_tokens. Perplexity\'s own presets set max_tokens 2,000 on web_search. Low/mid use 2,000; worst uses 4,000.'],
 ['xAI: search content', 'billed, re-sent every step', 'xAI re-bills the growing context at each agent step (caching offsets part of it). Rows count content once, so xAI web cost can be higher than shown.'],
 ['Retries', 'not included', 'A retried question costs the same again. Budget 10% extra.'],
 ['Sales tax', 'not included', 'OpenAI, Google, GitHub and Azure say US sales tax can apply by billing address; the others likely do too. No rate given.'],
 ['Long-context price steps', 'not hit', 'Highest single measured answer is 159K tokens; steps start at 200K-272K depending on model.'],
 ['Spending cap', 'prepaid balance, roughly', 'OpenAI, Google, OpenRouter and xAI are prepaid. OpenAI warns not to rely on the balance as an instant cutoff; Google can overrun for about 10 minutes.'],
 ['How to pin the real number', 'run 1 question first', 'The first paid web answer per model shows its real search-content tokens. Run one question, read the usage, then decide on the other 21.'],
 [],
 ['CHECK', 'Result', 'How'],
 ['1. Formula vs real bills', 'PASS: 198 of 198 within 1%', 'Recomputed tokens x price + $0.01 per search for every Claude call we stored and compared with Claude Code\'s own billed cost.'],
 ['Audit round 1 (v1)', 'FAILED, rebuilt', 'Prices, fees and math all correct, but the web input figure was Claude Code re-sending its own cached context, not search content. Also: OpenRouter Gemini content wrongly marked free; 3.5 Flash-Lite wrongly called cheapest.'],
 ['Audit round 2 (v2)', 'FAILED, rebuilt', 'Math exact again, but the single "typical" 8,000 tokens per search came from two other OpenAI models, and our own data runs higher. v3 replaces the point estimate with the low/mid/worst band. Also fixed: Groq free limits, credit expiry and refund terms, OpenRouter Sol endpoint range, Perplexity content caps and Sonar mapping, xAI fee scope, Cohere wording, Azure sales tax.'],
 ['2. Every price traced to its official page (v3)', 'PASS on every price, fee and cash term (30 rows)', 'A separate agent traced each price, fee and claim to the saved official pages and live re-pulls. It found 6 wrong labels, fixed here (Azure Flex name, OpenRouter Sol called list price, xAI fee scope, Copilot Free model list, Terra plans, two OpenRouter confidence labels). Its one WRONG finding, on Perplexity\'s 2,000-token budget, was itself wrong: the presets doc sets max_tokens 2000 on web_search.'],
 ['3. Every cell recomputed independently (v3)', 'PASS: all cells and bundles to the cent', 'A separate agent recomputed every cost with its own code and re-derived every SQL figure exactly (15,732 mean, 11,608 median, n=56; 1,448,382; 26.03 searches). Bracket test on Claude Opus 5: low predicts $2.16, mid $3.16, real bill $2.66.'],
]
s2 = sheet_xml(t2, [44, 30, 110], {2: 3})

write_xlsx(OUT + '.xlsx', [('Cost for 22 Qs', s1), ('Assumptions and checks', s2)])
json.dump(dict(assumptions=dict(Q=Q, MEM_IN=MEM_IN, OUT_R_TYP=OUT_R_TYP, OUT_R_WORST=OUT_R_WORST, OUT_I_TYP=OUT_I_TYP,
                                OUT_I_WORST=OUT_I_WORST, SEARCH_TYP=SEARCH_TYP, SEARCH_WORST=SEARCH_WORST,
                                CONTENT_LOW=CONTENT_LOW, CONTENT_MID=CONTENT_MID, CONTENT_WORST=CONTENT_WORST,
                                WEB_IN_LOW=WEB_IN_LOW, WEB_IN_MID=WEB_IN_MID, WEB_IN_WORST=WEB_IN_WORST,
                                PPLX_IN_TYP=PPLX_IN_TYP, PPLX_IN_WORST=PPLX_IN_WORST),
               rows=rows, combos=combos, assumptions_tab=t2), open(OUT + '.json', 'w', encoding='utf-8'), indent=1)

f = lambda x: '   -  ' if x is None else '%6.2f' % x
for r in rows:
    print('%-11s %-30s %-30s mem %s lo %s mid %s | TOT lo %s mid %s worst %s' % (r['group'], r['route'][:30], r['model'][:30], f(r['mem_t']), f(r['web_lo']), f(r['web_mid']), f(r['tot_lo']), f(r['tot_mid']), f(r['tot_w'])))
for b in combos:
    print('%-28s lo %6.2f mid %6.2f worst %6.2f  %s' % (b['name'], b['lo'], b['mid'], b['w'], b['cash']))
