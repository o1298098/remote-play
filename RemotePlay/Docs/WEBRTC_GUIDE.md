# WebRTC 实时流媒体推送指南

## 概述

本项目现已支持通过 WebRTC 将 PlayStation Remote Play 的音视频流推送到 Web 浏览器。这使得用户可以直接在浏览器中观看游戏画面，无需安装额外的播放器。

## 架构说明

### 组件介绍

1. **WebRTCReceiver** (`Services/Streaming/Receiver/WebRTCReceiver.cs`)
   - 实现 `IAVReceiver` 接口
   - 接收来自 PlayStation 的 AV 包
   - 将 AV 包转换为 RTP 包并通过 WebRTC 发送

2. **WebRTCSignalingService** (`Services/WebRTCSignalingService.cs`)
   - 管理 WebRTC 会话
   - 处理 SDP Offer/Answer 交换
   - 管理 ICE Candidate

3. **WebRTCController** (`Controllers/WebRTCController.cs`)
   - 提供 RESTful API 接口
   - 处理客户端的连接请求
   - 连接 WebRTC 会话到 Remote Play 会话

4. **Web 客户端** (`wwwroot/webrtc-player.html`)
   - 提供用户界面
   - 处理 WebRTC 连接建立
   - 显示视频流

## 使用流程

### 1. 启动 Remote Play 会话

首先，你需要启动一个 Remote Play 会话并开始流媒体传输。

```bash
# 1. 注册设备（如果尚未注册）
POST /api/PlayStation/register

# 2. 启动会话
POST /api/PlayStation/start-session

# 3. 开始流媒体（记录返回的 sessionId）
POST /api/PlayStation/start-stream/{sessionId}
```

### 2. 访问 WebRTC 播放器

在浏览器中打开：
```
http://localhost:5000/webrtc-player.html
```

### 3. 连接到 Remote Play 会话

1. 在播放器页面输入 Remote Play Session ID（上一步获得的 GUID）
2. 点击"连接"按钮
3. 等待连接建立，你将看到游戏画面

## API 接口说明

### 创建 WebRTC Offer

```http
POST /api/webrtc/offer
```

**响应：**
```json
{
  "success": true,
  "data": {
    "sessionId": "a1b2c3d4...",
    "sdp": "v=0\r\no=- ...",
    "type": "offer"
  }
}
```

### 发送 Answer

```http
POST /api/webrtc/answer
Content-Type: application/json

{
  "sessionId": "a1b2c3d4...",
  "sdp": "v=0\r\no=- ...",
  "type": "answer"
}
```

### 发送 ICE Candidate

```http
POST /api/webrtc/ice
Content-Type: application/json

{
  "sessionId": "a1b2c3d4...",
  "candidate": "candidate:...",
  "sdpMid": "0",
  "sdpMLineIndex": 0
}
```

### 连接到 Remote Play 会话

```http
POST /api/webrtc/connect/{webrtcSessionId}/{remotePlaySessionId}
```

### 获取会话状态

```http
GET /api/webrtc/session/{sessionId}
```

### 获取所有会话

```http
GET /api/webrtc/sessions
```

### 删除会话

```http
DELETE /api/webrtc/session/{sessionId}
```

## 配置说明

### STUN/TURN 服务器

默认配置使用 Google 的公共 STUN 服务器：
```csharp
var config = new RTCConfiguration
{
    iceServers = new List<RTCIceServer>
    {
        new RTCIceServer 
        { 
            urls = "stun:stun.l.google.com:19302" 
        }
    }
};
```

如果需要在复杂网络环境下使用，建议配置 TURN 服务器：
```csharp
new RTCIceServer 
{ 
    urls = "turn:your-turn-server.com:3478",
    username = "your-username",
    credential = "your-password"
}
```

## 技术细节

### 视频编码

- **支持格式：** H.264 (AVC) / H.265 (HEVC)
- **编码方式：** PlayStation 已编码，直接传输
- **浏览器兼容性：**
  - Chrome/Edge: 完全支持 H.264
  - Firefox: 完全支持 H.264
  - Safari: 完全支持 H.264
  - ⚠️ HEVC 支持有限，建议使用 H.264

