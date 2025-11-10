中文 | [EN](README.en.md)

# RemotePlay C# 服务架构

## 概述

本项目提供基于 C# 的 RemotePlay 服务端实现，涵盖设备发现、注册与统一接入能力，并通过模块化与配置化设计增强可维护性与扩展性。

---

## 核心服务

### 1) 设备发现服务 DeviceDiscoveryService

**功能**: 实现 DDP（设备发现协议）以发现本地网络中的 PlayStation 主机

**主要特性**:
- 支持多网卡并发发现
- 可配置超时
- 支持按 IP 定向发现
- 完整的错误处理与日志

**接口**: `IDeviceDiscoveryService`

**方法**:
- `DiscoverDevicesAsync()` - 发现所有设备
- `DiscoverDeviceAsync(hostIp)` - 按 IP 发现设备

### 2) 设备注册服务 RegisterService

**功能**: 处理客户端在 PlayStation 主机上的注册流程

**主要特性**:
- 安全的密钥生成与管理
- JSON 请求/响应处理
- 错误处理与重试
- 凭据有效期管理

**接口**: `IRegisterService`

**方法**:
- `RegisterDeviceAsync(hostIp, accountId, pin)` - 注册设备

### 3) 加密工具 CryptoUtils

**功能**: 提供加解密与密钥管理

**主要特性**:
- AES-256-CBC 加密
- SHA-256 哈希
- RSA 签名校验
- 随机密钥与 PIN 生成

**接口**: `ICryptoUtils`

### 4) 统一服务 RemotePlayService

**功能**: 对外提供统一编排入口

**主要特性**:
- 参数与凭据校验
- 统一错误处理
- 服务编排协调

**接口**: `IRemotePlayService`

---

## 数据模型

### ConsoleInfo
```csharp
public record ConsoleInfo(string Ip, string Name, string Uuid);
```

### DeviceCredentials
```csharp
public class DeviceCredentials
{
    public string AccountId { get; set; }
    public string Pin { get; set; }
    public string HostId { get; set; }
    public string HostName { get; set; }
    public string HostIp { get; set; }
    public byte[] RegistrationKey { get; set; }
    public byte[] ServerKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsValid { get; }
}
```

### RegisterResult
```csharp
public class RegisterResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; }
    public DeviceCredentials? Credentials { get; set; }
    public string HostId { get; set; }
    public string HostName { get; set; }
    public DateTime RegisteredAt { get; set; }
    public TimeSpan Duration { get; set; }
}
```

---

## 配置

### appsettings.json
```json
{
  "RemotePlay": {
    "Discovery": {
      "TimeoutMs": 2000,
      "DiscoveryPort": 9302,
      "ClientPort": 9303,
      "ProtocolVersion": "00030010",
      "MaxRetries": 3
    },
    "Registration": {
      "TimeoutMs": 30000,
      "Endpoint": "/sce/rp/rp/session",
      "MaxRetries": 3,
      "CredentialExpiryDays": 30
    },
    "Security": {
      "KeyLength": 32,
      "PinLength": 8,
      "EnableEncryption": true,
      "EncryptionAlgorithm": "AES-256-CBC"
    },
    "Logging": {
      "EnableDebugLogging": false,
      "LogNetworkTraffic": false,
      "LogLevel": "Information"
    }
  }
}
```

---

## API 端点

### 设备发现
- `GET /api/PlayStation/discover` - 发现所有设备
- `GET /api/PlayStation/discover/{hostIp}` - 发现特定设备

### 设备注册
- `POST /api/PlayStation/register` - 注册设备

### 凭据验证
- `POST /api/PlayStation/validate-credentials` - 验证设备凭据

---

## 使用示例

### 1) 发现设备
```csharp
var devices = await _remotePlayService.DiscoverDevicesAsync();
```

### 2) 注册设备
```csharp
var result = await _remotePlayService.RegisterDeviceAsync("192.168.1.100", "account123", "12345678");
if (result.Success)
{
    var credentials = result.Credentials; // 保存凭据
}
```

### 3) 验证凭据
```csharp
var isValid = await _remotePlayService.ValidateCredentialsAsync(credentials);
```

---

## 架构优势

### 1) 模块化
- 单一职责
- 接口与实现分离
- 便于测试与维护

### 2) 依赖注入
- 使用 ASP.NET Core 内置容器
- 便于单元测试
- 松耦合

### 3) 配置管理
- 集中化配置
- 环境化
- 运行时可调整

### 4) 错误处理
- 统一处理
- 结构化日志
- 优雅恢复

### 5) 安全性
- 加密通信
- 密钥管理
- 凭据有效期控制

---

## 扩展性

支持以下扩展：

1. 新的设备类型：通过实现新的发现协议
2. 新的认证方式：扩展注册服务
3. 新的加密算法：扩展加密工具
4. 新的存储后端：实现新的凭据存储
5. 新的网络协议：扩展通信协议