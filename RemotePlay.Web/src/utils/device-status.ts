import type { DeviceStatus } from '@/types/device'

/**
 * 映射设备状态字符串到标准状态类型
 * @param status 后端返回的状态字符串
 * @returns 标准化的设备状态
 */
export const mapDeviceStatus = (status?: string): DeviceStatus => {
  if (!status) return 'offline'
  const statusUpper = status.toUpperCase()
  if (statusUpper.includes('STANDBY')) return 'standby'
  if (statusUpper === 'OK' || statusUpper.includes('READY') || statusUpper.includes('AVAILABLE')) return 'available'
  return 'offline'
}

