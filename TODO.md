# AiVideoStudio Project Roadmap (Minimax H3 Edition)

## Phase 1: Local AI Environment & Dependency Setup
- [x] **Ollama Setup:**
  - [x] Verify local Ollama server running on `http://127.0.0.1:11434`.
  - [x] Pull target director LLM (e.g., `llama3.1`, `qwen2.5`, or `mistral`).
- [x] **ComfyUI Environment Preparation:**
  - [x] Ensure ComfyUI startup command does **NOT** contain `--no-cache` (verified via process cmdline 2026-10-02).
  - [x] Install missing core custom nodes via ComfyUI Manager: (proven — Seed Hunter renders end-to-end)
    - [x] `ComfyUI-KJNodes` (preview & VRAM monitor)
    - [x] `ComfyUI-Frame-Interpolation` (RIFE 48/60 FPS)
    - [x] `ComfyUI-VideoHelperSuite` (VHS video load/combine)
    - [x] `Comfy Kitchen` (sparse block attention & speed optimizations)
  - [x] Manually clone non-registry custom nodes (proven — workflow loads all of these):
    - [x] `LBH-123-AI/Comfyui_Minimax_h3_latent_Upscaler`
    - [x] `Adudeguyman/ComfyUI-Fantastic-MiniMaxH3-PromptBuilder`
    - [x] `ethanfel/ComfyUI-H3-Prompt-IDE`
    - [x] `Luisacaotica/ComfyUI-MiniMaxH3Mod`
    - [x] `pixaroma/ComfyUI-Pixaroma`
- [x] **Model Weights & Checkpoints:** (proven — renders succeed)
  - [x] Place MiniMax H3 base diffusion checkpoint & JSON config in `models/checkpoints/`.
  - [x] Place Dareties/Silver Oxides LightX2V Turbo LoRA in `models/loras/`.
  - [x] Place Kijai int8 Video VAE (`minimax_h3_video_vae_int8_convrot.safetensors`) in `models/vae/`.
  - [x] Place H3 latent upscale model in `models/latent_upscale_models/`.
  - [x] (Optional) Place `taeh3.safetensors` in `models/vae_approx/` for real-time decoding previews.
- [ ] **Workflow Export:**
  - [ ] Open Minimax H3 Seed Hunter v2.0 workflow in browser.
  - [ ] Enable ComfyUI Dev Mode.
  - [ ] Export API templates:
    - [ ] `minimax_h3_single_pass_api.json` (Production mode: 1.0–1.5 MP, 6–8 turbo steps).
    - [x] `minimax_h3_seed_hunter_api.json` (Fast preview: 0.4 MP -> 1.2 MP upscale). ← bundled in app
    - [x] Portrait Seed Hunter v21 (`minimax_h3_seed_hunter_v21_portrait_api.json`, 9:16, flattened) ← current app default
    - [ ] `minimax_h3_continuation_api.json` (Video extension mode with overlap frame handling). ← optional now: chaining reuses prior clip as Video-1 reference in any template

---

## Phase 2: .NET MAUI Solution Architecture
- [x] Initialize .NET MAUI project targeting Windows/macOS desktop.
- [x] Bundle static FFmpeg executable into application resources (`Assets/ffmpeg.exe`). ← DEVIATION: uses system ffmpeg/ffprobe on PATH + Settings override; no vendored binary.
- [x] Implement MVVM foundation (`BaseViewModel`, `RelayCommand`, property change notifications).
- [x] Configure dependency injection in `MauiProgram.cs`:
  - [x] `IOllamaService`
  - [x] `IComfyUiService`
  - [x] `IVideoMergeService`
  - [x] `IYouTubePublisher` / `ITikTokPublisher` / `IFacebookPublisher` ← stubs only, throw SetupHint until dev apps registered
- [x] Configure local SQLite or LiteDB for saving projects, scripts, generated clip paths, and tokens. ← DEVIATION: flat JSON `GenerationStore` (stdlib, no NuGet); tokens in SecureStorage still pending
- [x] Implement system health-check poller for ports `11434` (Ollama) and `8188` (ComfyUI). ← 30s timer + ComfyUI-KA auto-launch

---

## Phase 3: Ollama Story & Prompt Engineering Engine
- [x] Implement `OllamaService` (`POST /api/chat`) with strict JSON schema serialization. ← + model auto-pick, same-language rule
- [x] Build Director Prompt system: ← DEVIATION: emits H3PromptIDE structure (subject_definitions/detailed_description/`<d>[English]`/soundscape) because that is what node 365 consumes; scene-card schema dropped
  - [x] Break stories into structured scenes ($N$ scenes). ← as [Shot N] beats inside detailed_description
  - [x] Output scene fields: `SceneNumber`, `Narration`, `VisualPrompt`, `NegativePrompt`, `EstimatedDuration`. ← covered by H3 sections instead
  - [x] Format visual prompts specifically for MiniMax H3 (camera angle, lighting, line-art/cinematic details).
- [x] Create UI Storyboard Editor in XAML: ← DEVIATION: Projects tab (project list → scene cards with editable beats/H3, split-from-story, per-scene render, clip picker, assemble-to-film); History tab lists saved generations
  - [x] Dynamic scene cards showing prompt text and duration.
  - [x] Individual scene regeneration and manual prompt tweaking before dispatching to ComfyUI. ← per-scene render (same seed = retry), H3 editors, validator at queue time

---

