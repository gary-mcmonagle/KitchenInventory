"""Experiment 5 (spike #124): headroom against luna's tokens-per-minute quota. N plan outlines at once (3 days x 2 meals, use first on)."""
import json, sys, time, concurrent.futures as cf
import outline
from revise_lib import Ctx
SP, N = sys.argv[1], int(sys.argv[2])
c = Ctx(SP)
counts = {"lunch": 3, "dinner": 3}
msgs = [{"role": "system", "content": outline.system_prompt(True, prefer_use_first=True)}, {"role": "user", "content": outline.user_prompt(counts, c.stock, c.usual, True)}]
def one(i):
    try: return outline.call(msgs, outline.schema())[1]
    except Exception as e: return {"error": str(e)[:200]}
t = time.time()
with cf.ThreadPoolExecutor(N) as ex: rs = list(ex.map(one, range(N)))
tokens = sum(r.get("prompt", 0) + r.get("completion", 0) for r in rs)
print(f"{N} concurrent outlines: wall {time.time() - t:.1f}s, tokens {tokens}, errors {[r['error'] for r in rs if 'error' in r]}")
print("per-call seconds:", sorted(r.get("secs") for r in rs if "secs" in r))
