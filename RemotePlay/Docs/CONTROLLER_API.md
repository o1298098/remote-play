# 控制器 API 使用指南

## 概述

为了降低控制器输入的延迟，我们提供了两种API方式：

1. **HTTP REST API** - 适用于不频繁的操作和调试
2. **SignalR WebSocket API** - **推荐**，适用于实时控制，延迟更低

## 延迟对比

- **HTTP API**: 每次请求需要 ~10-50ms（包括HTTP握手、序列化等开销）
- **SignalR API**: 建立连接后，每次操作延迟 <5ms（持久连接，无需重复握手）

## SignalR API 使用（推荐）

### 连接

使用 SignalR 客户端连接到 Hub：

```javascript
// JavaScript/TypeScript 示例
import * as signalR from "@microsoft/signalr";

const connection = new signalR.HubConnectionBuilder()
    .withUrl("/hubs/controller")
    .build();

// 连接事件
connection.on("ControllerConnected", (success) => {
    console.log("控制器已连接:", success);
});

connection.on("Error", (message) => {
    console.error("错误:", message);
});

// 启动连接
await connection.start();
```

### 控制器操作

#### 1. 连接控制器到会话

```javascript
await connection.invoke("ConnectController", sessionId);
```

#### 2. 启动控制器

```javascript
await connection.invoke("StartController", sessionId);
```

#### 3. 按键操作

```javascript
// 轻按（按下后自动释放）
await connection.invoke("Button", sessionId, "X", "tap", 100);

// 按下
await connection.invoke("Button", sessionId, "X", "press");

// 释放
await connection.invoke("Button", sessionId, "X", "release");
```

可用按键：`X`, `O`, `TRIANGLE`, `SQUARE`, `L1`, `L2`, `R1`, `R2`, `L3`, `R3`, `UP`, `DOWN`, `LEFT`, `RIGHT`, `OPTIONS`, `SHARE`, `TOUCHPAD`, `PS`

#### 4. 摇杆操作

```javascript
// 设置左摇杆坐标（x, y 范围: -1.0 到 1.0）
await connection.invoke("Stick", sessionId, "left", null, null, 0.5, 0.3);

// 设置单个轴
await connection.invoke("Stick", sessionId, "left", "x", 0.5);

// 批量更新两个摇杆（推荐用于高频更新）
await connection.invoke("BatchStickUpdate", sessionId, 
    0.5,  // leftX
    0.3,  // leftY
    0.8,  // rightX
    0.2   // rightY
);
```

#### 5. 获取状态

```javascript
// 获取摇杆状态
connection.on("StickState", (state) => {
    console.log("左摇杆:", state.left);
    console.log("右摇杆:", state.right);
});
await connection.invoke("GetStickState", sessionId);

// 获取控制器状态
connection.on("ControllerStatus", (status) => {
    console.log("运行中:", status.isRunning);
    console.log("就绪:", status.isReady);
});
await connection.invoke("GetControllerStatus", sessionId);
```

#### 6. 断开连接

```javascript
await connection.invoke("DisconnectController", sessionId);
await connection.stop();
```

### C# 客户端示例

```csharp
using Microsoft.AspNetCore.SignalR.Client;

var connection = new HubConnectionBuilder()
    .WithUrl("http://localhost:5000/hubs/controller")
    .Build();

// 注册事件
connection.On<bool>("ControllerConnected", (success) => 
{
    Console.WriteLine($"控制器已连接: {success}");
});

connection.On<string>("Error", (message) => 
{
    Console.WriteLine($"错误: {message}");
});

// 启动连接
await connection.StartAsync();

// 连接控制器
await connection.InvokeAsync("ConnectController", sessionId);

// 启动控制器
await connection.InvokeAsync("StartController", sessionId);

// 按键
await connection.InvokeAsync("Button", sessionId, "X", "tap", 100);

// 摇杆
await connection.InvokeAsync("Stick", sessionId, "left", null, null, 0.5f, 0.3f);

// 批量更新摇杆（推荐）
await connection.InvokeAsync("BatchStickUpdate", sessionId, 
    0.5f,  // leftX
    0.3f,  // leftY
    0.8f,  // rightX
    0.2f   // rightY
);
```

### Python 客户端示例

```python
import asyncio
import signalr

# 使用 signalr 库或 websockets 库
# 这里使用 signalr 库的示例
async def main():
    connection = signalr.HubConnection("http://localhost:5000", "hubs/controller")
    
    # 注册事件处理
    connection.on("ControllerConnected", lambda success: print(f"控制器已连接: {success}"))
    connection.on("Error", lambda msg: print(f"错误: {msg}"))
    
    # 启动连接
    await connection.start()
    
    session_id = "your-session-id"
    
    # 连接控制器
    await connection.invoke("ConnectController", session_id)
    
    # 启动控制器
    await connection.invoke("StartController", session_id)
    
    # 按键
    await connection.invoke("Button", session_id, "X", "tap", 100)
    
    # 摇杆
    await connection.invoke("Stick", session_id, "left", None, None, 0.5, 0.3)
    
    # 批量更新摇杆
    await connection.invoke("BatchStickUpdate", session_id, 0.5, 0.3, 0.8, 0.2)
    
    await connection.stop()

asyncio.run(main())
```

## HTTP REST API（备用）

如果无法使用 SignalR，仍可使用 HTTP API：

### 端点

- `POST /api/playstation/controller/connect` - 连接控制器
- `POST /api/playstation/controller/button` - 按键操作
- `POST /api/playstation/controller/stick` - 摇杆操作
- `GET /api/playstation/controller/state` - 获取状态

详细文档请参考 Swagger UI（开发环境下可用）。

## 性能优化建议

1. **使用 SignalR 而非 HTTP API** - 延迟降低 80-90%
2. **使用 BatchStickUpdate** - 批量更新摇杆，减少网络往返
3. **避免频繁的连接/断开** - 保持连接打开，重用连接
4. **合理设置更新频率** - 摇杆更新频率建议 30-60Hz（每 16-33ms 一次）

## 延迟测试

```javascript
// 测试延迟
const startTime = performance.now();
await connection.invoke("Button", sessionId, "X", "tap");
const endTime = performance.now();
console.log(`延迟: ${endTime - startTime}ms`);
```

## 注意事项

1. SignalR 连接在断开后会自动重连（如果配置了）
2. 每个会话只能有一个活跃的控制器连接
3. 摇杆值范围是 -1.0 到 1.0
4. 按键操作是异步的，不会等待 PlayStation 的响应

## 故障排除

1. **连接失败**: 检查 Hub 路径是否正确（`/hubs/controller`）
2. **延迟仍然很高**: 确保使用 SignalR 而非 HTTP API
3. **操作无响应**: 检查控制器是否已连接并启动（`GetControllerStatus`）

