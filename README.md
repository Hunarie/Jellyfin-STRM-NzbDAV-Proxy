# Jellyfin NZBDav Proxy

A Jellyfin plugin that proxies media stored on an **internal NZBDav** instance
through Jellyfin itself, so remote players such as **Infuse** can stream and
**seek** without:

- exposing NZBDav to the public internet,
- running `rclone`, or
- writing custom reverse-proxy rules.

NZBDav writes `.strm` files whose URL points at the internal Docker hostname
(e.g. `http://nzbdav:3000/...`), which a remote player can't reach. This plugin
adds a `/NZBDavProxy/Stream/...` endpoint to your Jellyfin server. You rewrite
the `.strm` files to point at that endpoint; when Infuse requests the file,
Jellyfin fetches it from NZBDav over the internal Docker network and pipes the
bytes back to the client.

```
Infuse ──HTTPS──> Jellyfin (tv.jaydinleeman.com)
                     │  /NZBDavProxy/Stream/<path>?api_key=...
                     ▼
                  NzbDavProxyController  ──HTTP──>  NZBDav (http://nzbdav:3000)
                     │  (streams bytes back, forwarding Range headers)
                     ▼
                  Infuse receives 206 Partial Content and seeks freely
```

## How it meets the requirements

| Requirement | Where |
| --- | --- |
| .NET 9 / Jellyfin 10.11 architecture | `Jellyfin.Plugin.NzbDavProxy.csproj` targets `net9.0`, references `Jellyfin.Controller` 10.11.x |
| No full-file buffering | Controller uses `HttpCompletionOption.ResponseHeadersRead` + `Stream.CopyToAsync` |
| Seamless seek/scrub | Forwards the client's `Range`/`If-Range` headers to NZBDav and relays the `206 Partial Content` response verbatim (see note below) |
| Secured with Jellyfin auth | Controller is decorated with `[Authorize(Policy = "DefaultAuthorization")]` |

### A note on `enableRangeProcessing`

The brief asked for `File(..., enableRangeProcessing: true)`. That overload only
produces real range/seek behaviour when the source `Stream` is **seekable**, so
ASP.NET can read its `Length` and slice it. A live network stream piped from
NZBDav is **not** seekable — its length is unknown until fully read — so
`enableRangeProcessing` would silently fall back to sending the whole file
(`200 OK`) and break scrubbing, while also defeating the no-buffering goal.

To deliver the *intent* of that requirement — seamless seeking with no buffering
— this plugin instead **forwards the client's `Range` header upstream** and
relays NZBDav's `206 Partial Content` response (status, `Content-Range`,
`Content-Length`, `Accept-Ranges`). NZBDav is itself a range-capable media
server, so this gives Infuse true, instant seeking while memory usage stays flat
regardless of file size. This is the correct production pattern for a streaming
reverse proxy.

---

## 1. Project setup (scaffolding from the official template)

You don't need to scaffold from scratch — this repo already contains a complete
project. This section documents how it was structured and how to regenerate an
equivalent skeleton if you want to start fresh.

The official template is distributed as a `dotnet new` template:

```bash
# Install the .NET 9 SDK first: https://dotnet.microsoft.com/download/dotnet/9.0

# Install Jellyfin's plugin template
dotnet new install Jellyfin.Plugin.Template

# Scaffold a new plugin
dotnet new jellyfin-plugin -n Jellyfin.Plugin.NzbDavProxy
```

That generates the same shape you see here:

```
Jellyfin-STRM-NzbDAV-Proxy/
├── Jellyfin.Plugin.NzbDavProxy/
│   ├── Jellyfin.Plugin.NzbDavProxy.csproj   # net9.0, references Jellyfin.Controller
│   ├── Plugin.cs                            # BasePlugin<PluginConfiguration>, IHasWebPages
│   ├── Api/
│   │   └── NzbDavProxyController.cs         # the /NZBDavProxy streaming endpoint
│   └── Configuration/
│       ├── PluginConfiguration.cs           # NZBDav base URL + timeout
│       └── configPage.html                  # dashboard settings page
├── scripts/
│   └── rewrite-strm.sh                       # bulk .strm rewriter
├── build.yaml                                # jprm packaging manifest
└── README.md
```

**Dependency injection note:** the controller depends on `IHttpClientFactory`,
which Jellyfin already registers in its DI container — so it is constructor-
injected with **no extra wiring**. Jellyfin auto-discovers any `[ApiController]`
in the plugin assembly, so the endpoint is live as soon as the DLL is loaded; no
`IPluginServiceRegistrator` is required. (If you later need to register your own
services, add a class implementing `IPluginServiceRegistrator` and Jellyfin will
call it at startup.)

---

## 2. Build instructions

### Option A — build locally with the .NET 9 SDK

```bash
cd Jellyfin-STRM-NzbDAV-Proxy
dotnet restore
dotnet build Jellyfin.Plugin.NzbDavProxy/Jellyfin.Plugin.NzbDavProxy.csproj -c Release
```

The compiled plugin is at:

```
Jellyfin.Plugin.NzbDavProxy/bin/Release/net9.0/Jellyfin.Plugin.NzbDavProxy.dll
```

### Option B — produce a packaged ZIP with `jprm` (optional)

```bash
python3 -m pip install jprm
jprm --verbosity debug plugin build .
```

### Option C — let GitHub Actions build it for you

