# basalt server — HTTP API reference for a native Windows client

Source of truth: `serve/handler.py`, `serve/service.py`, `serve/router.py`,
`serve/engine/process.py`, `serve/engine/progress.py`, `serve/engine/slots.py`,
`serve/util/logbuf.py`, `serve/util/loglevel.py`, `serve/util/telemetry.py`,
`serve/security.py`, `serve/web/app.js` (the built-in stats page), `docs/running.md`.

Every field name, type and unit below is derived from that code. Sample objects are **reconstructed**
from the code with plausible values — they are not captured traffic.

> Note on the path: this file is at `C:\Users\neur0\Documents\Projects\basalt-ui\design\api-reference.md`.
> The originally requested `C:\Users\neur0\Documents\basalt-ui\design\` is outside this session's writable
> workspace, so it could not be created or written from here. To place it there yourself:
> `New-Item -ItemType Directory C:\Users\neur0\Documents\basalt-ui\design -Force; Copy-Item C:\Users\neur0\Documents\Projects\basalt-ui\design\api-reference.md C:\Users\neur0\Documents\basalt-ui\design\`

---

## 1. Conventions

| Item | Value | Where |
| --- | --- | --- |
| Default bind | `127.0.0.1:8080` | `serve/handler.py` `serve()` |
| HTTP version | **`HTTP/1.0`** (`protocol_version`) | `handler.py:54` |
| Connection reuse | The server closes after each response. Reuse needs the client to send `Connection: keep-alive` on every request (stdlib behaviour; the server itself never advertises keep-alive). Plan one connection per poll otherwise. | `handler.py:54` |
| Body limit (JSON APIs) | `svc.max_payload`, default `256 MiB` (`--max-payload-mib`) → `413 {"error":{"type":"invalid_request_error","message":"the request body is larger than 256 MiB"}}` | `handler.py:_body`, `service.py:112` |
| Control body limit (`/load`, `/unload`) | `CONTROL_BODY_MAX = 64 KiB` → `413 {"error":{"message":"control request body is limited to 64 KiB"}}` | `handler.py:34,523` |
| Trailing slashes | Path is normalised: `rstrip("/")`, query ignored for routing (`/v1/chat/completions/` == `/v1/chat/completions`) | `handler.py:269,361` |
| Chunked bodies | Supported (`Transfer-Encoding: chunked` wins over `Content-Length`) | `handler.py:81-118` |
| Error shape (most routes) | `{"error": {"type": <str>, "message": <str>}}`. Some 404s send only `{"error": {"message": "not found"}}` (no `type`) — treat `type` as optional. | `handler.py:291,356,403` |
| `/v1/responses` errors | Different shape (`{"error": {"message":…, "code":…, "param":…}}`) via `responses.error_body` | `handler.py:395,459` |
| Streaming | Only the generation endpoints (`/v1/chat/completions`, `/v1/messages`, `/v1/responses`) stream `text/event-stream`; comment lines `: keep-alive` every ~10 s while queued (`QUEUE_BEAT_S = 10.0`). | `handler.py:167-185,583` |
| Encoding | `Content-Type: application/json`, UTF-8, `ensure_ascii=False` (non-ASCII is sent raw) | `handler.py:247-256` |

### 1.1 Authentication — `_authorized()`

```
if not svc.api_key:                      # server started without --api-key
    return True                          # no key ever required
