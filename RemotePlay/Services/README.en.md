# RemotePlay C# Service Architecture

## Overview

This project provides a C#-based RemotePlay backend implementing device discovery, registration, and a unified access layer, with modular and configurable architecture for maintainability and extensibility.

---

## Core Services

### 1) DeviceDiscoveryService

**Purpose**: Implement DDP (Device Discovery Protocol) to discover PlayStation consoles on the local network

**Features**:
- Concurrent discovery across multiple network interfaces
- Configurable timeouts
- Targeted discovery by IP
- Robust error handling and logging

**Interface**: `IDeviceDiscoveryService`

**Methods**:
- `DiscoverDevicesAsync()` - discover all devices
- `DiscoverDeviceAsync(hostIp)` - discover device by IP

### 2) RegisterService

**Purpose**: Handle client registration on the PlayStation console

**Features**:
- Secure key generation and management
- JSON request/response handling
- Error handling with retries
- Credential expiry management

**Interface**: `IRegisterService`

**Methods**:
- `RegisterDeviceAsync(hostIp, accountId, pin)` - register device

### 3) CryptoUtils

**Purpose**: Provide cryptography, decryption and key management

**Features**:
- AES-256-CBC encryption
- SHA-256 hashing
- RSA signature verification
- Secure key and PIN generation

**Interface**: `ICryptoUtils`

### 4) RemotePlayService

**Purpose**: Provide a unified orchestration entrypoint

**Features**:
- Parameter and credential validation
- Unified error handling
- Service orchestration

**Interface**: `IRemotePlayService`

---

## Data Models

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

## Configuration

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

## API Endpoints

### Discovery
- `GET /api/PlayStation/discover` - discover all devices
- `GET /api/PlayStation/discover/{hostIp}` - discover by IP

### Registration
- `POST /api/PlayStation/register` - register device

### Credential Validation
- `POST /api/PlayStation/validate-credentials` - validate credentials

---

## Usage Examples

### 1) Discover devices
```csharp
var devices = await _remotePlayService.DiscoverDevicesAsync();
```

### 2) Register device
```csharp
var result = await _remotePlayService.RegisterDeviceAsync("192.168.1.100", "account123", "12345678");
if (result.Success)
{
    var credentials = result.Credentials; // persist credentials
}
```

### 3) Validate credentials
```csharp
var isValid = await _remotePlayService.ValidateCredentialsAsync(credentials);
```

---

## Advantages

### 1) Modularity
- Single responsibility
- Interface-implementation separation
- Testable and maintainable

### 2) Dependency Injection
- Built-in DI container (ASP.NET Core)
- Test-friendly
- Loose coupling

### 3) Configuration Management
- Centralized configuration
- Environment-specific settings
- Runtime-adjustable

### 4) Error Handling
- Unified handling
- Structured logging
- Graceful recovery

### 5) Security
- Encrypted communication
- Key management
- Credential expiry control

---

## Extensibility

Supports extensions such as:

1. New device types: via new discovery protocols
2. New auth methods: extend registration service
3. New crypto: extend cryptography suite
4. New storage backends: new credential stores
5. New network protocols: extend communication protocols

