"""Checks how chat models handle the image tool, using the same requests the app sends.

usage: python tool_test.py <port> <app.js>
"""
import json
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request

port, app_js = sys.argv[1], sys.argv[2]
src = open(app_js, encoding='utf-8').read()


def js_string(name):
    m = re.search(r'const ' + name + r' = `([\s\S]*?)`;', src)
    return m.group(1)


TOOL_HINT = js_string('TOOL_HINT')
BASE = re.search(r'return `(You are a helpful[\s\S]*?)`;', src).group(1).replace('${date}', 'Sunday, 11 October 2026')
# evaluate the tool definition with node, exactly as the app has it
node = "const s=require('fs').readFileSync(process.argv[1],'utf8');const a=s.indexOf('const IMAGE_TOOL');const b=s.indexOf(String.fromCharCode(10)+'};',a)+3;eval(s.slice(a,b).replace('const IMAGE_TOOL','globalThis.T'));console.log(JSON.stringify(T))"
IMAGE_TOOL = json.loads(subprocess.check_output(['node', '-e', node, app_js]))
SYSTEM = BASE + '\n\n' + TOOL_HINT


def chat(messages, **extra):
    body = {'messages': messages, 'tools': [IMAGE_TOOL], 'stream': True, 'temperature': 0.7,
            'return_progress': True, 'timings_per_token': True, 'stream_options': {'include_usage': True}, **extra}
    req = urllib.request.Request(f'http://127.0.0.1:{port}/v1/chat/completions', data=json.dumps(body).encode(),
                                 headers={'Content-Type': 'application/json'})
    t = time.time()
    content, reasoning, calls, keys, progress, timings = '', '', {}, set(), 0, None
    try:
        resp = urllib.request.urlopen(req, timeout=600)
    except urllib.error.HTTPError as e:
        return {'error': f'{e.code} {e.read()[:300]!r}'}
    for line in resp:
        line = line.decode('utf-8', 'replace').strip()
        if not line.startswith('data:') or line.endswith('[DONE]'):
            continue
        j = json.loads(line[5:])
        keys |= set(j.keys())
        if 'prompt_progress' in j:
            progress += 1
        if j.get('timings'):
            timings = j['timings']
        d = (j.get('choices') or [{}])[0].get('delta') or {}
        content += d.get('content') or ''
        reasoning += d.get('reasoning_content') or ''
        for tc in d.get('tool_calls') or []:
            c = calls.setdefault(tc.get('index', 0), {'name': '', 'args': ''})
            f = tc.get('function') or {}
            c['name'] += f.get('name') or ''
            c['args'] += f.get('arguments') or ''
    return {'s': round(time.time() - t, 1), 'content': content[:160], 'reasoning': len(reasoning), 'calls': list(calls.values()),
            'progress_chunks': progress, 'keys': sorted(keys),
            'timings': {k: timings.get(k) for k in ('prompt_n', 'cache_n', 'predicted_n', 'predicted_per_second')} if timings else None}


IMAGE = ["צייר לי חתול ג'ינג'י ישן על אדן חלון בשקיעה", 'תעשה לי לוגו לחנות פרחים בשם "פריחה"', 'אני רוצה תמונה של ירושלים בשלג']
NORMAL = ['מה בירת צרפת?', 'איך מציירים חתול? תן טיפים קצרים', 'כתוב לי שיר קצר על הים', 'מה ההבדל בין תמונה דיגיטלית לציור?']

results = {'image_required': [], 'image_auto': [], 'normal_auto': [], 'followup': None}
for q in IMAGE:
    msgs = [{'role': 'system', 'content': SYSTEM}, {'role': 'user', 'content': q}]
    results['image_required'].append((q, chat(msgs, tool_choice='required', chat_template_kwargs={'enable_thinking': False})))
    results['image_auto'].append((q, chat(msgs)))
for q in NORMAL:
    results['normal_auto'].append((q, chat([{'role': 'system', 'content': SYSTEM}, {'role': 'user', 'content': q}])))

# a follow-up after a picture, the way the app sends history
hist = [{'role': 'system', 'content': SYSTEM}, {'role': 'user', 'content': IMAGE[0]},
        {'role': 'assistant', 'content': '', 'tool_calls': [{'id': 'call_1', 'type': 'function', 'function': {'name': 'generate_image', 'arguments': json.dumps({'prompt': 'ginger cat sleeping on a windowsill at sunset, warm light, photo'})}}]},
        {'role': 'tool', 'tool_call_id': 'call_1', 'content': 'The picture was created and is now shown to the user.'}]
results['after_reply'] = chat(hist, max_tokens=120, chat_template_kwargs={'enable_thinking': False})
hist += [{'role': 'assistant', 'content': 'הנה החתול!'}, {'role': 'user', 'content': 'עכשיו שיהיה בחוץ בשלג'}]
results['followup'] = chat(hist, tool_choice='required', chat_template_kwargs={'enable_thinking': False})

ok_req = sum(1 for _, r in results['image_required'] if r.get('calls') and r['calls'][0]['name'] == 'generate_image')
ok_auto = sum(1 for _, r in results['image_auto'] if r.get('calls'))
misfire = sum(1 for _, r in results['normal_auto'] if r.get('calls'))
print(json.dumps(results, ensure_ascii=False, indent=1))
print(f'SUMMARY required={ok_req}/{len(IMAGE)} auto={ok_auto}/{len(IMAGE)} misfire_on_normal={misfire}/{len(NORMAL)}')
