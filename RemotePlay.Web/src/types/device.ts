import type { DeviceStreamingSettings } from '@/service/playstation.service'

export interface Console {
  id: string
  userDeviceId: string
  name: string
  type: 'PS4' | 'PS5'
  status: 'available' | 'standby' | 'offline'
  statusText: string
  readyText: string
  ipAddress?: string
  macAddress?: string
  hostId?: string
  isRegistered: boolean
  settings?: DeviceStreamingSettings
}

export type DeviceStatus = 'available' | 'standby' | 'offline'

