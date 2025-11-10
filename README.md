# Remote Play
EN | [中文](README.zh-CN.md)
## Overview
PSRP is implemented as an ASP.NET Core service that bridges PlayStation Remote Play to the browser, pairing a SignalR/WebRTC media pipeline with a React front-end and PostgreSQL-backed persistence so you can self-host a low-latency streaming experience.

## Feature Highlights
- **WebRTC streaming**: Native WebRTC pipeline optimized for ultra-low latency browser playback.
- **Real-time controller relay**: SignalR-based WebSocket channels keep button presses and stick movement synchronized within milliseconds.
- **Complete device lifecycle management**: APIs for discovery, registration, session orchestration, and health monitoring.
- **Hardened authentication**: Built-in JWT authentication plus extensible OAuth/Profile services.
- **Operational visibility**: Latency statistics, tunable logging, and detailed documentation to streamline troubleshooting.

## Tech Stack
- **Backend**: ASP.NET Core 10, SignalR, EF Core, Npgsql, gRPC/Protobuf, FFmpeg, BouncyCastle, Concentus, SIPSorcery.
- **Frontend**: React 18, TypeScript, Vite, Tailwind CSS, shadcn/ui, Radix UI, i18next, SignalR client.
- **Database**: PostgreSQL 16 (recommended) with EF Core migrations powered by `RemotePlay.DBTool`.
- **Containerization**: Docker, Docker Compose, and Nginx for static asset delivery and reverse proxying.

## Architecture Overview
1. **RemotePlay**: The ASP.NET Core backend that speaks the PlayStation Remote Play protocol, exposes REST APIs, SignalR hubs, WebRTC signaling, and media pipelines.
2. **RemotePlay.Web**: The React front-end providing multi-language UI, streaming playback, device dashboards, and session controls.
3. **RemotePlay.DBTool**: A command-line helper for EF Core migrations and database bootstrapping, designed for automation workflows.
4. **External services**: PostgreSQL for configuration and state persistence, optional STUN/TURN servers for WebRTC traversal, and FFmpeg for HLS segment generation and management.

The system follows a layered architecture: the web client communicates with the backend via REST/SSE/SignalR, while backend services coordinate session management, streaming, controller I/O, authentication, and telemetry. Media flows to browsers either through WebRTC or HLS depending on the client capabilities.

## Repository Layout
```
.
├── RemotePlay/            # ASP.NET Core service with Streaming, Auth, Profile modules
│   └── Docs/              # Architecture and protocol docs (WebRTC, encryption, controller APIs)
├── RemotePlay.Web/        # React + Vite web client
│   └── docker/            # Frontend container and Nginx configuration
├── RemotePlay.DBTool/     # EF Core migration & initialization utility
```

