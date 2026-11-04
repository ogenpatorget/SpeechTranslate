# ChurchTranslate

ChurchTranslate is a live speech translation service for churches. **Phases 1–3 are implemented.** The admin app has church settings, a live studio, mixer/microphone capture, and a shareable listener link. The API supports a free synthetic demo signal and OpenAI's `gpt-realtime-translate` model. Google sign-in and the account allowlist remain available for deployment.

## Architecture

The `.NET 10` solution has independent `Domain`, `Application`, `Contracts`, `Infrastructure`, `Api`, `Admin` and `Listener` projects. The two Blazor WebAssembly frontends can be published as static sites. The API is an independently deployable container. Domain and application code have no provider, WebSocket or persistence dependencies.

## Run locally

Install the .NET 10 SDK, then run:

```sh
dotnet restore ChurchTranslate.sln
dotnet build ChurchTranslate.sln
dotnet test ChurchTranslate.sln
dotnet run --project src/ChurchTranslate.Api --urls http://127.0.0.1:5300
dotnet run --project src/ChurchTranslate.Admin --urls http://127.0.0.1:5301
dotnet run --project src/ChurchTranslate.Listener --urls http://127.0.0.1:5302
```

The admin and listener apps read their public API URL from their respective `wwwroot/appsettings.json` files. Edit those URLs for other environments. Do not put secrets in these files. The API defaults to `data/` under its content root; the directory contains church/user records and cookie protection keys and is ignored by Git.

In a direct local `Development` run, the admin app requires Google sign-in. The Visual Studio launch profiles and `scripts/start-local.ps1` bind all three services to `127.0.0.1` so they stay on your computer. Open `http://127.0.0.1:5301`, sign in with an invited Google account, then open the live studio. The local default provider is **Demo tone**, so the listener hears a synthetic signal rather than translated speech. Copy the listener link from the studio and open it at `http://127.0.0.1:5302`. Startup rejects `DevOpenAccess` outside Development.

In **Visual Studio**, right-click the solution → **Properties** → **Configure Startup Projects** → **Multiple startup projects**. Set `ChurchTranslate.Api`, `ChurchTranslate.Admin`, and `ChurchTranslate.Listener` to **Start**, with the API first. Select each project's `http` launch profile, then press **F5**. Open the admin page at `http://127.0.0.1:5301` and sign in with Google. If you prefer one command outside Visual Studio, run `powershell -ExecutionPolicy Bypass -File scripts/start-local.ps1` from the repository root. Stop those background services with `powershell -ExecutionPolicy Bypass -File scripts/stop-local.ps1`.

When a broadcast starts, the studio shows a QR code for its listener link. Keep the studio tab open while listeners join; leaving it disconnects the audio source and ends the broadcast. The local QR contains `127.0.0.1` and therefore works only on the same computer. For phone listening, configure a listener and API address that the phone can reach over trusted HTTPS; changing the QR image alone does not expose the local services to the phone.

During testing, each broadcast ends automatically five minutes after it becomes live. This is a wall-clock limit, including silence. `ChurchTranslate:MaxBroadcastDuration` controls it on the API (default `00:05:00`).

The listener defaults to **Smooth** playback, which holds a short reserve of translated audio to reduce gaps when chunks arrive unevenly. Listeners can switch to Balanced or Fast to reduce delay, and adjust playback volume. A gentle compressor evens out loud peaks. These controls change playback on each listener device; they do not change the translation model's voice or phrasing.

### Switch to OpenAI translation

