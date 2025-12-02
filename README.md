# Remote Play
EN | [中文](README.zh-CN.md)
## Overview
PSRP is a self-hosted solution that allows you to stream and play PlayStation games directly in your web browser. It bridges your PlayStation console to any device with a modern browser, providing low-latency streaming and full controller support.

## Features

| Feature | Description |
|---------|-------------|
| **Gamepad Support** | Support for physical gamepads/joysticks, virtual touch controller for mobile devices, and customizable keyboard-to-controller mapping |
| **Console Registration** | Easy registration of your PlayStation console using PIN code, with automatic device discovery |
| **Mobile Adaptation** | Optimized mobile experience with touch-based virtual controller and responsive UI design |

## UI Preview

### Devices Overview
![Devices overview](./RemotePlay.Web/docs/images/devices-overview.png)

### Streaming Interface
![Streaming demo](./RemotePlay.Web/docs/images/streaming-demo.png)

### Video Demo
https://github.com/user-attachments/assets/0a8f075f-7577-4fbc-bdd2-e6741669eb2d


## Quick Start

The easiest way to get started is using Docker Compose (see below). For manual setup, you'll need:
- Docker and Docker Compose
- PostgreSQL database (or use the included PostgreSQL container)
- Basic knowledge of Docker and networking

## Docker Compose Deployment

The recommended way to deploy PSRP is using Docker Compose. The following configuration sets up all required services:

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
    image: ghcr.io/o1298098/remoteplay-web
    container_name: web
    restart: unless-stopped
    environment:
      - API_PROXY_URL=http://backend:8080
    ports:
      - "10110:80"
    networks:
      - default

  backend:
    image: ghcr.io/o1298098/remoteplay-server
    container_name: server
    restart: unless-stopped
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - Database__HOST=postgres
      - Database__PORT=5432
      - Database__NAME=remoteplay
      - Database__USER=remoteplay
      - Database__PASSWORD=remoteplay
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
    image: ghcr.io/o1298098/remoteplay-dbtool
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
volumes:
   postgres-data:
```

### Deployment Steps

1. **Adjust the configuration** in the `docker-compose.yml` file:
   - Modify the `lan` network settings to match your network interface (replace `eth0` with your actual network interface)
   - Update subnet and gateway if needed
   - (Optional) Set JWT secret for production use
   - **Note**: WebRTC and TURN server settings can be configured in the web interface after deployment (Settings → WebRTC Settings / TURN Server Settings)

2. **Start the services**:
   ```bash
   docker compose up -d
   ```

3. **Access the web interface**:
   - Frontend: `http://your-server-ip:10110`
   - Backend API: `http://your-server-ip:10111`

4. **Configure WebRTC and TURN servers** (optional):
   - Open the web interface and navigate to Settings
   - Configure WebRTC settings (Public IP, ICE port range) if needed
   - Configure TURN servers for better connectivity in complex network environments

5. **Register your PlayStation console**:
   - Open the web interface
   - Click "Add Device" to discover your PlayStation console
   - Enter the PIN code shown on your PlayStation screen
   - Your console will be registered and ready to use

### Important Notes

- Ensure the `lan` network interface matches your physical network adapter
- The backend needs to be on the same network as your PlayStation console
- WebRTC and TURN server settings are now configured through the web interface (Settings page)
- For remote access, configure port forwarding and TURN servers via the web interface
- Change the default JWT secret before production deployment

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


