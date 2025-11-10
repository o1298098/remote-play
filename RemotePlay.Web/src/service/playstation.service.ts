import { apiRequest, type ApiResponse } from './api-client'

// 设备信息类型
export interface ConsoleInfo {
  ip: string
  name: string
  uuid: string
  hostType?: string
  systemVerion?: string
  deviceDiscoverPotocolVersion?: string
  status?: string
}

// 绑定设备请求类型
export interface BindDeviceRequest {
  hostIp: string
  accountId?: string
  pin?: string
  deviceName?: string
}

export interface DeviceStreamingSettings {
  resolution?: string
  frameRate?: string
  bitrate?: string
  quality?: string
  streamType?: string
}

export interface DeviceResolutionOption {
  key: string
  label: string
  labelKey: string
  width: number
  height: number
  bitrate: number
}

export interface DeviceFrameRateOption {
  value: string
  label: string
  labelKey: string
  fps?: string
}

export interface DeviceBitrateOption {
  bitrate: string
  label: string
  quality: string
  labelKey: string
}

export interface DeviceStreamTypeOption {
  value: string
  label: string
  code?: string
  labelKey: string
}

export interface DeviceStreamingOptions {
  resolutions: DeviceResolutionOption[]
  frameRates: DeviceFrameRateOption[]
  bitrates: DeviceBitrateOption[]
  streamTypes: DeviceStreamTypeOption[]
}

export interface DeviceSettingsResponse {
  settings: DeviceStreamingSettings
  options: DeviceStreamingOptions
}

export interface UpdateDeviceSettingsRequest extends DeviceStreamingSettings {}

// 用户设备类型
export interface UserDevice {
  userDeviceId: string
  deviceId: string
  hostId: string
  hostName: string
  hostType?: string
  ipAddress?: string
  macAddress?: string
  systemVersion?: string
  isRegistered: boolean
  status?: string
  lastUsedAt?: string
  createdAt: string
  settings?: DeviceStreamingSettings
}

/**
 * PlayStation 设备服务
 */
export const playStationService = {
  /**
   * 发现本地网络中的PlayStation主机
   */
  discoverDevices: async (timeoutMs?: number): Promise<ApiResponse<ConsoleInfo[]>> => {
    const params = timeoutMs ? `?timeoutMs=${timeoutMs}` : ''
    return apiRequest<ConsoleInfo[]>(`/playstation/discover${params}`, {
      method: 'GET',
    })
  },

  /**
   * 发现特定IP的PlayStation主机
   */
  discoverDevice: async (hostIp: string, timeoutMs?: number): Promise<ApiResponse<ConsoleInfo>> => {
    const params = timeoutMs ? `?timeoutMs=${timeoutMs}` : ''
    return apiRequest<ConsoleInfo>(`/playstation/discover/${hostIp}${params}`, {
      method: 'GET',
    })
  },

  /**
   * 绑定PS主机到当前用户
   */
  bindDevice: async (data: BindDeviceRequest): Promise<ApiResponse<any>> => {
    return apiRequest('/playstation/bind', {
      method: 'POST',
      body: JSON.stringify(data),
    })
  },

  /**
   * 获取当前用户已绑定的设备列表
   */
  getMyDevices: async (): Promise<ApiResponse<UserDevice[]>> => {
    return apiRequest<UserDevice[]>('/playstation/my-devices', {
      method: 'GET',
    })
  },

  /**
   * 解绑设备
   */
  unbindDevice: async (userDeviceId: string): Promise<ApiResponse<any>> => {
    return apiRequest(`/playstation/unbind?userDeviceId=${userDeviceId}`, {
      method: 'POST',
    })
  },

  /**
   * 唤醒主机
   */
  wakeUpConsole: async (hostId: string): Promise<ApiResponse<boolean>> => {
    return apiRequest<boolean>(`/playstation/wakeup?hostId=${encodeURIComponent(hostId)}`, {
      method: 'POST',
    })
  },

  /**
   * 获取设备串流设置
   */
  getDeviceSettings: async (deviceId: string): Promise<ApiResponse<DeviceSettingsResponse>> => {
    return apiRequest<DeviceSettingsResponse>(`/playstation/device-settings/${deviceId}`, {
      method: 'GET',
    })
  },

  /**
   * 更新设备串流设置
   */
  updateDeviceSettings: async (
    deviceId: string,
    data: UpdateDeviceSettingsRequest
  ): Promise<ApiResponse<DeviceSettingsResponse>> => {
    return apiRequest<DeviceSettingsResponse>(`/playstation/device-settings/${deviceId}`, {
      method: 'POST',
      body: JSON.stringify(data),
    })
  },
}