## Getting Started
1. **Install prerequisites**
   - [.NET SDK 10 preview](https://dotnet.microsoft.com/) (or any SDK compatible with `net10.0`)
   - [Node.js 18+ with npm](https://nodejs.org/)
   - [PostgreSQL 16+](https://www.postgresql.org/) (local instance or managed service)
   - Optional: `ffmpeg`, `pnpm`, Docker Desktop

2. **Clone the repository**
   ```bash
   git clone https://github.com/o1298098/remote-play.git
   cd remote-play
   ```

3. **Configure the backend**
   - Copy `RemotePlay/appsettings.json` to `appsettings.Development.json` and adjust `Database`, `JWT`, `RemotePlay`, `WebRTC`, and related sections.
   - Environment variables such as `DB_HOST`, `DB_NAME`, `DB_USER`, `DB_PASSWORD`, `CORS__AllowAllOrigins`, and `JWT__Secret` can override appsettings.

4. **Initialize the database**
   ```bash
   dotnet run --project RemotePlay.DBTool -- \
     --connection "Host=localhost;Port=5432;Database=remoteplay;Username=remoteplay;Password=remoteplay"
   ```
   The tool applies migrations and seeds baseline data. Provide a custom connection string when needed.

5. **Run the backend**
   ```bash
   dotnet run --project RemotePlay
   ```
   The default endpoint is `http://localhost:5000`, configurable through `Properties/launchSettings.json` or environment variables.

6. **Run the frontend**
   ```bash
   cd RemotePlay.Web
   cp .env.example .env
   npm install
   npm run dev
   ```
   Set `VITE_API_BASE_URL` in `.env` to the backend address (for example `http://localhost:5000/api`). The dev server listens on `http://localhost:5173`.

## Docker Compose Example
The following Compose file mirrors the production setup used in our self-hosted environment. It pulls prebuilt images, wires up TURN/STUN settings, and connects to an existing PostgreSQL instance (adjust the connection values to match your deployment).

```yaml
version: "3.9"

services:
  postgres:
    image: postgres:16
    container_name: postgres
    restart: unless-stopped
    environment:
      - POSTGRES_DB=remoteplay
      - POSTGRES_USER=remoteplay
      - POSTGRES_PASSWORD=remoteplay
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U remoteplay -d remoteplay"]
      interval: 5s
      timeout: 5s
      retries: 5
    volumes:
      - postgres-data:/var/lib/postgresql/data

  frontend:
    image: registry.o1298098.xyz/o1298098/remote-play/web
    container_name: web
    restart: unless-stopped
    environment:
      - API_PROXY_URL=http://backend:8080
    ports:
      - "10110:80"
    networks:
      - default

  backend:
    image: registry.o1298098.xyz/o1298098/remote-play/server
    container_name: server
    restart: unless-stopped
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - Database__HOST=postgres
      - Database__PORT=5432
      - Database__NAME=remoteplay
      - Database__USER=remoteplay
      - Database__PASSWORD=remoteplay
      - WebRTC__IcePortMin=40200
      - WebRTC__IcePortMax=40400
      - WebRTC__PublicIp=192.168.100.186
      - WebRTC__TurnServers__0__Url=turn:stun.cloudflare.com:3478?transport=udp
      # - WebRTC__TurnServers__0__Username=<turn-username>
      # - WebRTC__TurnServers__0__Credential=<turn-password>
      # - JWT__Secret=change-me-to-a-strong-secret
      # - TZ=Asia/Shanghai
    ports:
      - "10111:8080"
      - "40200-40400:40200-40400/udp"
    depends_on:
      postgres:
        condition: service_healthy
      dbtool:
        condition: service_completed_successfully
    networks:
      lan:
        ipv4_address: 192.168.50.33
      default:

  dbtool:
    image: registry.o1298098.xyz/o1298098/remote-play/dbtool
    container_name: dbtool
    restart: "no"
    environment:
      - Database__HOST=postgres
      - Database__PORT=5432
      - Database__NAME=remoteplay
      - Database__USER=remoteplay
      - Database__PASSWORD=remoteplay
    healthcheck:
      test: ["CMD-SHELL", "exit 0"]
      interval: 1s
      retries: 1
    networks:
      - default
    depends_on:
      postgres:
        condition: service_healthy

networks:
  default:
    driver: bridge
  lan:
    driver: macvlan
    driver_opts:
      parent: eth0
    ipam:
      config:
        - subnet: 192.168.50.0/24
          gateway: 192.168.50.1
```

Before deploying, run:
- `docker compose up -d`
- Confirm that the `lan` macvlan network matches your physical interface and subnet.
- (Optional) uncomment the TURN credential or JWT lines to harden production deployments.

> Tip: To enable TURN servers or customize FFmpeg behavior, extend the `WEBRTC` and `RemotePlay` configuration sections and mirror the changes via container environment variables.

## Configuration Essentials
- `Database:*` / `DB_*`: PostgreSQL connection settings.
- `JWT:*`: Token signing configuration—change the `Secret` before going live.
- `RemotePlay:Discovery` / `RemotePlay:Registration`: Parameters for console discovery and pairing.
- `WebRTC:*`: Signaling, port ranges, and ICE server configuration.
- `CORS:*`: Allowed client origins; always lock down domains in production.

Consult the documents under `RemotePlay/Docs/` (for example `WEBRTC_GUIDE.md`, `CONTROLLER_API.md`, `ENCRYPTION_REFERENCE.md`) for deeper protocol and troubleshooting details.

## Testing & Troubleshooting
- Run backend unit tests with `dotnet test` (suite in progress).
- Follow the playbooks in `RemotePlay/Docs/WEBRTC_GUIDE.md` to validate WebRTC sessions.
- Enable `RemotePlay:Logging:EnableDebugLogging` and `LogNetworkTraffic` for verbose diagnostics.
- Use `npm run lint` and `npm run build` in `RemotePlay.Web` to validate front-end code and production bundles.

## Contributing
Contributions via issues, pull requests, or discussions are welcome. Before submitting, please:
- Reference the related issue or architectural background.
- Update documentation and configuration samples for new or changed features.
- Include manual or automated test notes demonstrating expected behavior.

## License
Distributed under the MIT License. See `LICENSE` for details.

---

> The PlayStation console handshake and authentication flow draws on protocol insights from the community project [pyremoteplay](https://github.com/ktnrg45/pyremoteplay).

> Last updated: 2025-11-10. Feel free to open an issue if you have questions or suggestions.