## Phase 4: ComfyUI Orchestration & Minimax H3 Integration
- [x] Implement `ComfyUiService`:
  - [x] **Workflow Mutator:** Parse exported workflow JSON, inject seeds, prompts, aspect ratio, and step count. ← by class_type scan (template-agnostic) + media_state 9/3/3 upload
  - [x] **Mode Controller:** ← PARTIAL: template picker accepts any dropped-in API file; per-mode toggles (previews off / latent pick / overlap math) not implemented
    - [ ] Single-Pass configuration (toggle previews off, set resolution to 1.0–1.5 MP, steps 6–8).
    - [ ] Seed-Hunter configuration (queue 2–3 previews at 0.4 MP, pass chosen latent ID to Stage 2 upscale).
    - [ ] Video Continuation mode (inject previous clip, calculate overlap frames: $17n + 5$, default 22 frames).
  - [x] **Queue Dispatcher:** `POST http://127.0.0.1:8188/prompt` with unique `client_id`.
  - [x] **WebSocket Client:** Connect to `ws://127.0.0.1:8188/ws?clientId={id}`: friendly executing/progress/queue messages; history poll stays source of truth.
  - [x] **Artifact Downloader:** Fetch output MP4 via `GET http://127.0.0.1:8188/view?filename=...` to local cache.
- [x] Add queue concurrency throttling (render 1 video at a time to prevent VRAM allocation crashes). ← SemaphoreSlim(1)

---

## Phase 5: Video Concatenation & Post-Processing
- [x] Implement `VideoMergeService` using `ProcessStartInfo` or `CliWrap` targeting bundled FFmpeg:
  - [x] Generate concat manifest file (`concat_list.txt`) containing absolute paths of all scene clips.
  - [x] Run fast stream-copy merge:
    ```bash
    ffmpeg -y -f concat -safe 0 -i concat_list.txt -c copy final_video.mp4
    ```
- [x] Implement audio synchronization safeguard:
  - [x] Detect clips lacking audio tracks and generate silent audio padding to avoid de-syncing concatenated outputs. ← via ffprobe pre-pass
- [x] (Optional) Local Voiceover Integration: ← via edge-tts CLI if installed (ar-SA default) + ffmpeg mux; install prompt otherwise
  - [x] Integrate Edge-TTS or Piper to generate scene voiceovers from narration text.
  - [x] Multiplex narration audio over the final video stream using FFmpeg.
- [x] Add `CommunityToolkit.Maui.Views.MediaElement` to view the stitched output directly inside the app. ← DEVIATION: OS default player via Launcher; no new dependency

---

## Phase 6: Social Media Multi-Upload Engine ("One-Click Publish")
> YouTube is fully wired (OAuth device-loop + resumable chunked upload + SecureStorage tokens). TikTok/Facebook upload code is real HTTP behind pasted tokens (neither supports desktop loopback). Remaining user-side: Google Cloud OAuth client, TikTok approved app, Meta Page token.
- [x] **Unified Authentication Architecture:**
  - [x] Embed local HTTP listener (`http://127.0.0.1:port/callback`) for local desktop OAuth loops. ← via GoogleWebAuthorizationBroker loopback for YouTube
  - [x] Securely store OAuth access/refresh tokens using MAUI `SecureStorage`. ← AuthStore + SecureDataStore; client secrets too
  - [ ] Add token expiration checks and automatic refresh routines. ← Google lib auto-refreshes; TikTok/FB re-paste on expiry
- [x] **YouTube Publishing:** ← REAL via Google.Apis.YouTube.v3 1.77
  - [x] Integrate `Google.Apis.YouTube.v3` NuGet.
  - [x] Implement resumable chunked upload with title, description, tags, and privacy status. ← unlisted default, picker in Settings
- [x] **TikTok Direct Post:** ← REAL Content Posting API (FILE_UPLOAD chunks + publish_id)
  - [ ] Register TikTok for Developers app with `video.upload` scope. ← USER ACTION
  - [x] Implement Content Posting API initialization (`POST /v2/post/publish/video/init/`).
  - [x] Upload video chunk stream and verify publish status.
- [x] **Facebook / Instagram Reels:** ← REAL Graph v21 resumable (start/transfer/finish)
  - [ ] Configure Meta App with `pages_manage_posts` and `publish_video` permissions. ← USER ACTION
  - [x] Implement Meta Graph API resumable video upload sessions (`/{page-id}/videos`).
- [x] **Publishing Dispatcher:**
  - [x] Build UI upload modal with checkboxes for YouTube, TikTok, and Facebook.
  - [x] Implement `Task.WhenAll` to broadcast video simultaneously to all checked platforms.
  - [x] Real-time progress bar for each platform's upload stream. ← live % status line per platform

---

## Phase 7: UI Polish, Error Recovery & End-to-End Testing
- [x] Build Settings Page:
  - [x] Custom base URLs for Ollama and ComfyUI. ← live-applied via AppSettings
  - [x] MiniMax H3 default generation parameters (aspect ratio, default steps, LoRA weights). ← steps/template/voice/ffmpeg wired; LoRA weights stay template-side
  - [x] API Credentials and OAuth connection status indicators. ← "not configured" indicators; token entry UI pending
- [ ] Implement automatic error recovery:
  - [x] ComfyUI OOM detection from WebSocket logs. ← render-failure messages now surface instead of hanging; dedicated OOM parsing pending
  - [ ] Retry logic for disconnected WebSockets or failed platform upload chunks. ← WS is best-effort enrichment by design; upload retry arrives with first real publisher
- [ ] Execute full pipeline test:
  1. Input short prompt -> Ollama generates 3-scene script.
  2. ComfyUI renders clips via MiniMax H3.
  3. FFmpeg stitches clips cleanly.
  4. One-Click publish dispatches to YouTube, TikTok, and Facebook.