If you don't want to install the SDK, push to GitHub and grab the artifact from
the **Build plugin** workflow run (see `.github/workflows/build.yml`). Download
the `Jellyfin.Plugin.NzbDavProxy` artifact — it contains the `.dll`.

---

## 3. Install into Jellyfin

1. On your Jellyfin server, find the plugins data directory (it is
   `<data>/plugins`, e.g. `/config/plugins` in the LinuxServer/official Docker
   images).
2. Create a folder for the plugin and drop the DLL in:

   ```bash
   mkdir -p /config/plugins/NzbDavProxy
   cp Jellyfin.Plugin.NzbDavProxy.dll /config/plugins/NzbDavProxy/
   ```

   (If you built a ZIP with `jprm`, unzip its contents into that folder instead.)
3. **Restart Jellyfin.**
4. Go to **Dashboard → Plugins → NZBDav Proxy** and confirm the **NZBDav base
   URL** is correct (default `http://nzbdav:3000`). This must be reachable from
   the **Jellyfin** container — if both run in the same Docker network, the
   service name works directly. Save.

> Docker note: make sure the Jellyfin and NZBDav containers share a network so
> Jellyfin can resolve `nzbdav`. With docker-compose they do by default.

---

## 4. Rewrite your `.strm` files

Use `scripts/rewrite-strm.sh`. It replaces the internal origin with your public
Jellyfin proxy path and appends a Jellyfin API key for authentication.

1. Create an API key in **Dashboard → Advanced → API Keys → +**.
2. Preview first with a dry run (no files are changed):

   ```bash
   export JELLYFIN_API_KEY=your_key_here
   export JELLYFIN_URL=https://tv.jaydinleeman.com    # placeholder — change to yours
   DRY_RUN=1 ./scripts/rewrite-strm.sh /path/to/media
   ```

3. Apply it for real:

   ```bash
   ./scripts/rewrite-strm.sh /path/to/media
   ```

The script:

- finds every `*.strm` under the directory (handles spaces/unicode in paths),
- rewrites `http://nzbdav:3000/<path>` → `https://tv.jaydinleeman.com/NZBDavProxy/Stream/<path>?api_key=<key>`,
- correctly uses `&api_key=` if the original URL already had a query string,
- skips files that don't reference the internal origin,
- writes a `*.strm.bak` backup of each changed file.

Example transformation:

```
# before
http://nzbdav:3000/content/Movies/Example (2024)/Example.mkv

# after
https://tv.jaydinleeman.com/NZBDavProxy/Stream/content/Movies/Example (2024)/Example.mkv?api_key=YOURKEY
```

Once playback is confirmed, remove the backups:

```bash
find /path/to/media -name '*.strm.bak' -delete
```

---

## 4b. Automate it for new downloads (post-processing hook)

NZBDav writes a fresh `.strm` with the internal `nzbdav:3000` URL for every new
download, so the rewrite needs to run on an ongoing basis. Two helper scripts
handle this:

- `scripts/rewrite-strm-file.sh <file>` — rewrites a single `.strm` in place.
  Idempotent (skips already-rewritten files), writes atomically. This is the
  hook handler.
- `scripts/watch-strm.sh <media-dir>` — does an initial sweep of existing files,
  then watches the tree and rewrites each new `.strm` the instant it appears
  (via `inotifywait` if installed, otherwise a 15-second polling fallback).

### Setup

1. Create a persistent config (survives reboots on Unraid):

   ```bash
   cp scripts/nzbdav-proxy.conf.example /boot/config/nzbdav-proxy.conf
   nano /boot/config/nzbdav-proxy.conf    # set JELLYFIN_API_KEY and MEDIA_DIR
   ```

2. Run the watcher as a background service. On Unraid, use the **User Scripts**
   plugin: create a script with the body below and schedule it
   **At Startup of Array**:

   ```bash
   #!/bin/bash
   /path/to/repo/scripts/watch-strm.sh &
   ```

   (The watcher reads `MEDIA_DIR` and the rest from `/boot/config/nzbdav-proxy.conf`.)

### Alternative hook points

The same `rewrite-strm-file.sh` can be driven by your existing pipeline instead
of the watcher:

- **Sonarr/Radarr** → Settings → Connect → **Custom Script**, On Import / On
  Upgrade. Point it at `rewrite-strm-file.sh` and pass the imported file path
  (`$radarr_moviefile_path` / `$sonarr_episodefile_path`).
- **Any NZBDav/SAB post-processing** that can run a command with the produced
  file path.

---

## 5. Verify

Test the endpoint directly (replace the host, path, and key):

```bash
# Should return 206 and a Content-Range header => seeking works
curl -I -H "Range: bytes=0-1023" \
  "https://tv.jaydinleeman.com/NZBDavProxy/Stream/content/Movies/Example/Example.mkv?api_key=YOURKEY"
```

Then point Infuse at your Jellyfin server and play a rewritten title — scrubbing
should be instant. Without a valid `api_key` the endpoint returns `401`.

## Security

The endpoint is gated by `[Authorize(Policy = "DefaultAuthorization")]`, so it
requires a valid Jellyfin session token or API key. The proxy only ever talks to
the single configured NZBDav base URL and forwards the captured path beneath it;
it does not let callers reach arbitrary hosts. Treat the API key embedded in
`.strm` files as a credential — anyone with it can stream your library.
