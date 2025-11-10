# 🔐 RemotePlay 消息加密策略完整参考

## 📋 快速参考表

| # | 消息类型 | 发送时机 | Channel | Encrypt Payload | GMAC | Key Pos | Advance By |
|---|---------|---------|---------|-----------------|------|---------|------------|
| **1** | INIT | 握手开始 | N/A | ❌ N/A | ❌ | ❌ | ❌ |
| **2** | COOKIE | 收到 INIT_ACK | N/A | ❌ N/A | ❌ | ❌ | ❌ |
| **3** | BIG (LaunchSpec+ECDH) | 收到 COOKIE_ACK | 1 | ❌ N/A | ❌ | ❌ | ❌ |
| **4** | DATA_ACK | 收到 DATA | N/A | ❌ No | ✅ | ✅ | ✅ 29 |
| **5** | CLIENTINFO | 收到 BANG | 1 | ❌ **No** | ✅ | ✅ | ✅ len |
| **6** | STREAMINFO_ACK | 收到 STREAMINFO | 9 | ❌ **No** | ✅ | ✅ | ✅ len |
| **7** | CONTROLLER_CONNECTION | STREAMINFO_ACK 后 | 1 | ❌ **No** | ✅ | ✅ | ✅ len |
| **8** | MIC_CONNECTION | 可选 | 1 | ❌ **No** | ✅ | ✅ | ✅ len |
| **9** | MICROPHONE_ENABLE | 可选 | 1 | ❌ **No** | ✅ | ✅ | ✅ len |
| **10** | IDRREQUEST | 请求关键帧 | 1 | ❌ **No** | ✅ | ✅ | ✅ len |
| **11** | **FeedbackState** | 定时发送（60Hz） | N/A | ✅ **Yes** | ✅ | ✅ | ✅ 28 |
| **12** | **FeedbackEvent** | 按键事件 | N/A | ✅ **Yes** | ✅ | ✅ | ✅ len |
| **13** | Congestion | 定时发送 | N/A | ❌ N/A | ✅ | ✅ | ✅ 15 |
| **14** | CorruptFrame | 检测损坏帧 | 2 | ❌ **No** | ✅ | ✅ | ✅ len |
| **15** | Heartbeat | 定时发送 | 1 | ❌ **No** | ✅ | ✅ | ✅ len |
| **16** | DISCONNECT | 断开连接 | 1 | ❌ **No** | ✅ | ✅ | ✅ len |

---

## 🎨 颜色编码

- 🔴 **Encrypt Payload = Yes**: 需要加密 payload（只有 FeedbackState/Event）
- 🟢 **Encrypt Payload = No**: 不加密 payload，但计算 GMAC（所有 Protobuf 消息）
- ⚪ **Encrypt Payload = N/A**: 无 cipher，不加密（握手阶段）

---

## 📊 按阶段分类

### 阶段 1: 握手（无 Cipher）⚪

```
Client                    PS5 Server
  |                           |
  |------- INIT ------------> |
  | <---- INIT_ACK ---------- |
  |------ COOKIE -----------> |
  | <---- COOKIE_ACK -------- |
  |                           |
```

| 消息 | Encrypt | GMAC | 说明 |
|------|---------|------|------|
| INIT | ❌ N/A | ❌ | 无 cipher，纯明文 |
| COOKIE | ❌ N/A | ❌ | 无 cipher，纯明文 |

---

### 阶段 2: ECDH 握手（建立 Cipher）🔑

```
Client                    PS5 Server
  |                           |
  |------- BIG ------------> |  (LaunchSpec + ECDH Public Key)
  | <------ BANG ----------- |  (ECDH Public Key)
  |                           |
  | ✅ 双方计算 shared_secret  |
  | ✅ 建立 StreamCipher       |
  |                           |
```

| 消息 | Encrypt | GMAC | 说明 |
|------|---------|------|------|
| BIG | ❌ N/A | ❌ | 无 cipher（ECDH 握手前），包含 ECDH public key |

---

### 阶段 3: 流信息交换（有 Cipher）🟢