1. In [OpenAI Platform API keys](https://platform.openai.com/api-keys), create a project API key. Confirm that the API organization has billing enabled and access to `gpt-realtime-translate`. API billing is separate from a ChatGPT subscription.
2. Store the key in the **API backend only**. For local development, the simplest option is a .NET user secret:

```sh
dotnet user-secrets init --project src/ChurchTranslate.Api
dotnet user-secrets set "OPENAI_API_KEY" "YOUR_KEY" --project src/ChurchTranslate.Api
dotnet user-secrets set "ChurchTranslate:TranslationProvider" "OpenAI" --project src/ChurchTranslate.Api
```

3. Restart the API and start a new broadcast. The studio shows `OpenAI · gpt-realtime-translate` when that provider is active. The browser never receives the API key.

To check API access without a microphone, run `dotnet run --project tools/TranslationSmoke/TranslationSmoke.csproj` from the repository root. It reads the API user secret locally and sends two seconds of silence. A successful check verifies key, credit, model and WebSocket access; it cannot validate speech quality.

The model uses one 24 kHz PCM16 WebSocket session **per target language**. It accepts 200 ms frames, including silence, and emits translated PCM16 audio to all listeners of each language. At the [currently listed price](https://developers.openai.com/api/docs/models/gpt-realtime-translate) of **$0.034 per audio minute**, 90 minutes in three languages is approximately **$9.18** before taxes or other charges. The app stores an estimate from input duration, not an official invoice. The original 50–150 SEK target may need reevaluation. Test a short service before using it live: actual Swedish→English/Arabic/French quality and latency have not been verified without an API key.

Google sign-in needs a Google Cloud **Web application** OAuth client with an exact authorized redirect URI matching the API's `/signin-google` callback. Follow the [Google OpenID Connect setup](https://developers.google.com/identity/openid-connect/openid-connect) and [ASP.NET Core external sign-in guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/social-without-identity?view=aspnetcore-10.0). Configure the client ID and secret only on the backend, for example with .NET user secrets during development:

```sh
dotnet user-secrets init --project src/ChurchTranslate.Api
dotnet user-secrets set "Authentication:Google:ClientId" "YOUR_CLIENT_ID" --project src/ChurchTranslate.Api
dotnet user-secrets set "Authentication:Google:ClientSecret" "YOUR_CLIENT_SECRET" --project src/ChurchTranslate.Api
```

Set `ChurchTranslate__BootstrapPlatformAdminEmail` to the email of the first platform administrator **before first sign-in**. This creates a pending invitation, not an active login. Google must assert `email_verified=true` before the invitation is bound to the stable `sub` identifier. After that, platform admins can create churches and invite further accounts from the UI. When Google credentials are absent, `/auth/login` returns 503 and health endpoints still work.

## API and security

- `/auth/login` starts the server-side OpenID Connect code flow. `/signin-google` is the callback.
- `/api/auth/me`, `/api/auth/csrf`, `/api/auth/logout` expose session state and the antiforgery token.
- `/api/admin/churches` and `/api/admin/users` manage churches and approved administrators. The OpenAPI-style DTOs are in `ChurchTranslate.Contracts`.
- Cookies are HttpOnly and Secure in production. State-changing API requests require a server-issued `X-CSRF-TOKEN` header. CORS permits only the configured admin and listener origins. The login endpoint is rate limited.
- `/api/admin/broadcasts` starts and stops streams. `/ws/ingest/{id}` accepts one authenticated 24 kHz PCM16 source. `/api/public/broadcasts/{id}` and the short-lived token endpoint let anonymous listeners connect to `/ws/listen/{id}/{language}`. Listener WebSockets have bounded queues so one slow phone does not block a language.
- Protected requests reload the user record, so disabling an account takes effect on its next request. Church resource access is checked server-side. Only platform admins can invite users or change church limits, status and slug.
- No raw or translated audio is stored. No Google or AI secrets are sent to the frontends.
- By default, church settings, approved Google accounts and usage records are stored in `churchtranslate.json`; sign-in protection keys are stored in `keys/`. `ChurchTranslate__EphemeralMode=true` instead keeps temporary church settings and usage in memory, loads approved admin emails from server configuration, and keeps sign-in keys in memory. No app state is written to disk in that mode. A restart clears churches, usage, active broadcasts and sessions; administrators must sign in again.
- Anyone holding a live listener link can request a short-lived listening token and hear that broadcast. Starting a broadcast and sending paid translation audio require an approved administrator account. The API limits each test broadcast to five minutes and defaults to at most 100 simultaneous listener connections per broadcast (`ChurchTranslate:MaxListenersPerBroadcast`). These limits do not impose a total monthly spending cap; set a church minute limit before testing the paid provider.

The frontend and API should use HTTPS origins under the **same registrable domain** in production (for example `admin.example.org` and `api.example.org`) so SameSite session cookies work. Configure `ChurchTranslate__AdminOrigin` to the exact frontend origin. Persistent mode needs a data volume including its `keys` directory; losing the keys invalidates existing sessions. Both storage modes are deliberately limited to **one API process**. Moving to multiple API instances requires shared storage. Restrict access to a persistent volume because cookie protection keys are stored there.

### Disposable MVP mode

For a free host with temporary filesystem storage, set `ChurchTranslate__EphemeralMode=true` and `ChurchTranslate__ApprovedAdminEmails` to a comma-separated list of approved Google email addresses. At least one address is required outside local preview. Each address becomes a platform administrator after Google verifies the email and the person signs in. The account page is read-only in this mode; change the allowlist on the API host and restart to add or remove administrators. Church settings can be entered in the admin UI after each startup, but disappear at the next restart. The OpenAI key and Google OAuth credentials remain server-side environment secrets.

Usage records and church minute limits reset at restart in disposable mode, so they cannot provide a durable spending cap. The five-minute stream limit still applies to each broadcast. The Render Blueprint uses the OpenAI provider; set a hard monthly OpenAI project spending limit before testing it. A free host may sleep and interrupt streams. If you need stable church settings or durable cost accounting later, move to a persistent database or paid disk.

## Docker

Run `docker compose -f deploy/docker-compose.yml up --build`. The API is on port 8080 and its data is on the `churchtranslate-data` volume. Set `TRANSLATION_PROVIDER=OpenAI` and `OPENAI_API_KEY` for paid translation. Set `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, `BOOTSTRAP_ADMIN_EMAIL`, `ADMIN_ORIGIN` and `LISTENER_ORIGIN` for deployment sign-in. The Compose file defaults to Development for local HTTP use; set `ASPNETCORE_ENVIRONMENT=Production` when deploying behind HTTPS. `src/ChurchTranslate.Api/appsettings.Example.json` lists the configuration keys.

Static frontends can be published separately with `dotnet publish`. Their `wwwroot` output can be hosted on Cloudflare Pages. The backend needs a host that supports long-lived WebSocket connections for Phase 3. Scale-to-zero hosts can add cold starts. Production reverse proxy or container-host HTTPS and forwarded-header settings must be configured for the chosen host before live sign-in.

### GitHub Pages frontends

The repository includes `.github/workflows/pages.yml`. It publishes the two Blazor apps under `/SpeechTranslate/admin/` and `/SpeechTranslate/listen/` on the repository's GitHub Pages site. The workflow skips deployment until the repository **Actions variable** `PUBLIC_API_URL` contains the public HTTPS **origin** of a running API (for example `https://api.example.org`). In repository **Settings → Pages**, select **GitHub Actions** as the source. After setting the variable, run the workflow manually or push a commit to `main`.

For the free test API, `render.yaml` defines a Render Free Docker web service. Create a Render account, connect this GitHub repository, and choose **New → Blueprint**. Render prompts for `ChurchTranslate__ApprovedAdminEmails` (your verified Google email), `Authentication__Google__ClientId`, `Authentication__Google__ClientSecret`, and `OPENAI_API_KEY`; enter these in Render, not in Git. The Blueprint enables disposable mode and uses the OpenAI provider for real translation. Render supplies an HTTPS `onrender.com` URL; add its exact `https://YOUR-SERVICE.onrender.com/signin-google` callback to the Google OAuth web client. Put the service origin in the GitHub Actions variable `PUBLIC_API_URL`, then run the Pages workflow. The API's `/health/ready` endpoint is Render's health check. Render terminates HTTPS before forwarding traffic to the container; the API sets the external scheme for OAuth redirects when `RENDER_EXTERNAL_URL` is supplied by Render.

The free Render instance sleeps after inactivity and disposable mode forgets churches and usage on every restart. Create the church again after signing in. This is suitable for testing, not for a live service that must retain settings or spending history. On an existing Blueprint service, add `OPENAI_API_KEY` directly under Render **Environment**: adding a new `sync: false` key to the Blueprint does not prompt again. Keep the key out of Git.

GitHub Pages cannot run the API, WebSockets, Google OAuth callback, or OpenAI connection. Deploy `ChurchTranslate.Api` separately on an HTTPS host that supports long-lived WebSockets. For the current `ogenpatorget/SpeechTranslate` repository, configure the API with:

```text
ChurchTranslate__AdminOrigin=https://ogenpatorget.github.io
ChurchTranslate__ListenerOrigin=https://ogenpatorget.github.io
ChurchTranslate__PublicAdminUrl=https://ogenpatorget.github.io/SpeechTranslate/admin/
ChurchTranslate__PublicListenerUrl=https://ogenpatorget.github.io/SpeechTranslate/listen/
ChurchTranslate__CrossSiteCookies=true
ChurchTranslate__EphemeralMode=true
ChurchTranslate__ApprovedAdminEmails=your-google-email@example.org
```

Also set the backend's Google client ID and secret, OpenAI key/provider and approved admin emails. Choose either disposable mode or a persistent `ChurchTranslate__DataPath`. Add `https://YOUR_API_HOST/signin-google` as an authorized Google redirect URI. Keep secrets on the API host, never in GitHub Pages variables or the frontend files. GitHub Pages and a separately hosted API usually have different site domains; `CrossSiteCookies=true` enables the cookies needed by that setup, but browsers that block third-party cookies may still prevent admin sign-in. For reliable sign-in, put the Pages site and API under subdomains of the same domain, then leave `CrossSiteCookies=false`. Update the Pages base paths if a custom domain changes the site URL structure.

## Validation and next phase

The test suite covers invitation verification, church scope, persistence, CSRF checks, revocation, broadcast limits, shared language output and usage metering. No paid AI service is used in tests. A live Google round trip and a live OpenAI translation still require your own credentials. Latency and audio quality remain unverified targets until a real venue test.

