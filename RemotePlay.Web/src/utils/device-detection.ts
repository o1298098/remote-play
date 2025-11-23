/**
 * 设备检测和移动端工具函数
 */

/**
 * 检测是否为移动设备
 * 包括真实的移动设备和 Chrome DevTools 的设备模拟器
 */
export function isMobileDevice(): boolean {
  if (typeof window === 'undefined') return false
  
  const viewportWidth = window.innerWidth
  const viewportHeight = window.innerHeight
  
  // 优先检查视口大小（Chrome DevTools 模拟器会改变视口）
  // 如果宽度小于等于 768px，且高度/宽度比例接近移动设备，认为是移动设备
  if (viewportWidth <= 768) {
    // 检查是否是移动设备的宽高比（竖屏或横屏）
    const isPortraitMobile = viewportHeight > viewportWidth && viewportHeight / viewportWidth > 1.3
    const isLandscapeMobile = viewportWidth > viewportHeight && viewportWidth / viewportHeight > 1.3
    const isSquareMobile = viewportWidth <= 768 && viewportHeight <= 1024
    
    if (isPortraitMobile || isLandscapeMobile || isSquareMobile) {
      return true
    }
  }
  
  // 检查用户代理（真实的移动设备）
  const userAgent = navigator.userAgent || navigator.vendor || (window as any).opera
  const mobileRegex = /android|webos|iphone|ipad|ipod|blackberry|iemobile|opera mini/i
  if (mobileRegex.test(userAgent)) {
    return true
  }
  
  // 检查触摸支持 + 小屏幕的组合（更可靠的移动设备检测）
  // 注意：单独的触摸支持不足以判断，因为桌面也可能有触摸屏
  const hasTouchSupport = 'ontouchstart' in window || navigator.maxTouchPoints > 0
  if (hasTouchSupport && viewportWidth <= 768) {
    return true
  }
  
  return false
}

/**
 * 检测是否为平板设备
 * 包括真实的平板设备和 Chrome DevTools 的平板模拟器
 */
export function isTabletDevice(): boolean {
  if (typeof window === 'undefined') return false
  
  const viewportWidth = window.innerWidth
  
  // 检查视口大小（平板通常是 768px - 1024px 宽度）
  const isTabletSize = viewportWidth >= 768 && viewportWidth <= 1024
  const hasTouchSupport = 'ontouchstart' in window || navigator.maxTouchPoints > 0
  
  // 如果是平板尺寸且有触摸支持，认为是平板
  if (isTabletSize && hasTouchSupport) {
    return true
  }
  
  // 检查用户代理（真实的平板设备）
  const userAgent = navigator.userAgent || navigator.vendor || (window as any).opera
  const tabletRegex = /ipad|android(?!.*mobile)|tablet/i
  if (tabletRegex.test(userAgent)) {
    return true
  }
  
  return false
}

/**
 * 检测是否为 iOS 设备
 */
export function isIOSDevice(): boolean {
  if (typeof window === 'undefined') return false
  
  const userAgent = navigator.userAgent || navigator.vendor || (window as any).opera
  return /iPad|iPhone|iPod/.test(userAgent) && !(window as any).MSStream
}

/**
 * 检测是否为 Android 设备
 */
export function isAndroidDevice(): boolean {
  if (typeof window === 'undefined') return false
  
  const userAgent = navigator.userAgent || navigator.vendor || (window as any).opera
  return /android/i.test(userAgent)
}

/**
 * 获取设备类型
 */
export type DeviceType = 'mobile' | 'tablet' | 'desktop'

export function getDeviceType(): DeviceType {
  if (isTabletDevice()) return 'tablet'
  if (isMobileDevice()) return 'mobile'
  return 'desktop'
}

/**
 * 检测是否支持全屏 API
 */
export function supportsFullscreen(): boolean {
  if (typeof document === 'undefined') return false
  
  return !!(
    document.fullscreenEnabled ||
    (document as any).webkitFullscreenEnabled ||
    (document as any).mozFullScreenEnabled ||
    (document as any).msFullscreenEnabled
  )
}

/**
 * 进入全屏
 */
export async function requestFullscreen(element: HTMLElement): Promise<void> {
  if (element.requestFullscreen) {
    await element.requestFullscreen()
  } else if ((element as any).webkitRequestFullscreen) {
    await (element as any).webkitRequestFullscreen()
  } else if ((element as any).mozRequestFullScreen) {
    await (element as any).mozRequestFullScreen()
  } else if ((element as any).msRequestFullscreen) {
    await (element as any).msRequestFullscreen()
  }
}

/**
 * 退出全屏
 */
export async function exitFullscreen(): Promise<void> {
  if (document.exitFullscreen) {
    await document.exitFullscreen()
  } else if ((document as any).webkitExitFullscreen) {
    await (document as any).webkitExitFullscreen()
  } else if ((document as any).mozCancelFullScreen) {
    await (document as any).mozCancelFullScreen()
  } else if ((document as any).msExitFullscreen) {
    await (document as any).msExitFullscreen()
  }
}

/**
 * 检测是否处于全屏状态
 */
export function isFullscreen(): boolean {
  if (typeof document === 'undefined') return false
  
  return !!(
    document.fullscreenElement ||
    (document as any).webkitFullscreenElement ||
    (document as any).mozFullScreenElement ||
    (document as any).msFullscreenElement
  )
}

/**
 * 检测是否支持触摸事件
 */
export function supportsTouch(): boolean {
  if (typeof window === 'undefined') return false
  return 'ontouchstart' in window || navigator.maxTouchPoints > 0
}

/**
 * 获取视口尺寸
 */
export function getViewportSize(): { width: number; height: number } {
  if (typeof window === 'undefined') {
    return { width: 0, height: 0 }
  }
  return {
    width: window.innerWidth,
    height: window.innerHeight,
  }
}

/**
 * 检测是否为横屏
 */
export function isLandscape(): boolean {
  if (typeof window === 'undefined') return false
  return window.innerWidth > window.innerHeight
}

/**
 * 检测是否为竖屏
 */
export function isPortrait(): boolean {
  if (typeof window === 'undefined') return false
  return window.innerHeight > window.innerWidth
}

/**
 * 获取设备检测的详细信息（用于调试）
 */
export function getDeviceDetectionInfo() {
  if (typeof window === 'undefined') {
    return {
      isMobile: false,
      isTablet: false,
      deviceType: 'desktop' as DeviceType,
      viewport: { width: 0, height: 0 },
      userAgent: '',
      hasTouchSupport: false,
      maxTouchPoints: 0,
      details: 'Window is undefined',
    }
  }

  const viewport = getViewportSize()
  const userAgent = navigator.userAgent || navigator.vendor || (window as any).opera
  const hasTouchSupport = supportsTouch()
  const maxTouchPoints = navigator.maxTouchPoints || 0

  return {
    isMobile: isMobileDevice(),
    isTablet: isTabletDevice(),
    deviceType: getDeviceType(),
    viewport,
    userAgent,
    hasTouchSupport,
    maxTouchPoints,
    details: {
      viewportCheck: viewport.width <= 768,
      touchCheck: hasTouchSupport,
      userAgentCheck: /android|webos|iphone|ipad|ipod|blackberry|iemobile|opera mini/i.test(userAgent),
      aspectRatio: viewport.width > 0 ? viewport.height / viewport.width : 0,
    },
  }
}