```
Client                    PS5 Server
  |                           |
  | <-- STREAMINFO --------- |  (Protobuf: 视频/音频参数)
  |-- STREAMINFO_ACK ------> |  (Protobuf: ✅ 确认)
  |                           |
  | <----- CLIENTINFO? ------ |  (可选)
  |                           |
```

| 消息 | Encrypt Payload | GMAC | 说明 |
|------|-----------------|------|------|
| STREAMINFO_ACK | ❌ **No** | ✅ | 🔥 Protobuf，PS5 需要解析 |
| CLIENTINFO | ❌ **No** | ✅ | 🔥 Protobuf，PS5 需要解析 |

---

### 阶段 4: 控制器连接（有 Cipher）🟢

```
Client                    PS5 Server
  |                           |
  |-- CONTROLLER_CONNECTION -> | (Protobuf: 控制器已连接)
  |-- MIC_CONNECTION -------> | (可选，Protobuf)
  |-- MICROPHONE_ENABLE ----> | (可选，Protobuf)
  |                           |
```

| 消息 | Encrypt Payload | GMAC | 说明 |
|------|-----------------|------|------|
| CONTROLLER_CONNECTION | ❌ **No** | ✅ | 🔥 Protobuf，PS5 需要解析 |
| MIC_CONNECTION | ❌ **No** | ✅ | Protobuf |
| MICROPHONE_ENABLE | ❌ **No** | ✅ | Protobuf |

---

### 阶段 5: 正常流传输（有 Cipher）🟢🔴

```
Client                    PS5 Server
  |                           |
  | <---- VIDEO/AUDIO ------- |  (加密的 AV 数据)
  |------- DATA_ACK -------> |  (确认接收)
  |                           |
  |-- FeedbackState -------> |  (🔴 加密的控制器输入)
  |-- Congestion ----------> |  (网络状态)
  |-- Heartbeat -----------> |  (保持连接)
  |                           |
```

| 消息 | Encrypt Payload | GMAC | 说明 |
|------|-----------------|------|------|
| DATA_ACK | ❌ No | ✅ | 控制包，只需 GMAC |
| **FeedbackState** | ✅ **Yes** | ✅ | 🔴 包含敏感输入数据，必须加密！ |
| **FeedbackEvent** | ✅ **Yes** | ✅ | 🔴 包含按键事件，必须加密！ |
| Congestion | ❌ N/A | ✅ | 只有固定字段（received, lost） |
| Heartbeat | ❌ No | ✅ | Protobuf，保持连接 |

---

### 阶段 6: 错误处理（有 Cipher）🟢

```
Client                    PS5 Server
  |                           |
  |-- CorruptFrame --------> |  (Protobuf: 请求重传)
  |-- IDRREQUEST ----------> |  (Protobuf: 请求关键帧)
  | <---- IDR Frame --------- |  (关键帧)
  |                           |
```

| 消息 | Encrypt Payload | GMAC | 说明 |
|------|-----------------|------|------|
| CorruptFrame | ❌ **No** | ✅ | Protobuf，PS5 需要解析 |
| IDRREQUEST | ❌ **No** | ✅ | Protobuf，PS5 需要解析 |

---

### 阶段 7: 断开连接（有 Cipher）🟢

```
Client                    PS5 Server
  |                           |
  |---- DISCONNECT --------> |  (Protobuf: 断开原因)
  |                           |
  | ❌ 关闭连接                |
```

| 消息 | Encrypt Payload | GMAC | 说明 |
|------|-----------------|------|------|
| DISCONNECT | ❌ **No** | ✅ | Protobuf，PS5 需要解析 |

---

## 🔍 详细说明

### 什么时候 Encrypt Payload？

#### ✅ 需要加密 Payload 的消息（只有 2 种）

| 消息类型 | 原因 | 数据内容 |
|---------|------|---------|
| **FeedbackState** | 包含敏感的实时控制器输入 | 摇杆位置、按键状态、触摸板、传感器 |
| **FeedbackEvent** | 包含敏感的按键事件 | 按键 ID、按下/释放状态 |

