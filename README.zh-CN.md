# Remote Play（PlayStation Remote Play 平台）
中文 | [EN](README.md)
## 项目简介
PSRP 基于 ASP.NET Core，结合 SignalR/WebRTC 串流管线、React 前端与 PostgreSQL 持久化，将 PlayStation Remote Play 会话桥接到浏览器，便于自建低延迟云游戏体验。

## 功能亮点
- **WebRTC 串流**：内建 WebRTC 低延迟管线，为浏览器端提供毫秒级推流体验。
- **控制器实时回传**：利用 SignalR WebSocket 管道，将按键与摇杆输入在毫秒级同步。
- **完善的设备管理**：提供发现、注册、会话控制等完整 API。
- **安全认证体系**：内置 JWT 身份验证，支持扩展 OAuth/Profile 服务。
- **可观测性支撑**：延迟统计、日志级别配置与详尽文档，便于排障。

## 技术栈
- **后端**：ASP.NET Core 10、SignalR、EF Core、Npgsql、gRPC/Protobuf、FFmpeg、BouncyCastle、Concentus、SIPSorcery。
- **前端**：React 18、TypeScript、Vite、Tailwind CSS、shadcn/ui、Radix UI、i18next、SignalR 客户端。
- **数据库**：推荐使用 PostgreSQL 16，并通过 `RemotePlay.DBTool` 执行 EF Core 迁移。
- **容器化**：Docker、Docker Compose、Nginx（静态资源与反向代理）。

## 系统架构
1. **RemotePlay**：ASP.NET Core 后端，实现 PlayStation Remote Play 协议通信，提供 REST API、SignalR Hub、WebRTC 信令与媒体管道。
2. **RemotePlay.Web**：基于 React 的 Web 前端，支持多语言界面、流媒体播放、设备面板与会话控制。
3. **RemotePlay.DBTool**：命令行数据库工具，封装 EF Core 迁移与初始化脚本，适用于自动化流程。
4. **外部依赖**：PostgreSQL 负责配置与状态存储，可选 STUN/TURN 服务用于 WebRTC 穿透，FFmpeg 负责 HLS 切片生成与管理。

整体采用分层架构：前端通过 REST/SSE/SignalR 与后端交互，后端内部涵盖会话管理、流媒体服务、控制器服务、认证服务与监控模块。媒体数据可通过 WebRTC 或 HLS 分发到浏览器端。

## 仓库结构
```
.
├── RemotePlay/            # ASP.NET Core 服务，包含 Streaming、Auth、Profile 等模块
│   └── Docs/              # 协议与架构文档（WebRTC、加密、控制器接口等）
├── RemotePlay.Web/        # React + Vite Web 客户端
│   └── docker/            # 前端容器与 Nginx 配置
├── RemotePlay.DBTool/     # EF Core 迁移与数据库初始化工具
```

## 开发环境准备
1. **安装依赖**
   - [.NET SDK 10 预览版](https://dotnet.microsoft.com/)（或任何兼容 `net10.0` 的版本）
   - [Node.js 18+ 与 npm](https://nodejs.org/)
   - [PostgreSQL 16+](https://www.postgresql.org/)（本地或云端实例）
   - 可选：`ffmpeg`、`pnpm`、Docker Desktop

2. **克隆项目**
   ```bash
   git clone https://github.com/<your-account>/PSRP.git
   cd PSRP
   ```

3. **配置后端**
   - 将 `RemotePlay/appsettings.json` 复制为 `appsettings.Development.json`，并根据环境修改 `Database`、`JWT`、`RemotePlay`、`WebRTC` 等设置。
   - 可通过环境变量 `DB_HOST`、`DB_NAME`、`DB_USER`、`DB_PASSWORD`、`CORS__AllowAllOrigins`、`JWT__Secret` 等覆盖配置。

4. **初始化数据库**
   ```bash
   dotnet run --project RemotePlay.DBTool -- \
     --connection "Host=localhost;Port=5432;Database=remoteplay;Username=remoteplay;Password=remoteplay"
   ```
   工具会执行迁移与基础数据导入；如有需要，替换为自定义连接字符串。

5. **启动后端**
   ```bash
   dotnet run --project RemotePlay
   ```
   默认监听 `http://localhost:5000`，可通过 `Properties/launchSettings.json` 或环境变量调整。

6. **启动前端**
   ```bash
   cd RemotePlay.Web
   cp .env.example .env
   npm install
   npm run dev
   ```
   将 `.env` 中的 `VITE_API_BASE_URL` 指向后端地址（如 `http://localhost:5000/api`），开发服务器默认运行在 `http://localhost:5173`。

## Docker Compose 部署示例
以下示例参考了我们的实际部署方案，直接拉取预构建镜像，并连接到现有的 PostgreSQL 实例（请根据自身环境调整 IP、端口与凭证）。

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
      #请把eth0替换为实际与Playstaion同一网络的网口
      parent: eth0
    ipam:
      config:
        - subnet: 192.168.50.0/24
          gateway: 192.168.50.1
```

部署前请确认：
- `docker compose up -d`
- `lan` 网络的 macvlan 配置与实际网卡、子网保持一致。
- （可选）取消注释 TURN 凭证或 JWT 配置，以满足生产环境需求。

> 提示：如需启用 TURN 服务或自定义 FFmpeg 行为，可在配置文件 `WEBRTC` 与 `RemotePlay` 节点中补充字段，并同步到容器环境变量。

## 配置与环境变量
- `Database:*` / `DB_*`：PostgreSQL 连接参数。
- `JWT:*`：访问令牌签名配置，部署前务必修改 `Secret`。
- `RemotePlay:Discovery` / `RemotePlay:Registration`：主机发现与配对参数。
- `WebRTC:*`：信令、端口范围、ICE 服务器等设置。
- `CORS:*`：允许访问的前端域名，生产环境应明确列出。

更多细节请查阅 `RemotePlay/Docs/` 下的文档（如 `WEBRTC_GUIDE.md`、`CONTROLLER_API.md`、`ENCRYPTION_REFERENCE.md`）。

## 测试与调试
- 后端单元测试：`dotnet test`（测试集持续完善中）。
- WebRTC 排查：参考 `RemotePlay/Docs/WEBRTC_GUIDE.md` 的操作手册。
- 日志排查：启用 `RemotePlay:Logging:EnableDebugLogging` 与 `LogNetworkTraffic` 可获取更详细的网络日志。
- 前端校验：在 `RemotePlay.Web` 目录运行 `npm run lint`、`npm run build`。

## 贡献指南
欢迎通过 Issue、Pull Request 或 Discussions 参与共建。提交前建议：
- 关联对应的问题描述或架构背景。
- 为新增或修改的功能补充文档与配置示例。
- 说明手动或自动化测试结果，确保行为可复现。

## 许可协议
本项目以 MIT License 授权发布，详情请参阅仓库根目录下的 `LICENSE` 文件。

---

> PlayStation 主机握手与认证流程在实现时参考了社区项目 [pyremoteplay](https://github.com/ktnrg45/pyremoteplay) 提供的协议分析。

> 文档最后更新：2025-11-10。如有疑问或改进建议，欢迎提交 Issue。