### 音频编码

- **源格式：** AAC
- **目标格式：** OPUS (计划中)
- **当前状态：** 音频编码转换待实现

### NAL Unit 处理

视频数据采用 Annex-B 格式（带起始码 00 00 00 01），符合 WebRTC 标准。系统会自动：
- 检测关键帧（IDR）
- 从关键帧开始发送数据
- 处理 SPS/PPS 参数集

### RTP 时间戳

- **视频：** 90kHz 时钟
- **音频：** 48kHz 时钟

## 故障排除

### 视频无法显示

1. **检查 Remote Play 会话状态**
   ```http
   GET /api/PlayStation/sessions
   ```

2. **检查流是否运行**
   ```http
   GET /api/PlayStation/stream-status/{sessionId}
   ```

3. **检查 WebRTC 连接状态**
   - 打开浏览器开发者工具
   - 查看控制台日志
   - 检查 ICE 连接状态

### ICE 连接失败

1. **防火墙问题：** 确保端口开放
2. **NAT 穿透：** 配置 TURN 服务器
3. **网络限制：** 检查企业网络策略

### 音频无法播放

当前版本音频编码转换功能尚未完全实现。如需音频支持，需要：
1. 实现 AAC 到 OPUS 的转码
2. 或使用 AAC 直接传输（浏览器支持有限）

## 性能优化建议

### 服务器端

1. **批量处理 AV 包**
   ```csharp
   // AVHandler 已实现批量处理（50个包/批次）
   int batchSize = 50;
   ```

2. **减少日志输出**
   - 生产环境设置日志级别为 Warning 或 Error

3. **资源清理**
   - 定期清理过期会话
   - 监控内存使用

### 客户端

1. **视频元素配置**
   ```html
   <video autoplay playsinline muted controls></video>
   ```

2. **缓冲策略**
   - 使用低延迟模式
   - 减少缓冲区大小

3. **错误恢复**
   - 检测连接断开
   - 自动重连机制

## 扩展功能

### 多用户支持

当前实现支持多个 WebRTC 客户端同时观看同一个 Remote Play 会话：

```csharp
// 每个客户端创建独立的 WebRTCReceiver
var receiver1 = new WebRTCReceiver(sessionId1, ...);
var receiver2 = new WebRTCReceiver(sessionId2, ...);

// 连接到同一个流
stream.AddReceiver(receiver1);
stream.AddReceiver(receiver2);
```

### 录制功能

可以结合 `FfmpegMuxReceiver` 同时录制和实时播放：

```csharp
// 创建录制接收器
var recorder = new FfmpegMuxReceiver(output: "recording.mkv");
stream.AddReceiver(recorder);

// 创建 WebRTC 接收器（实时播放）
var webrtcReceiver = new WebRTCReceiver(...);
stream.AddReceiver(webrtcReceiver);
```

## 安全考虑

1. **HTTPS 要求：** 生产环境必须使用 HTTPS
2. **身份验证：** 建议添加用户认证
3. **会话管理：** 限制会话数量和时长
4. **速率限制：** 防止滥用 API

## 浏览器兼容性

| 浏览器 | 版本 | H.264 | HEVC | WebRTC |
|-------|------|-------|------|--------|
| Chrome | 88+ | ✅ | ⚠️ | ✅ |
| Edge | 88+ | ✅ | ⚠️ | ✅ |
| Firefox | 78+ | ✅ | ❌ | ✅ |
| Safari | 14+ | ✅ | ✅ | ✅ |

## 参考资料

- [WebRTC API](https://developer.mozilla.org/en-US/docs/Web/API/WebRTC_API)
- [SIPSorcery Library](https://github.com/sipsorcery-org/sipsorcery)
- [H.264 Annex B Format](https://www.itu.int/rec/T-REC-H.264)
- [RTP Payload Format for H.264](https://tools.ietf.org/html/rfc6184)

## 许可证

本功能基于 SIPSorcery 库实现，遵循相应的开源许可证。