**为什么需要加密**？
- 🔒 **隐私保护**：防止窃听玩家的操作
- 🛡️ **防篡改**：防止中间人修改玩家输入
- ⚡ **实时性要求**：高频发送（60Hz），需要高效加密（AES-CFB）

#### ❌ 不需要加密 Payload 的消息（所有 Protobuf）

| 消息类型 | 原因 | 保护方式 |
|---------|------|---------|
| **所有 Protobuf 消息** | PS5 需要直接解析 Protobuf | GMAC 完整性保护 |

**包括哪些**？
- STREAMINFO_ACK
- CONTROLLER_CONNECTION
- MIC_CONNECTION
- MICROPHONE_ENABLE
- CLIENTINFO
- IDRREQUEST
- Heartbeat
- CorruptFrame
- DISCONNECT
- 等等...

**为什么不加密**？
- 📝 **协议握手需要**：PS5 需要解析这些消息来管理会话状态
- 🔓 **内容不敏感**：这些消息只是控制信号（"准备好了"、"连接了" 等）
- ✅ **仍然安全**：通过 GMAC 保护完整性，防止篡改和重放攻击
- 🚀 **性能考虑**：减少不必要的加密/解密开销

---

## 🔐 安全机制详解

### 即使不加密 Payload，也有多层保护

#### 1. GMAC（Galois Message Authentication Code）

```
GMAC = AES-GCM-MAC(key, nonce, entire_packet)
```

**作用**：
- ✅ 检测篡改：任何字节被修改，GMAC 验证失败
- ✅ 防重放：结合 key_pos，防止重放攻击
- ✅ 认证：确保消息来自拥有密钥的对方

**计算范围**：整个包（包括 header + payload），但计算时 GMAC 字段本身为 0

#### 2. Key Position (key_pos)

```
key_pos = 当前密钥流的位置（每发送 N 字节推进 N）
```

**作用**：
- ✅ 同步密钥流：确保发送方和接收方使用相同的密钥位置
- ✅ 防重放：每个包的 key_pos 是单调递增的
- ✅ 顺序保证：检测乱序的包

**推进规则**：
- Protobuf 消息：`advance_by = len(payload)`
- FeedbackState：`advance_by = 28` (固定)
- Congestion：`advance_by = 15` (固定)
- DATA_ACK：`advance_by = 29` (固定)

#### 3. ECDH（Elliptic Curve Diffie-Hellman）

```
Client: private_key_A, public_key_A = generate_keypair()
Server: private_key_B, public_key_B = generate_keypair()

shared_secret = ECDH(private_key_A, public_key_B)
               = ECDH(private_key_B, public_key_A)
```

**作用**：
- ✅ 安全密钥交换：在不安全的网络上建立共享密钥
- ✅ 前向保密：每次会话使用不同的临时密钥
- ✅ 防窃听：即使中间人截获公钥，也无法计算共享密钥

---

## 📝 C# 代码模式

### ✅ 正确的 Protobuf 消息发送

```csharp
// 1. 构建 Protobuf 消息
var ack = ProtoCodec.BuildStreamInfoAck();

// 2. 推进 TSN（如果有 cipher）
if (_cipher != null) _tsn++;

// 3. 发送（不加密 payload，但计算 GMAC）
await SendAsync(
    Packet.CreateData(_tsn, 9, 1, ack), 
    encryptPayload: false,  // ✅ 关键！Protobuf 不加密
    advanceByOverride: ack.Length  // ✅ 推进 key_pos
);
```

### ✅ 正确的 FeedbackState 发送

```csharp
// 1. 构建 FeedbackState
var stateBytes = state.Pack(isPs5);

// 2. 创建 FeedbackState 包
var pkt = FeedbackPacket.CreateState(
    _feedbackSequence++, 
    stateBytes
);

// 3. 发送（加密 payload + GMAC）
await SendAsync(
    pkt, 
    encryptPayload: true,  // ✅ 关键！FeedbackState 必须加密
    advanceByOverride: 28  // ✅ FeedbackState 固定 28 字节
);
```

