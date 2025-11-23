import { useState, useEffect } from 'react'
import {
  isMobileDevice,
  isTabletDevice,
  isIOSDevice,
  isAndroidDevice,
  getDeviceType,
  type DeviceType,
  supportsFullscreen,
  isFullscreen,
  supportsTouch,
  getViewportSize,
  isLandscape,
  isPortrait,
} from '@/utils/device-detection'

export interface DeviceInfo {
  isMobile: boolean
  isTablet: boolean
  isIOS: boolean
  isAndroid: boolean
  deviceType: DeviceType
  supportsFullscreen: boolean
  isFullscreen: boolean
  supportsTouch: boolean
  viewport: { width: number; height: number }
  isLandscape: boolean
  isPortrait: boolean
}

/**
 * 设备信息 Hook
 */
export function useDevice(): DeviceInfo {
  const [deviceInfo, setDeviceInfo] = useState<DeviceInfo>(() => ({
    isMobile: isMobileDevice(),
    isTablet: isTabletDevice(),
    isIOS: isIOSDevice(),
    isAndroid: isAndroidDevice(),
    deviceType: getDeviceType(),
    supportsFullscreen: supportsFullscreen(),
    isFullscreen: isFullscreen(),
    supportsTouch: supportsTouch(),
    viewport: getViewportSize(),
    isLandscape: isLandscape(),
    isPortrait: isPortrait(),
  }))

  useEffect(() => {
    const updateDeviceInfo = () => {
      setDeviceInfo({
        isMobile: isMobileDevice(),
        isTablet: isTabletDevice(),
        isIOS: isIOSDevice(),
        isAndroid: isAndroidDevice(),
        deviceType: getDeviceType(),
        supportsFullscreen: supportsFullscreen(),
        isFullscreen: isFullscreen(),
        supportsTouch: supportsTouch(),
        viewport: getViewportSize(),
        isLandscape: isLandscape(),
        isPortrait: isPortrait(),
      })
    }

    // 监听窗口大小变化
    window.addEventListener('resize', updateDeviceInfo)
    
    // 监听全屏状态变化
    const fullscreenChangeEvents = [
      'fullscreenchange',
      'webkitfullscreenchange',
      'mozfullscreenchange',
      'MSFullscreenChange',
    ]
    
    fullscreenChangeEvents.forEach((event) => {
      document.addEventListener(event, updateDeviceInfo)
    })

    // 监听方向变化（移动端）
    window.addEventListener('orientationchange', updateDeviceInfo)

    return () => {
      window.removeEventListener('resize', updateDeviceInfo)
      fullscreenChangeEvents.forEach((event) => {
        document.removeEventListener(event, updateDeviceInfo)
      })
      window.removeEventListener('orientationchange', updateDeviceInfo)
    }
  }, [])

  return deviceInfo
}