auth  = headers["Authorization"]         # "Bearer <key>"  (case-insensitive "bearer ")
given = auth[7:] if bearer else headers["x-api-key"]
key_matches(given, svc.api_key)          # constant-time; accepts UTF-8 or Latin-1 bytes of the key
→ else 401 {"error":{"type":"authentication_error","message":"missing or wrong API key"}}
```

Send the key as **`Authorization: Bearer <key>`** (what the bundled page does) or **`x-api-key: <key>`**.
Note the header is read from `self.headers`, so `api-key` (bare) is *not* a supported header name.

### 1.2 Which endpoints need the key

`do_GET` checks `_authorized()` per route, `do_POST` checks it first for all POSTs.

| Unauthenticated — safe to poll without a key | Key-required (only when the server has `--api-key`) |
| --- | --- |
| `GET /health`, `GET /api/health` (`handler.py:319`, no `_authorized()` call) | `GET /metrics` (with or without `?model=`) |
| `GET /` (the stats page HTML) | `GET /status` |
| `GET /web/<file>.css|.js|.svg` (static assets) | `GET /slots` |
| `GET /fonts/<name>.woff2` | `GET /props` |
| `OPTIONS *` (CORS preflight — never key-checked) | `GET /api/logs` |
| *anything else unknown → `404`, checked after auth on GET routes* | `GET /v1/models`, `GET /models`, `GET /v1/status` |
| | **every** `POST` route (`do_POST` checks the key first) |

`GET /health` is the only useful unauthenticated status route: it is what the app should use as the
"server up / is a key required / model loading" beacon. Everything else a dashboard needs
(`/metrics`, `/slots`, `/api/logs`, `/v1/status`) requires the key when the server was started with `--api-key`.
`GET /health` also carries `"api_key": true|false`, so the client can decide whether to ask for one
(the bundled page does exactly this: `handler.py:319-325`, `app.js:461`).

### 1.3 Host header, CSRF-ish guards, `X-Forwarded-*`

All three guards are **inert when the server runs with `--api-key`** (each starts with `if svc.api_key: …return/pass`).
Without a key:

1. **DNS-rebinding check — every request, before routing** (`parse_request`, `handler.py:65-79` →
   `security.host_allowed`). The `Host` header must be an IP, `localhost`/`*.localhost`, or a known name
   (bind address, `--allowed-host` names incl. `.example.com` suffixes, trusted-origin hosts, this PC's
   name/LAN IPs). Unknown name →
   `403 {"error":{"type":"forbidden","message":"Host '<h>' is not allowed (DNS rebinding protection) …"}}`.
   A **missing** `Host` header passes. Practical effect for a native client: `Host: localhost:8080` or
   `127.0.0.1:8080` is always fine; a reverse-proxy name needs `--allowed-host`.
2. **`_foreign_page()`** — only `POST /v1/*`, no key
   (`handler.py:136-153`): if an `Origin` header is present it must be this server's own page / a known
   name / a `--cors-origin` (`security.origin_allowed`; `null` origin fails, non-`http(s)` schemes pass);
   otherwise `403 {"error":{"type":"forbidden","message":"web pages of <origin> may not use this server without an API key; …"}}`.
   A request **with no `Origin` header is fine**. If the origin is allowed but `Content-Type` does not start
   with `application/json`, `415 {"error":{"message":"send application/json"}}`.
3. **`_own_page()` — "own page and JSON only"** for the control routes `POST /load`, `POST /unload`,
   `POST /v1/load`, `POST /v1/unload`, `POST /v1/vram` (`handler.py:358-368`, `555-566`):
   * `Content-Type` must start with `application/json`, else `415 {"error":{"message":"send application/json"}}`
     (a browser form POST from any site therefore cannot control the model);
   * an `Origin`, when present, must have `scheme://host[:port]` **string-equal to the `Host` header**
     (after stripping the scheme and trailing `/`) or be listed in the server's `trusted_origins` setting; else
     `403 {"error":{"message":"<what> only from the server's own page"}}` where `<what>` is
     `the model can be loaded or unloaded` or `the VRAM reserve can be changed`.
     Proxy-added headers prove nothing. Ports matter: `Origin: http://localhost:8080` vs `Host: 127.0.0.1:8080` **fails**.
   ⇒ **A native app should send no `Origin` header and always `Content-Type: application/json` on POSTs**
   (even when the body is empty). Sending requests from a WebView2 renderer is the risky path: prefer the
   app's background process, or use the API key.
4. **`X-Forwarded-*`** (`security.FORWARDING_HEADERS` / `FORWARDING_PREFIXES`, used by `remote_reason`)
   never affect auth, but they *do* set the **image-source policy**: any header named `X-Forwarded-*`,
   `X-Original-*`, `X-Envoy-*`, `X-Amzn-*`, `X-Azure-*`, `X-Appengine-*`, `cf-*`, `tailscale-*`,
   `Via`, `X-Real-IP`, `True-Client-IP`, … — or an `Origin` header, or `Sec-Fetch-Site != none`,
   or a non-loopback socket peer, or a second/odd `Host` — marks the request *remote*, so it may not name
   local image files (`--image-url-host`/`--image-dir` policy, `handler.py:155-158`).
   A desktop app that wants to attach local images must therefore send the **minimum headers**:
   no `X-Forwarded-*`, no `Origin`, no `Sec-Fetch-*`, and `Host: localhost:8080` from a loopback socket.
5. **CORS** headers are emitted only for `/v1/*` and only for listed `--cors-origin` origins
   (`Access-Control-Allow-Headers` echoes the request's `Access-Control-Request-Headers`, else
   `Authorization, Content-Type, x-api-key, anthropic-version, anthropic-beta`; max-age 600). Irrelevant
   to a native client unless you embed a web page.
6. `--cors-origin *` together with no API key is warned about at start (`server.py:341`); listening on a
   non-loopback address without a key is flagged (`security.exposed_without_key`).

---

## 2. Status-route common objects

**`swap`** (`router.swap_status()`; `null` when no swap is running) — appears in `/health`, `/metrics`, `/v1/status`:

| Key | Type | Meaning |
| --- | --- | --- |
| `from` | `str \| null` | Model being swapped out (`null` on the first load) |
| `to` | `str` | Model being swapped in |
| `phase` | `"unloading" \| "loading"` | `unloading` until the old engine is gone, then `loading` |
| `elapsed_s` | `float` (1 dp, seconds) | Since the swap started |
| `waiting` | `int` | Requests in the router queue right now |

**`load`** (`Service.load_progress()`; `null` when nothing is loading) — appears in `/metrics live.load`, `/v1/status.load`:

| Key | Type | Meaning |
| --- | --- | --- |
| `kind` | `"weights" \| "wake"` | `weights` = cold load/swap; `wake` = resuming a sleeping engine (seconds, no percentages) |
| `percent` | `int \| null` | Weights only: 0..**99** — `LoadBars.progress()` clamps with `min(99, …)` so it **never reports 100**; `null` while the engine has announced no plan/total yet |
| `gib` | `object {target: float}` \| absent | Weights only; GiB **per memory target** already written (1 dp), e.g. `{"ram": 12.4, "cuda0": 21.7}`; target keys are display names (`ram`, `cuda<N>`, `ngram`). Keys appear only for started targets, so the object can be `{}` |

**Model entry** (array element of `/v1/models` `data`, `/metrics models`, `/v1/status models`):

| Key | Type | Meaning |
| --- | --- | --- |
| `id` | `str` | Served name (what a request's `model` field must contain; `Service.served_names()` also accepts the model file's basename) |
| `object` | `"model"` | constant |
| `status.value` | `"loading" \| "loaded" \| "unloaded" \| "failed"` | `failed` only after a load error the router still remembers |
| `status.error` | `str` (only when `value == "failed"`) | Last load failure text, e.g. `"… did not load: …"` |
| `asleep` | `bool` | Engine alive with its GPU state suspended by the driver (`--idle-sleep`, Linux only) |
| `meta.n_ctx` | `int` tokens | Context window |
| `architecture.input_modalities` | `["text"] \| ["text","image"]` | `image` iff `--vision` is on |
| `architecture.output_modalities` | `["text"]` | constant |

---

## 3. Endpoints

### 3.1 `GET /health` (also `GET /api/health`) — **no auth**, no query params

Always the "is it alive" probe. Describes `router.shown()` = swap target, else the loaded model, else the first configured model.

| Key | Type | Meaning |
| --- | --- | --- |
| `status` | `"ok"` | Constant; presence of a JSON body is the liveness signal |
| `service` | `"basalt"` | Constant |
| `model` | `str` | Shown model's name |
| `max_context` | `int` tokens | `engine.max_context` (0 while the engine has not sent `READY`) |
| `images` | `bool` | Vision input enabled |
| `api_key` | `bool` | Server requires a key → the client must send one on every other route |
| `loaded` | `bool` | Engine process alive |
| `loading` | `bool` | A load or wake is in progress (or queued) |
| `percent` | `int 0..99 \| null` | Load progress; `null` when not loading, when `load_kind == "wake"`, or before the engine announces a byte plan |
| `swap` | `object \| null` | See **swap**; `null` unless a model swap is running |

Reconstructed:

```json
{
  "status": "ok",
  "max_context": 131072,
  "model": "qwen3-30b-a3b",
  "images": true,
  "api_key": true,
  "loaded": false,
  "loading": true,
  "service": "basalt",
  "percent": 63,
  "swap": null
}
```

---

### 3.2 `GET /metrics` — auth. Optional `?model=<name>`

`model` selects the service by served name **or** model-file basename; unknown → `404 {"error":{"message":"model not found"}}`;
omitted → `router.shown()`. This is the dashboard's main feed (the bundled page polls it every 1 s).

Top level:

| Key | Type | Meaning |
| --- | --- | --- |
| `engine` | object | Engine/model facts (below) |
| `live` | object | Instantaneous state (below) |
| `requests` | array | Up to **12** finished requests, **newest first** (`REQUESTS_SHOWN`) |
| `best` | object | `{decode?, prefill?}` fastest qualifying request since start; `{}` before any qualifies |
| `hardware` | object | Latest hardware sample (≈1 s old) |
| `hardware_static` | object | Hardware names/versions (sampled once at start) |
| `history` | object | ≤60 one-second samples (≈last 60 s) |
| `models` | array | All configured models (see model entry) |
| `swap` | object \| null | See **swap** |

`engine`:

| Key | Type | Meaning |
| --- | --- | --- |
| `model` | `str` | Same as `live`'s model |
| `max_context` | `int` tokens | `engine.max_context` or, while a restart is under way, the last `known_ctx` |
| *(dynamic keys)* | `int \| str` | Everything the engine printed on its `INFO k=v` line; integer-looking values are JSON numbers, everything else stays a **string**. Keys the server itself relies on: `version`, `engine`, `batch_slots`, `eos_ids` (`"0,1"` string), `text_prompt`, `slot_cache`, `solo_any_slot`, `slot0_solo`, `vram_elastic`, `vram_free_mib` (**MiB**), `expert_slots` (count of expert cache slots). Do not hard-code the set; read what you need. |
| `spill` | `null \| {experts:int, total:int, bytes:int}` | Routed experts that live **only in the file/SSD tier**: count of them, total routed pairs, **bytes** (GiB = `bytes/1024³`). `null` = nothing on SSD. This is the "experts on SSD" warning as data — the stats page turns its Experts block amber off it. It is removed from the `INFO` keys, so `spill_experts`/`spill_total`/`spill_bytes` never appear as separate keys. |
| `expert_total` | `int \| null` | Every routed (layer, expert) pair, on SSD or not (`spill_total` read before the move) |

`live`:

| Key | Type | Meaning |
| --- | --- | --- |
| `state` | `"loading" \| "reading" \| "generating" \| "unloaded" \| "asleep" \| "idle"` | The single word to drive the UI. `reading` = busy, no first token yet (prefill); `generating` = busy with a token out (in parallel mode, if *any* slot decodes, the state is `generating`) |
| `load` | `object \| null` | See **load**; non-null only while `loading` (and typically alongside `state == "loading"`) |
| `queued` | `int` | Requests waiting: engine-side queue depth + this service's waiting requests + turns queued in the router for any model |
| `slots` | `{total:int, active:int, queue_depth:int}` | `total` slots (`max(1, batch)`), `active` running, `queue_depth` waiting |
| `queue` | array of `{api: str\|null, model: str, prompt_tokens: int\|null, waited_s: float}` | The waiters: live requests not yet admitted (`api` set) plus router turns for any model (`api: null`, `prompt_tokens: null`). `waited_s` 1 dp seconds |
| `prompt_tokens` | `int \| null` | Current request's prompt tokens; **`null` unless busy** (parallel mode: the *newest* request's) |
| `generated` | `int \| null` | Tokens generated so far; **`null` unless busy** |
| `elapsed_s` | `float 1dp \| null` | Seconds since the current request started; `null` unless busy with a `started` |
| `tok_s` | `float 1dp \| null` | Decode **tokens/s** over a ~2 s sliding window, summed over parallel requests; **non-null only when `state == "generating"`**, else `null` |
| `prefill_tok_s_mean` | `float \| null` | Engine's mean **tokens/s over newly read tokens** (cached prefix excluded); reported only while `busy` (and the engine has not said anything → `null`) |

`requests[]` (each finished request; absent values are `null`, never missing keys):

| Key | Type | Meaning |
| --- | --- | --- |
| `id` | `str` `"r_0007"` | `r_%04d`, rising since server start |
| `path` | `str \| null` | API path served, e.g. `/v1/chat/completions` |
| `slot` | `int \| null` | 0-based slot it last used |
| `time` | `float` epoch s | Request start |
| `duration_s` | `float 1dp` s | Wall duration |
| `finish` | `"stop" \| "length" \| "cancel" \| "disconnect" \| "error"` | Ends like the console's `end=` |
| `prompt_tokens` | `int` | Prompt tokens actually seen (a prompt read cancelled mid-way counts only what was read) |
| `reused` | `int \| null` tokens | Prefix cache reuse |
| `output_tokens` | `int` | Generated tokens (incl. thinking and closing text) |
| `prompt_ms` | `float \| null` ms | Prompt-read time (engine's `DONE`) |
| `decode_tok_s` | `float 1dp \| null` | Engine decode **tokens/s** for this request |
| `hit_rate` | `float 0..1 \| null` | Expert-cache hit rate (hits/lookups) |
| `pcie_share` | `float 0..1 \| null` | Share of routed experts fetched over PCIe |
| `ssd_share` | `float 0..1 \| null` | Share read from the expert file (SSD) |
| `drafts_offered`/`drafts_accepted` | `int \| null` | Speculative decoding counters |
| `ttft_s` | `float 2dp \| null` s | Time to first token |
| `draft_len` | `float 2dp \| null` | Mean tokens per decoding window |

`best`:

| Key | Type | Meaning |
| --- | --- | --- |
| `best.decode` | `{tok_s: float 1dp, id: str, time: float epoch, output_tokens: int}` | Best decode rate; present only for a request with **≥ 256** output tokens (`BEST_MIN_OUT`) |
| `best.prefill` | `{tok_s: float 1dp, id: str, time: float epoch, read_tokens: int}` | Best prefill rate; present only with **≥ 4096** newly read tokens (`BEST_MIN_READ`) |

`hardware` — **memory in BYTES here** (the stats page divides by 1024³ itself); GPU temps °C, power W, PCIe MB/s:

```
{ "gpus": [ {"index":0, "util":41, "mem_used":22548578304, "mem_total":25264668672,
             "temp":67, "power":298.5, "power_limit":400.0,
             "pcie_gen":4, "pcie_gen_max":5, "pcie_width":16,
             "pcie_rx_mb":1180.4, "pcie_tx_mb":3.2} ],
  "cpu": 34.0,                      # % CPU (1 s sample)
  "ram_used": 27487790694, "ram_total": 68719476736,   # BYTES
  "disk_read_mb": 512.75 }           # MB/s
```

`hardware_static`: `{gpus:[{index:int, name:str|null}], cpu_name:str|null, cores:int|null (physical), threads:int|null (logical), driver:str|null, cuda:str|null ("13.2")}`.

`history` — arrays ≤ **60** entries, oldest first, missing samples `null`, floats rounded to 2 dp:
`tok_s`, `prefill_tok_s_mean`, `slot_tok_s` (**one array per slot index**, padded with `null` for samples taken before that slot existed), and `gpus:[{index, util:[], power:[], temp:[]}]`.

Reconstructed (weights loading, single model, no traffic yet):

```json
{
  "engine": {
    "model": "qwen3-30b-a3b",
    "max_context": 131072,
    "version": "0.9.7",
    "engine": "basalt 0.9.7",
    "batch_slots": 0,
    "eos_ids": "151645",
    "text_prompt": 1,
    "vram_elastic": 1,
    "vram_free_mib": 1024,
    "expert_slots": 6144,
    "spill": { "experts": 184320, "total": 1179648, "bytes": 412316860416 },
    "expert_total": 1179648
  },
  "live": {
    "state": "loading",
    "load": { "kind": "weights", "percent": 63,
              "gib": { "ram": 11.8, "cuda0": 19.4 } },
    "queued": 0,
    "slots": { "total": 1, "active": 0, "queue_depth": 0 },
    "queue": [],
    "prompt_tokens": null,
    "generated": null,
    "elapsed_s": null,
    "tok_s": null,
    "prefill_tok_s_mean": null
  },
  "requests": [
    { "id": "r_0007", "path": "/v1/chat/completions", "slot": 0,
      "time": 1763055131.412, "duration_s": 14.9, "finish": "stop",
      "prompt_tokens": 1024, "reused": 896, "output_tokens": 1720,
      "prompt_ms": 812.4, "decode_tok_s": 119.6,
      "hit_rate": 0.897, "pcie_share": 0.393, "ssd_share": null,
      "drafts_offered": 740, "drafts_accepted": 481,
      "ttft_s": 0.12, "draft_len": 2.91 }
  ],
  "best": {
    "decode": { "tok_s": 121.4, "id": "r_0005", "time": 1763055010.0, "output_tokens": 1980 },
    "prefill": { "tok_s": 2415.6, "id": "r_0007", "time": 1763055131.412, "read_tokens": 4320 }
  },
  "hardware": {
    "gpus": [ { "index": 0, "util": 0, "mem_used": 23162773504, "mem_total": 25264668672,
                "temp": 41, "power": 78.2, "power_limit": 400.0,
                "pcie_gen": 4, "pcie_gen_max": 5, "pcie_width": 16,
                "pcie_rx_mb": 41.2, "pcie_tx_mb": 0.4 } ],
    "cpu": 22.0, "ram_used": 29531209728, "ram_total": 68719476736, "disk_read_mb": 604.13
  },
  "hardware_static": {
    "gpus": [ { "index": 0, "name": "NVIDIA GeForce RTX 4090" } ],
    "cpu_name": "AMD Ryzen 9 7950X 16-Core Processor",
    "cores": 16, "threads": 32, "driver": "560.94", "cuda": "13.0"
  },
  "history": {
    "tok_s": [null, null, 118.9, 121.4, 119.6],
    "prefill_tok_s_mean": [null, null, 2210.4, 2415.6, 0.0],
    "slot_tok_s": [[null, null, 118.9, 121.4, 119.6]],
    "gpus": [ { "index": 0, "util": [0, 0, 96, 99, 97],
                "power": [78.2, 96.7, 312.4, 318.1, 315.9],
                "temp": [41, 42, 63, 67, 67] } ]
  },
  "models": [
    { "id": "qwen3-30b-a3b", "object": "model",
      "status": { "value": "loading" }, "asleep": false,
      "meta": { "n_ctx": 131072 },
      "architecture": { "input_modalities": ["text", "image"], "output_modalities": ["text"] } }
  ],
  "swap": null
}
```

---

### 3.3 `GET /status` — auth. No query params

The engine's live request dict ("it shows the last answer's tail" is why it is keyed — the reply text so far
leaks through). Copied under a lock, with `started` and `first_token` **removed** and three computed fields added.

| Key | Type | Meaning |
| --- | --- | --- |
| `busy` | `bool` | A request is running |
| `queued` | `int` | Requests waiting for the FIFO/turn |
| `phase` | `str` \| *absent until first request* | `"reading the prompt"` → `"thinking"` → `"answering"` → `"writing a tool call: <name>"` → `"tool call complete"` |
| `api` | `"openai" \| "anthropic"` | API serving the current/last request |
| `model` | `str` | Served name of what is running |
| `prompt_tokens` | `int` | Prompt tokens |
| `generated` | `int` | Tokens produced so far |
| `max_tokens` | `int \| null` | Output cap in force for this request (tokens) |
| `slot` | `int \| null` | 0-based slot holding it |
| `reused` | `int \| null` tokens | Cached prefix reused (set once the engine reports it) |
| `tool` | `str \| null` | Tool whose call is being written (`null` outside a call) |
| `tail` | `str` \| *absent when idle* | **Last ≤ 600 characters of the reply text so far.** Present only while busy (popped at the end of a request) |
| `windows` / `window_tokens` | `int` \| *present only with batch decoding* | Window counts used for `draft_len` |
| `elapsed_s` | `float 1dp` | **Added only when `busy`**: seconds since start |
| `tokens_per_s` | `float 1dp` | **Added only when `busy` and a token has come out**: live decode **tok/s** (~2 s window, parallel-summed) |
| `tokens_per_s_mean` | `float 1dp` | **Same condition**: mean **tok/s** since the first token |

Accuracy note: after a request ends, only `busy` is cleared (and `tail`/`tool` popped) — `phase`,
`prompt_tokens`, `generated`, `max_tokens`, `slot`, `reused`, `api`, `model`, `windows`,
`window_tokens` **keep the last request's values** while `busy` is `false`. Before the first request the
body is just `{"busy": false, "queued": 0}`.

Reconstructed (mid-answer):

```json
{
  "busy": true, "queued": 0,
  "phase": "answering",
  "api": "openai", "model": "qwen3-30b-a3b",
  "prompt_tokens": 1024, "generated": 480, "max_tokens": 4096,
  "slot": 0, "reused": 896, "tool": null,
  "tail": "… of the MoE layers stay resident, so prefill dominates here.",
  "windows": 7, "window_tokens": 20,
  "elapsed_s": 4.2, "tokens_per_s": 118.4, "tokens_per_s_mean": 114.3
}
```

---

### 3.4 `GET /slots` — auth. No query params

Returns **only** `slot_table()[0]`: one object per slot, `id` 0 … `total-1` (`total = max(1, engine.batch)`;
`1` without `--parallel`). **`[]` when the engine is not alive.** Per-slot rates/drafts live here, not in `/metrics`.

| Key | Type | Meaning |
| --- | --- | --- |
| `id` | `int` | Slot index, 0-based (console calls it `s<id+1>`) |
| `n_ctx` | `int` tokens | Context this slot can hold (`engine.max_context`) |
| `is_processing` | `bool` | A request occupies the slot |
| `state` | `"idle" \| "prefill" \| "decoding"` | Per-slot activity |
| `api` | `str \| null` | `"openai"` / `"anthropic"`, `null` when idle |
| `model` | `str \| null` | Served name, `null` when idle |
| `tok_s` | `float 1dp` | This slot's decode **tokens/s** (`0.0` when idle) |
| `generated` | `int` | Tokens this request produced |
| `max_tokens` | `int \| null` | This request's output cap |
| `prompt_tokens` | `int` | This request's prompt tokens |
| `prefill_done` | `int \| null` | Prompt tokens read so far while `state == "prefill"` (`min(engine progress, prompt)` when exactly one slot is reading); `0` when idle, `null` when the engine gives no progress or several slots read at once |
| `reused` | `int \| null` tokens | Cached prefix reused by this request |
| `ttft_s` | `float 2dp \| null` s | Time to first token (`null` until the first token) |
| `draft_len` | `float 2dp \| null` | Mean tokens per decoding window (`null` if no windows) |
| `ctx_used` | `int` tokens | Context held in the slot = prompt + generated while running; else the last finished request's seen+generated |
| `drafts_offered` / `drafts_accepted` | `int \| null` | Draft counters: the running batch slot's own if fresher, otherwise **that slot's last finished request's** (`null` when the engine never reported them) |

Reconstructed (`--parallel 2`, one slot busy):

```json
[
  { "id": 0, "n_ctx": 65536, "is_processing": true, "state": "decoding",
    "api": "anthropic", "model": "qwen3-30b-a3b", "tok_s": 96.2,
    "generated": 512, "max_tokens": 4096, "prompt_tokens": 990,
    "prefill_done": 990, "reused": 768, "ttft_s": 0.31, "draft_len": 2.84,
    "ctx_used": 1502, "drafts_offered": 300, "drafts_accepted": 198 },
  { "id": 1, "n_ctx": 65536, "is_processing": false, "state": "idle",
    "api": null, "model": null, "tok_s": 0.0, "generated": 0, "max_tokens": null,
    "prompt_tokens": 0, "prefill_done": 0, "reused": null, "ttft_s": null, "draft_len": null,
    "ctx_used": 12040, "drafts_offered": null, "drafts_accepted": null }
]
```

---

### 3.5 `GET /props` — auth. Optional `?model=<name>` (unknown → `404`)

llama.cpp-compatible properties. Blocking nuance: with a **single** configured model, a running load is
*waited for* (`wait_load`); with several models it is not. Errors:
`503 {"error":{"type":"server_error","message":"…"}}` for `GpuBusy`/`EngineDied`,
`503 {"error":{"message":"the engine is not running"}}` when the engine is neither alive nor cleanly unloaded.

| Key | Type | Meaning |
| --- | --- | --- |
| `default_generation_settings.n_ctx` | `int` tokens | Context |
| `default_generation_settings.params` | object | The model file's sampling defaults, **only keys present**, renamed to llama.cpp names: `temperature`, `top_p`, `top_k`, `min_p`, `seed`, `presence_penalty`, `frequency_penalty`, `repeat_penalty` (from `repetition_penalty`), `repeat_last_n` (from `penalty_last_n`) |
| `default_generation_settings.params.n_predict` | `-1` | Constant: "fill the rest" |
| `total_slots` | `int` | Same as `/metrics live.slots.total` |
| `model_alias` | `str` | Served name |
| `chat_template` | `str` | **Full template source** — can be tens of KB; fetch once and cache |
| `modalities.vision` | `bool` | Image input available |
| `models_autoload` | `bool` | Engine can be restarted automatically on request (`restart` exists) |
| `is_sleeping` | `bool` | `true` when the engine is **not** loaded (includes "unloaded", not only driver-sleep) |
| `model_path` | `str` | *Optional*, only when the engine knows its GGUF path (absolute, backslashes on Windows) |
| `build_info` | `str` | `"basalt <version>"`, *optional* (engine version known) |

Reconstructed:

```json
{
  "default_generation_settings": {
    "n_ctx": 131072,
    "params": { "temperature": 0.7, "top_p": 0.8, "top_k": 20, "min_p": 0.05,
                "repeat_penalty": 1.0, "repeat_last_n": 64, "n_predict": -1 }
  },
  "total_slots": 1,
  "model_alias": "qwen3-30b-a3b",
  "chat_template": "{% for message in messages %}…",
  "modalities": { "vision": true },
  "models_autoload": true,
  "is_sleeping": false,
  "model_path": "D:\\models\\Qwen3-30B-A3B-IQ3_S.gguf",
  "build_info": "basalt 0.9.7"
}
```

---

### 3.6 `GET /api/logs` — auth. Paging query params

Backed by a ring buffer of the last **2000** lines (`logbuf.CAPACITY`), ANSI stripped,
each line cut at **2000 characters**.

| Param | Default | Behaviour |
| --- | --- | --- |
| `after` | absent (= `0`; negative clamps to 0) | Only lines with `id > after` |
| `limit` | **300** when `after` is omitted (`LOG_FIRST_LINES`), else **500** | Capped at **500** per answer (`LOG_MAX_LINES`) |
| *(both)* | non-numeric values fall back to the default, no error | |

Response `{"lines": [...], "last": <int>}` where `lines` are **oldest first** (newest last):

| Key | Type | Meaning |
| --- | --- | --- |
| `id` | `int` | Strictly rising, starts at 1; **resets to 0 when the server restarts** |
| `t` | `float` epoch s (3 dp) | Time the line was buffered (one timestamp per multi-line block) |
| `src` | `"srv" \| "eng"` | Server `print()`/stderr (`srv`, its `[server] ` prefix is already stripped from `msg`) vs engine output/log (`eng`) |
| `lvl` | `"info" \| "warn" \| "err"` | From markers only (`util/loglevel.py`): `[server] warning:` / `error:`, `basalt <area>: warning: …`, and whole Python traceback blocks are `err` |
| `msg` | `str` | ≤ 2000 chars, no ANSI, no trailing whitespace |
| `last` | `int` | Newest id in the buffer **whether or not it was returned** |

Paging rules that matter to the app:

* First call: `GET /api/logs` → up to 300 oldest-available lines; then poll `?after=<last>` (limit defaults to 500, enough for ~1 s of load output).
* Detect a restart (ids reset) with `last < previousLast` — the bundled page clears the view and says "The server restarted.".
* Because the ring is 2000 lines, ids can gap under heavy output: on a gap, re-sync from `?after=0` if the in-memory view must be complete.

Reconstructed:

```json
{
  "lines": [
    { "id": 812, "t": 1763055131.42, "src": "srv", "lvl": "info",
      "msg": "swap qwen3-30b-a3b -> deepseek-r1-14b (1 waiting)" },
    { "id": 813, "t": 1763055132.01, "src": "eng", "lvl": "warn",
      "msg": "basalt mmproj: vision encoder started without a dedicated GPU" },
    { "id": 814, "t": 1763055140.33, "src": "srv", "lvl": "info",
      "msg": "loading weights into cuda0 [=====                 > ]  31.0/122.6 GiB  1450 MB/s" }
  ],
  "last": 814
}
```

---

### 3.7 `GET /v1/models` and `GET /models` — auth. No params

Both paths answer identically: `{"object": "list", "data": [ <model entry>, … ]}` — every configured model,
loaded or not, in configured order. See **model entry** in §2.

Reconstructed (two models, a failed one):

```json
{
  "object": "list",
  "data": [
    { "id": "qwen3-30b-a3b", "object": "model", "status": { "value": "loaded" },
      "asleep": false, "meta": { "n_ctx": 131072 },
      "architecture": { "input_modalities": ["text", "image"], "output_modalities": ["text"] } },
    { "id": "deepseek-r1-14b", "object": "model",
      "status": { "value": "failed",
                  "error": "deepseek-r1-14b did not load: the GPU is in use by another program: 2100 MiB of VRAM free, the model needs 18000 (min_free_vram_mib) - it stays unloaded until that is free" },
      "asleep": false, "meta": { "n_ctx": 65536 },
      "architecture": { "input_modalities": ["text"], "output_modalities": ["text"] } }
  ]
}
```

---

### 3.8 `GET /v1/status` — auth. `{"model": …}` is **not** supported

`{…Service.v1_status(), "models": […], "swap": …}` — the compact, client-oriented status blob (shown model only).

| Key | Type | Meaning |
| --- | --- | --- |
| `service` | `"basalt"` | Constant |
| `model` | `str` | Shown model |
| `loaded` / `loading` | `bool` | Engine alive / load in progress |
| `load` | `object \| null` | See **load** |
| `asleep` | `bool` | Driver-suspended engine (Linux sleep feature) |
| `auto_load` | `bool` | The engine is (re)started on demand |
| `structured_output` | `{formats: ["json_object","json_schema"], method: "grammar", constrained_decoding: bool, stream_buffered: false}` | `constrained_decoding` = this engine enforces grammars |
| `engine` | `str \| null` | Engine version string only |
| `started` | `int` epoch s | Service start |
| `uptime_s` | `int` s | Since `started` |
| `cache_max_tokens` | `int` tokens | == `context.native` == `context.max_positions` |
| `context` | `{native:int, max_positions:int}` | Context, tokens |
| `concurrency` | `{serving:int, requested:int}` | Slots actually in use (`max(1, batch)`) — both equal today |
| `dialects` | `str[]` | Always `["/v1/chat/completions","/v1/messages","/v1/responses"]` |
| `vision` | `{enabled: bool, available: bool, error: null}` | `error` is always `null` (typed as a future field) |
| `activity.requests` | `int` | Finished requests + current one |
| `activity.in_flight` | `int` | Running + queued (a cheap busy signal) |
| `activity.last_request_at` | `int \| null` epoch s | Last request start/finish |
| `last_timings` | `null \| object` | Last request's timings in llama.cpp names, plus `at` (int epoch s): `cache_n`, `prompt_n`, `prompt_ms` (ms), `prompt_per_token_ms`, `prompt_per_second` (**tok/s**), `predicted_n`, `predicted_ms`, `predicted_per_token_ms`, `predicted_per_second` (**tok/s**), and only when draft counts were reported: `draft_n`, `draft_n_accepted` |
| `vram` | `object` | The engine's last `VRAM k=v` figures (values are MiB **ints**, or **strings** when the engine printed a non-integer) plus `elastic: bool` (started with `--vram-elastic`, so `POST /v1/vram` will work) |
| `machine.at` | `int` epoch s | When this reading was taken |
| `machine.gpu` | `null \| {name: str, used_mib: int, total_mib: int, util_pct: int 0..100, temp_c: int, power_w: float}` | First GPU only; **MiB**, already scaled |
| `machine.ram` | `null \| {used_gib: float 1dp, total_gib: float 1dp}` | **GiB**, already scaled |
| `models` | `array` | Model entries (see §2) |
| `swap` | `object \| null` | See **swap** |

`machine.gpu` is `null` when no GPU name is known; `machine.ram` is `null` when RAM total is unreadable.

Reconstructed (idle, one finished request):

```json
{
  "service": "basalt", "model": "qwen3-30b-a3b",
  "loaded": true, "loading": false, "load": null, "asleep": false, "auto_load": true,
  "structured_output": { "formats": ["json_object", "json_schema"], "method": "grammar",
                         "constrained_decoding": true, "stream_buffered": false },
  "engine": "basalt 0.9.7",
  "started": 1763054900, "uptime_s": 412,
  "cache_max_tokens": 131072,
  "context": { "native": 131072, "max_positions": 131072 },
  "concurrency": { "serving": 1, "requested": 1 },
  "dialects": ["/v1/chat/completions", "/v1/messages", "/v1/responses"],
  "vision": { "enabled": true, "available": true, "error": null },
  "activity": { "requests": 7, "in_flight": 0, "last_request_at": 1763055146 },
  "last_timings": { "cache_n": 896, "prompt_n": 128, "prompt_ms": 812.4,
                    "prompt_per_token_ms": 6.347, "prompt_per_second": 157.6,
                    "predicted_n": 1720, "predicted_ms": 14381.2,
                    "predicted_per_token_ms": 8.361, "predicted_per_second": 119.6,
                    "draft_n": 740, "draft_n_accepted": 481, "at": 1763055146 },
  "vram": { "vram_free_mib": 1024, "expert_slots": 6144, "expert_cache_mib": 18432,
            "expert_cache_full_mib": 18944, "elastic": true },
  "machine": { "at": 1763055312,
               "gpu": { "name": "NVIDIA GeForce RTX 4090", "used_mib": 22068,
                        "total_mib": 24096, "util_pct": 0, "temp_c": 39, "power_w": 74.3 },
               "ram": { "used_gib": 27.5, "total_gib": 64.0 } },
  "models": [ { "id": "qwen3-30b-a3b", "object": "model", "status": { "value": "loaded" },
                "asleep": false, "meta": { "n_ctx": 131072 },
                "architecture": { "input_modalities": ["text", "image"], "output_modalities": ["text"] } } ],
  "swap": null
}
```

---

### 3.9 `POST /load` — auth + `_own_page` (JSON content type, no foreign Origin)

Body is **ignored** (read and discarded, ≤ 64 KiB, `Connection` handling is done so the client sees no reset).
Send `Content-Type: application/json` and an empty body (chunked or `Content-Length: 0`).

**Blocking:** the handler takes its router turn, waits for the model to be loaded — which can include *another*
model's swap — and returns only after `load()` finished. A client with a 30–600 s timeout is normal here
(a cold multi-hundred-GiB load takes minutes; the engine gives up after `ENGINE_READY_S = 900 s`).

Response `200 {"status": "loaded"}`.
Errors: `503 {"error":{"type":"server_error","message":"…"}}` (`GpuBusy` VRAM check, `EngineDied`),
`401`, `403`, `415`, `413`, `400 {"error":{"message":"incomplete control request body"}}`.

Reconstructed: `{"status": "loaded"}`

### 3.10 `POST /unload` — auth + `_own_page`

Non-blocking; returns immediately. `200 {"status": <str>}` or **`409 {"status": "busy"}`** when a request
runs/queues. Status values from `Service.unload()`:

| `status` | Meaning |
| --- | --- |
| `"unloaded"` | Engine stopped; the next request re-loads it |
| `"not loaded"` | It was already stopped |
| `"busy"` | A request is running or queued → HTTP **409** |
| `"unsupported"` | This engine cannot be unloaded (no `unload`) |

Reconstructed: `{"status": "unloaded"}`

### 3.11 `POST /v1/load` — auth + `_foreign_page` + `_own_page`

Body (optional JSON object): `{"model": "<name>"}` — omitted, `null`, or unknown-name semantics:
`null`/absent means the shown model, an unknown name → `404 {"error":{"message":"model not found"}}`.
This is the **model-switch** call: it waits its turn in the router, swaps models if needed, and (for a
load already in progress) simply waits for it. Also `409 {"error":{"type":"model_busy","message":"a request is running or queued"}}`
when the target service is busy and no queueing is possible.

Response `200` = `{"status": "loaded"}` merged with the whole `v1_status()` object (§3.8 without
`models`/`swap`), so the client can refresh its whole dashboard from the body.

Reconstructed (trimmed):

```json
{ "status": "loaded", "service": "basalt", "model": "deepseek-r1-14b",
  "loaded": true, "loading": false, "load": null, "asleep": false, "auto_load": true,
  "uptime_s": 2014, "machine": { "at": 1763057000, "gpu": null, "ram": { "used_gib": 41.2, "total_gib": 64.0 } } }
```

### 3.12 `POST /v1/unload` — same auth/params as `/v1/load`

`200 {"status": "unloaded" | "not loaded" | "unsupported", …v1_status}`; busy → **`409 {"error":{"type":"model_busy","message":"a request is running or queued"}}`**.
If the engine was asleep it is woken first, then unloaded.

### 3.13 `POST /v1/vram` — auth + `_foreign_page` + `_own_page`

Resize the elastic expert cache so the GPU keeps a free reserve. Requires `--vram-elastic`
(otherwise `400 {"error":{"type":"invalid_request_error","message":"this engine cannot resize its VRAM use"}}`).
With several models configured the reserve is stored for **all** of them and applied when each loads
("the reserve is the machine's").

Body: `{"reserve_mib": <int ≥ 0> | null}` — `null` restores the reserve from server start.
Anything else (bool, float, negative, non-int) →
`400 {"error":{"type":"invalid_request_error","message":"reserve_mib: a whole number of MiB (0 or more), or null for the start's"}}`.
Note Windows/Linux refuse a reserve below **256 MiB** at configuration time (`docs/running.md`).

Waits up to **300 s** (`vram_wait_s`) for the FIFO and control lines: `409 {"error":{"type":"model_busy","message":"a request is still running; try again when it has finished"}}`.
Also `503 {"error":{"type":"server_error","message":"…"}}` on `EngineDied`.

Response, loaded engine: `200 {"status": "ok", …}` where the extra keys come verbatim from the engine's
`VRAM k=v` line — values are **ints in MiB** when the engine printed a plain integer, otherwise **strings**.
Known keys: `vram_free_mib` (MiB free after the resize), `expert_slots` (experts in the cache),
`expert_cache_mib`, `expert_cache_full_mib` (both MiB, optional). Two of these (`expert_slots`, `vram_free_mib`)
are also copied into `/metrics engine`.

Response, engine not loaded (deferred):

```json
{ "status": "not loaded", "reserve_mib": 1536, "note": "applied when the model loads" }
```

Reconstructed, loaded:

```json
{ "status": "ok", "vram_free_mib": 1536, "expert_slots": 5920,
  "expert_cache_mib": 17408, "expert_cache_full_mib": 18944 }
```

### 3.14 `POST /v1/chat/completions` — exists (generation; out of scope here)

OpenAI-compatible chat: JSON body (`model`, `messages`, `tools`, `stream`, `max_tokens`/`max_completion_tokens`,
`response_format`, sampling fields…), `stream: true` gives SSE
(`data: …` chunks ending with `data: [DONE]`, a `{"error": …}` chunk on mid-stream failure).
The same server also serves `POST /v1/messages` (+ `/v1/messages/count_tokens`) for Anthropic clients,
`POST /v1/responses` for the OpenAI Responses API (its own error shape; `GET`-style `/v1/responses/<id>`
404s because nothing is stored). Requests pick their model by `model`, are queued behind the router turn,
and a prompt that does not fit answers `400`, `503 {"error":{"type":"server_error","message":"the engine is starting (a minute or two); try again shortly"}}`
while the engine starts, and `503` for `GpuBusy`.

---

## 4. Units and gotchas, one place

| Thing | Unit / scale | Where |
| --- | --- | --- |
| GPU `mem_used` / `mem_total` | **bytes** | `/metrics hardware.gpus[]` |
| VRAM figures | **MiB** | `/v1/vram`, `/metrics engine.vram_free_mib`, `/v1/status vram`, `/v1/status machine.gpu.used_mib/total_mib` |
| RAM figures | **bytes** in `/metrics hardware`, **GiB (1 dp)** in `/v1/status machine.ram` | |
| `load.gib` | **GiB (1 dp)** per target | `/metrics live.load.gib`, `/v1/status load.gib` |
| `spill.bytes` | **bytes** (convert to GiB yourself) | `/metrics engine.spill.bytes` |
| Percentages | `percent` = integer **0..99** (never 100). GPU `util`/`util_pct` = integer **0..100 %**. `hit_rate`, `pcie_share`, `ssd_share` = **0..1** fraction (×100 for display) | |
| Speeds | **tokens/s** everywhere: `live.tok_s`, `live.prefill_tok_s_mean`, `requests[].decode_tok_s`, `best.*.tok_s`, `/status tokens_per_s(_mean)`, `slots[].tok_s`, `history.tok_s`, `last_timings.*_per_second` | |
| Time | `*_s` = seconds; `*_ms` = milliseconds; `time`, `t`, `at`, `started`, `last_request_at`, `uptime_s` = epoch seconds (only `started`/`time`/`t` carry fractions) | |
| `null` vs missing | Status/dict fields are `null`, not absent, when idle. Actually-optional keys: `/status` extras (added only while busy), `/props model_path`, `build_info`, `models[] .status.error`, `last_timings.draft_n(_accepted)`, `load.percent`/`load.gib`, `best.decode`/`best.prefill` | |
| HTTP status meanings | `401` key missing/wrong · `403` Host or Origin refused (no-key server) · `415` control POST without `application/json` · `404` unknown path or unknown `model` · `409` model busy (incl. `{"status":"busy"}` from `POST /unload`) · `413` body too large · `503` engine trouble (`GpuBusy`, `EngineDied`, `EngineStuck`, engine starting) | |

---

## 5. Recommended polling plan (native Windows client)

Assume: one background poll loop, no `Origin`/`X-Forwarded-*` headers, always
`Authorization: Bearer <key>` when `GET /health` says `api_key: true`, and always
`Content-Type: application/json` on POSTs. Because the server is HTTP/1.0, either send
`Connection: keep-alive` to reuse sockets or accept one connection per poll.

| Cadence | Call | Why |
| --- | --- | --- |
| **2 s** (while offline: back off 2 → 4 → 8 → 15 s max) | `GET /health` (no key) | Liveness, `api_key` flag, `loading`/`loaded`, `percent`, `swap`. The key-free route, so it also tells you *whether* a key is needed. |
| **1 s** (foreground only; pause when hidden) | `GET /metrics` | Everything: `live.state`, `queued`, `slots`, `queue`, `requests[]`, `hardware`, `history`, `models`, `swap`, `best`. The bundled page uses exactly 1 s. Optionally `GET /slots?…` right after (also 1 s) for per-slot rates — the page does the same second call. |
| **1 s** | `GET /api/logs?after=<last>` | Console tail + warnings (`last` cursor). Stop polling (or drop to 5 s) while the log panel is collapsed; re-sync from `after=0` after a gap. |
| **5–10 s** | `GET /v1/models`, `GET /props` (once per model, cache `chat_template`) | Model picker + capability matrix (`models_autoload`, `is_sleeping`, modalities, `default_generation_settings`). |
| **5 s** (only if you don't want `/metrics`) | `GET /v1/status` | A compact single blob: `activity.in_flight`, `machine`, `vram.elastic`, `last_timings`, `load`, `swap`, `models`. Good for a tray tooltip. `/status` is the raw live dict (and leaks `tail`) — prefer it only in a "watch this request" view. |
| On demand | `POST /v1/load`/`/v1/unload`, `POST /v1/vram` | Not polled; they return the new `v1_status()` in full, so refresh the dashboard from the response instead of an extra GET. `POST /load`/`/unload` are the non-model-picking variants (`/load` blocks until loaded). |

Detect these, specifically:

* **Server up / down:** `GET /health` returns 200 with `status:"ok"`. Timeout/refused/reset ⇒ offline.
  `401` on other routes ⇒ up but locked (ask for the key; `health.api_key` confirms).
  `403 {"error":{"type":"forbidden","message":"Host … is not allowed (DNS rebinding protection) …"}}` ⇒ you
  reached it with a bad `Host`: set `Host: localhost:8080` (or the literal address) or tell the user to add `--allowed-host`.
* **Model loading %, and how far:** poll `/metrics` and use `live.state == "loading"`,
  `live.load.kind` (`"wake"` = seconds, show "Waking up", **no percent bar**; `"weights"` = show percent),
  `live.load.percent` (**0..99** — treat "100 %" as "state left loading", not as a reached value; fall back to
  `live.load.gib` sums when percent is `null`), and `/health.percent` as a key-free coarse signal.
  A failed load surfaces in the next request as `503`, and in `/v1/models` as `status.value == "failed"`
  with `status.error`.
* **Swap in progress:** `swap != null` (in `/health`, `/metrics`, `/v1/status`): `swap.from` → `swap.to`,
  `swap.phase` (`"unloading"` then `"loading"`), `swap.elapsed_s`, `swap.waiting`. Note `/metrics` and `/v1/status`
  describe the *target* model while swapping (`router.shown()`), so don't treat the `model` field flipping as an error.
* **Idle vs busy:** `/metrics live.state ∈ {idle, reading, generating, unloaded, asleep, loading}` and
  `live.slots.active`/`live.queued`; lighter: `/v1/status activity.in_flight`; rawest: `/status busy`/`queued`
  (remember `/status` keeps stale per-request fields after the request ends — use `busy`, not `phase`).
* **Queue depth:** `live.queued` (it already folds in router turns for other models) and the `live.queue`
  list with each waiter's `waited_s` for "waiting since…" UI.
* **RAM/VRAM guard warnings:**
  * *Experts on SSD* (the one amber warning): read `/metrics engine.spill` — non-`null` means experts are on
    the SSD tier: `{experts, total, bytes}` → `<experts> experts on SSD (<bytes/1024³> GiB)`; combine with
    `requests[0].ssd_share` / `hit_rate` / `pcie_share` for the VRAM/peer/CPU/SSD split. `engine.expert_total`
    vs `engine.expert_slots` tells "all experts in VRAM".
  * *GPU in use, load refused (`GpuBusy`):* poll `/health` `loaded:false` + `loading:false` with
    `/v1/models status.value == "failed"` (`status.error` contains `the GPU is in use by another program: <free> MiB of VRAM free, the model needs <min>`),
    plus `lvl:"err"` log lines; generation attempts fail with `503`.
  * *RAM guard / `RAM floor`:* engine lines `lvl == "warn"` (`RAM floor …` are printed as tagged `ram` lines)
    and err lines while serving; per the docs a request waits up to 30 s for RAM, and a running request only
    errors when free RAM goes under ~1 GiB (or below the reserve under pressure) — show it from the log tail
    with `lvl:"err"` and the request's `finish: "error"` in `/metrics requests[]`.
  * *VRAM headroom for your own guard:* `/metrics hardware.gpus[].mem_used/mem_total` (**bytes**) and
    `/v1/status machine.gpu.used_mib/total_mib` (**MiB**); warn before offering `POST /v1/vram` with a reserve
    below 256 MiB on Windows.
* **Server restarted (ids/uptime reset):** `api/logs` `last < previous last` (ids restart at 0), or
  `/v1/status uptime_s` going down. Then reset the log view, `best`, and history charts.
* **History charts:** `history.tok_s`, `history.prefill_tok_s_mean`, `history.slot_tok_s[i]`,
  `history.gpus[*].util|power|temp` — ≤ 60 one-second samples, oldest→newest, `null` = no sample: break the
  line at `null`s rather than drawing to 0.
