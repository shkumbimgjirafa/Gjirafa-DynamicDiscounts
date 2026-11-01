"""The build sessions' own AI usage, per hours.md block (the unattended blocks count as AI usage only).

Reads the Claude Code session transcripts of the two build sessions (JSONL; only the usage fields, never the content),
keeps each API response once (by message id), sums input, output, cache-write and cache-read tokens per model, and prices
them at the API list price of that model (from the claude-api skill, cached 2026-10-06): API-equivalent cost. The actual
spend is the flat Claude subscription. Writes docs/hackathon-2026/evidence/ai-usage.json (aggregates only).

    python -I docs/hackathon-2026/evidence/build_ai_usage.py <transcript.jsonl>[@<from-utc>] ... [extra:<label>=<usd>@<utc>] ...

An extra is a build-time call that left no transcript (e.g. the AI-judge dry run, run with --no-session-persistence):
its total_cost_usd from the call's JSON, counted in the block it ran in.

Timestamps in the transcripts are UTC; hours.md blocks are CET (UTC+1 on 30-31 Oct 2026).
"""
import json
import re
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
CET = timezone(timedelta(hours=1))

# USD per million tokens: input, output, cache read; cache writes are 1.25x input (5-minute) and 2x input (1-hour).
PRICES = {
    "claude-opus-5-5": (4.00, 20.00, 0.20),
    "claude-sonnet-5-5": (2.00, 10.00, 0.20),
    "claude-opus-5": (5.00, 25.00, 0.50),
    "claude-opus-4-8": (5.00, 25.00, 0.50),
    "claude-fable-5-1": (10.00, 50.00, 0.25),
    "claude-haiku-5-5": (0.10, 0.50, 0.01),
}


def cost(model, u):
    p_in, p_out, p_read = PRICES[model]
    cc = u.get("cache_creation") or {}
    w1h = cc.get("ephemeral_1h_input_tokens", 0)
    w5m = cc.get("ephemeral_5m_input_tokens", u.get("cache_creation_input_tokens", 0) - w1h)
    return (u.get("input_tokens", 0) * p_in + u.get("output_tokens", 0) * p_out + u.get("cache_read_input_tokens", 0) * p_read
            + w5m * p_in * 1.25 + w1h * p_in * 2.0) / 1_000_000


def responses(path, since):
    seen = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            try:
                d = json.loads(line)
            except ValueError:
                continue
            m = d.get("message") or {}
            if d.get("type") != "assistant" or not m.get("usage") or not m.get("id"):
                continue
            t = datetime.fromisoformat(d["timestamp"].replace("Z", "+00:00"))
            if since and t < since:
                continue
            seen[m["id"]] = (t, m.get("model", "?"), m["usage"])        # the last copy carries the final usage
    return seen


def blocks():
    out = []
    for row in (REPO / "docs/hackathon-2026/hours.md").read_text(encoding="utf-8").splitlines():
        if not re.match(r"^\| (Fri|Sat|Sun|Mon) ", row):
            continue
        c = [x.strip() for x in row.split("|")]
        if not (re.match(r"^\d\d:\d\d", c[2]) and re.match(r"^\d\d:\d\d", c[3])):
            continue                    # a day-level row of the person's own hours (no block times): no AI usage of its own
        day = datetime.strptime(c[1].split(" ", 1)[1] + " 2026", "%d %b %Y").date()
        start_txt = c[2]
        start_day = day - timedelta(days=1) if "(30 Oct)" in start_txt and day.day == 31 else day
        start = datetime.combine(start_day, datetime.strptime(start_txt[:5], "%H:%M").time(), CET)
        end = datetime.combine(day, datetime.strptime(c[3][:5], "%H:%M").time(), CET)
        if end < start:
            end += timedelta(days=1)
        out.append({"day": c[1], "from": start_txt, "to": c[3], "hours": c[4], "unattended": c[4].startswith("0 (unattended"),
                    "startUtc": start.astimezone(timezone.utc), "endUtc": end.astimezone(timezone.utc)})
    return out


def main(args):
    resp, sources, extras = [], [], []
    counted = set()                     # message ids already counted: a subagent's transcript can sit under two session folders
    for a in args:
        if a.startswith("extra:"):
            name, _, rest = a[len("extra:"):].partition("=")
            usd, _, at = rest.partition("@")
            extras.append({"label": name, "usd": float(usd), "atUtc": datetime.fromisoformat(at).replace(tzinfo=timezone.utc)})
            continue
        path, _, since_txt = a.partition("@")
        since = datetime.fromisoformat(since_txt).replace(tzinfo=timezone.utc) if since_txt else None
        r = {k: v for k, v in responses(path, since).items() if k not in counted}
        counted.update(r)
        resp += r.values()
        sources.append({"transcript": Path(path).name, "from": since_txt or "start", "responses": len(r)})
    unpriced = sorted({m for _, m, _ in resp if m not in PRICES})
    priced = [(t, m, u) for t, m, u in resp if m in PRICES]

    def total(rs):
        tok = {"input": 0, "output": 0, "cacheWrite": 0, "cacheRead": 0}
        usd = 0.0
        for _, m, u in rs:
            tok["input"] += u.get("input_tokens", 0)
            tok["output"] += u.get("output_tokens", 0)
            tok["cacheWrite"] += u.get("cache_creation_input_tokens", 0)
            tok["cacheRead"] += u.get("cache_read_input_tokens", 0)
            usd += cost(m, u)
        return {"responses": len(rs), "tokens": tok, "usdApiEquivalent": round(usd, 2)}

    def with_extras(t, inside):
        xs = [x for x in extras if inside(x["atUtc"])]
        if xs:
            t["usdApiEquivalent"] = round(t["usdApiEquivalent"] + sum(x["usd"] for x in xs), 2)
            t["extras"] = [{"label": x["label"], "usd": x["usd"]} for x in xs]
        return t

    rows = []
    for b in blocks():
        inside = [x for x in priced if b["startUtc"] <= x[0] < b["endUtc"]]
        rows.append({"day": b["day"], "from": b["from"], "to": b["to"], "unattended": b["unattended"],
                     **with_extras(total(inside), lambda t, b=b: b["startUtc"] <= t < b["endUtc"])})
    first = min(b["startUtc"] for b in blocks())
    result = {
        "generated": datetime.now(CET).strftime("%Y-%m-%d %H:%M"),
        "label": "measured tokens; API-equivalent cost at list prices (the actual spend is the flat Claude subscription)",
        "prices": "USD per million tokens from the claude-api skill (cached 2026-10-06): " + "; ".join(
            f"{k} {v[0]}/{v[1]}, cache read {v[2]}" for k, v in PRICES.items()) + "; cache writes 1.25x input (5 min), 2x (1 h)",
        "sources": sources,
        "unpricedModels": unpriced,
        "sinceKickoff": with_extras(total([x for x in priced if x[0] >= first]), lambda t: t >= first),
        "unattendedBlocks": with_extras(total([x for x in priced if any(b["unattended"] and b["startUtc"] <= x[0] < b["endUtc"] for b in blocks())]),
                                        lambda t: any(b["unattended"] and b["startUtc"] <= t < b["endUtc"] for b in blocks())),
        "blocks": rows,
    }
    (HERE / "ai-usage.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps({k: result[k] for k in ("sources", "unpricedModels", "sinceKickoff", "unattendedBlocks")}, indent=1))


if __name__ == "__main__":
    main(sys.argv[1:])