### ✅ 正确的 Congestion 发送

```csharp
// 1. 创建 Congestion 包（只有固定字段）
var pkt = FeedbackPacket.CreateCongestion(
    _feedbackSequence++, 
    received, 
    lost
);

// 2. 发送（不加密，计算 GMAC）
await SendAsync(
    pkt, 
    encryptPayload: false,  // ✅ 没有 payload，不加密
    advanceByOverride: 15  // ✅ Congestion 固定 15 字节
);
```

---

## 🚨 常见错误

### ❌ 错误 1: 加密 Protobuf 消息

```csharp
// ❌ 错误！
await SendAsync(
    Packet.CreateData(_tsn, 9, 1, ack), 
    encryptPayload: true,  // ❌ PS5 无法解析加密的 Protobuf
    advanceByOverride: ack.Length
);
```

**结果**：
- PS5 收到加密数据
- PS5 尝试解析 Protobuf，失败
- PS5 忽略消息或断开连接
- 结果：握手失败或没有视频

### ❌ 错误 2: 不加密 FeedbackState

```csharp
// ❌ 错误！
await SendAsync(
    pkt, 
    encryptPayload: false,  // ❌ 控制器输入暴露
    advanceByOverride: 28
);
```

**结果**：
- 控制器输入以明文发送
- 中间人可以窃听玩家操作
- PS5 可能拒绝明文的 FeedbackState
- 安全风险！

### ❌ 错误 3: 错误的 advanceByOverride

```csharp
// ❌ 错误！
await SendAsync(
    Packet.CreateData(_tsn, 9, 1, ack), 
    encryptPayload: false,
    advanceByOverride: null  // ❌ key_pos 不同步
);
```

**结果**：
- 客户端和 PS5 的 key_pos 不同步
- GMAC 验证失败
- PS5 拒绝后续消息
- 连接断开

---

## 🎯 检查清单

使用这个清单来验证你的代码：

### ✅ 发送 Protobuf 消息时

- [ ] `encryptPayload: false` ✅
- [ ] `advanceByOverride: payload.Length` ✅
- [ ] 推进 TSN（如果有 cipher）✅
- [ ] 日志显示 `encrypted=False` ✅

### ✅ 发送 FeedbackState 时

- [ ] `encryptPayload: true` ✅
- [ ] `advanceByOverride: 28` ✅
- [ ] 推进 sequence ✅
- [ ] 日志显示 `encrypted=True` ✅

### ✅ 发送 Congestion 时

- [ ] `encryptPayload: false` ✅
- [ ] `advanceByOverride: 15` ✅
- [ ] 推进 sequence ✅

### ✅ GMAC 计算

- [ ] 有 cipher 时总是计算 GMAC ✅
- [ ] 计算时 GMAC 字段为 0 ✅
- [ ] 计算后写入 GMAC ✅

### ✅ key_pos 管理

- [ ] 发送前记录当前 key_pos ✅
- [ ] 写入 header ✅
- [ ] 发送后推进 key_pos ✅
- [ ] 推进量 = advanceByOverride ✅

---


### C# RemotePlay 关键文件

| 文件 | 说明 |
|------|------|
| `RPStream.cs` | 主要的流管理和发送逻辑 |
| `Packets.cs` | 包结构定义和创建方法 |
| `ProtoCodec.cs` | Protobuf 消息构建 |
| `StreamCipher.cs` | 加密和 GMAC 计算 |

---

## 🎉 总结

### 记住这两条黄金规则

1. **Protobuf 消息 → `encryptPayload: false`** 
   - PS5 需要解析，只用 GMAC 保护

2. **FeedbackState/Event → `encryptPayload: true`**
   - 包含敏感输入数据，必须加密

### 其他所有情况

- Congestion：没有 payload，`encryptPayload: false`
- DATA_ACK：控制包，`encryptPayload: false`

遵循这些规则，你的 RemotePlay 客户端就能正确地与 PS5 通信！🚀🎮

