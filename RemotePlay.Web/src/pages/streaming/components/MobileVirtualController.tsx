import { useRef, useState, useCallback, useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import { useDevice } from '@/hooks/use-device'
import { controllerService } from '@/service/controller.service'
import { getStreamingButtonName } from '@/types/controller-mapping'
import { ArrowLeft, Activity, RotateCw, ChevronUp, ChevronDown } from 'lucide-react'
import { AXIS_DEADZONE, MOBILE_SEND_INTERVAL_MS, MAX_HEARTBEAT_INTERVAL_MS } from '@/hooks/use-streaming-connection/constants'
import plainL2Png from '@/assets/plain-L2.png'
import plainL1Png from '@/assets/plain-L1.png'
import plainR2Png from '@/assets/plain-R2.png'
import plainR1Png from '@/assets/plain-R1.png'
import trianglePng from '@/assets/triangle.png'
import squarePng from '@/assets/square.png'
import circlePng from '@/assets/circle.png'
import crossPng from '@/assets/cross.png'
import directionUpPng from '@/assets/direction-up.png'
import directionLeftPng from '@/assets/direction-left.png'
import directionRightPng from '@/assets/direction-right.png'
import directionDownPng from '@/assets/direction-down.png'
import sharePng from '@/assets/plain-small-share.png'
import optionsPng from '@/assets/plain-small-option.png'
import psPng from '@/assets/outline-PS.png'

interface VirtualJoystickState {
  x: number
  y: number
  displayX: number
  displayY: number
  isActive: boolean
  touchX: number
  touchY: number
}

interface MobileVirtualControllerProps {
  sessionId: string | null
  isVisible: boolean
  onBack?: () => void
  onRefresh?: () => void
  isStatsEnabled?: boolean
  onStatsToggle?: (enabled: boolean) => void
}

interface ButtonConfig {
  name: string
  icon: string | React.ComponentType<React.SVGProps<SVGSVGElement>>
  alt: string
  style: React.CSSProperties
}

const STICK_CONFIG = {
  radius: 60,
  maxDistance: 15,
  displayMaxDistance: 50,
  responseCurve: 0.25,
} as const

const BUTTON_FEEDBACK = {
  duration: 200,
  filter: 'brightness(1.3) saturate(3) hue-rotate(200deg) drop-shadow(0 0 15px rgba(50, 150, 255, 1))',
} as const

// 震动反馈配置
const VIBRATION_PATTERNS: Record<'tap' | 'press' | 'release', number[]> = {
  tap: [10], // 短按：10ms 震动
  press: [15], // 长按开始：15ms 震动
  release: [8], // 释放：8ms 震动
}

/**
 * 触发手机震动反馈
 * @param pattern 震动模式数组，例如 [10, 50, 10] 表示震动10ms，暂停50ms，再震动10ms
 */
function vibrate(pattern: number | number[]): void {
  if (typeof window === 'undefined' || !navigator.vibrate) {
    return
  }
  
  try {
    navigator.vibrate(pattern)
  } catch (error) {
    // 忽略震动错误（某些浏览器可能不支持）
  }
}

const JOYSTICK_AREA = {
  left: { minX: 0.05, maxX: 0.4 },
  right: { minX: 0.35, maxX: 0.7 },
  vertical: { minY: 0.5, maxY: 0.85 },
  buttonExclude: { width: 250, height: 400 },
} as const

const LEFT_BUTTONS: ButtonConfig[] = [
  {
    name: 'L2',
    icon: plainL2Png,
    alt: 'L2',
    style: {
      top: '23%',
      left: '50%',
      width: '100px',
      height: '100px',
    },
  },
  {
    name: 'L1',
    icon: plainL1Png,
    alt: 'L1',
    style: {
      top: '45%',
      left: '130%',
      width: '100px',
      height: '100px',
    },
  },
]

const DPAD_BUTTONS: ButtonConfig[] = [
  {
    name: 'DPAD_UP',
    icon: directionUpPng,
    alt: 'Up',
    style: {
      top: '130%',
      left: '140%',
      transform: 'translateX(-50%)',
      width: `${(55 * 17) / 23}px`,
      height: '55px',
    },
  },
  {
    name: 'DPAD_LEFT',
    icon: directionLeftPng,
    alt: 'Left',
    style: {
      left: '60%',
      top: '200%',
      transform: 'translateY(-50%)',
      width: '55px',
      height: `${(55 * 17) / 23}px`,
    },
  },
  {
    name: 'DPAD_RIGHT',
    icon: directionRightPng,
    alt: 'Right',
    style: {
      left: '160%',
      top: '200%',
      transform: 'translateY(-50%)',
      width: '55px',
      height: `${(55 * 17) / 23}px`,
    },
  },
  {
    name: 'DPAD_DOWN',
    icon: directionDownPng,
    alt: 'Down',
    style: {
      top: '210%',
      left: '140%',
      transform: 'translateX(-50%)',
      width: `${(55 * 17) / 23}px`,
      height: '55px',
    },
  },
]

const RIGHT_BUTTONS: ButtonConfig[] = [
  {
    name: 'R2',
    icon: plainR2Png,
    alt: 'R2',
    style: {
      top: '23%',
      right: '20%',
      width: '100px',
      height: '100px',
    },
  },
  {
    name: 'R1',
    icon: plainR1Png,
    alt: 'R1',
    style: {
      top: '45%',
      right: '100%',
      width: '100px',
      height: '100px',
    },
  },
]

const ACTION_BUTTONS: ButtonConfig[] = [
  {
    name: 'TRIANGLE',
    icon: trianglePng,
    alt: 'Triangle',
    style: {
      top: '120%',
      right: '70%',
      transform: 'translateX(-50%)',
      width: '45px',
      height: '45px',
    },
  },
  {
    name: 'SQUARE',
    icon: squarePng,
    alt: 'Square',
    style: {
      right: '150%',
      top: '200%',
      transform: 'translateY(-50%)',
      width: '45px',
      height: '45px',
    },
  },
  {
    name: 'CIRCLE',
    icon: circlePng,
    alt: 'Circle',
    style: {
      right: '40%',
      top: '200%',
      transform: 'translateY(-50%)',
      width: '45px',
      height: '45px',
    },
  },
  {
    name: 'CROSS',
    icon: crossPng,
    alt: 'Cross',
    style: {
      top: '230%',
      right: '70%',
      transform: 'translateX(-50%)',
      width: '46px',
      height: '46px',
    },
  },
]

const BOTTOM_BUTTONS: ButtonConfig[] = [
  {
    name: 'SHARE',
    icon: sharePng,
    alt: 'Share',
    style: {
      width: '20px',
      height: '20px',
    },
  },
  {
    name: 'PS',
    icon: psPng,
    alt: 'PS',
    style: {
      width: '20px',
      height: '20px',
    },
  },
  {
    name: 'OPTIONS',
    icon: optionsPng,
    alt: 'Options',
    style: {
      width: '20px',
      height: '20px',
    },
  },
]

interface VirtualButtonProps {
  config: ButtonConfig
  isActive: boolean
  onClick: (buttonName: string, action: 'press' | 'release' | 'tap') => void
}

const LONG_PRESS_DELAY_MS = 200 // 长按检测延迟（毫秒）

function VirtualButton({ config, isActive, onClick }: VirtualButtonProps) {
  const isBottomButton = config.name === 'PS' || config.name === 'SHARE' || config.name === 'OPTIONS'
  const IconComponent = typeof config.icon === 'string' ? null : config.icon
  const longPressTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const isLongPressingRef = useRef(false)
  const touchStartTimeRef = useRef(0)
  const buttonRef = useRef<HTMLButtonElement>(null)
  
  // 使用原生事件监听器以避免 passive 事件监听器问题
  useEffect(() => {
    const button = buttonRef.current
    if (!button) return

    const handleTouchStart = (e: TouchEvent) => {
      e.preventDefault()
      e.stopPropagation()
      
      touchStartTimeRef.current = Date.now()
      isLongPressingRef.current = false
      
      // 清除可能存在的定时器
      if (longPressTimerRef.current) {
        clearTimeout(longPressTimerRef.current)
        longPressTimerRef.current = null
      }
      
      // 设置长按检测定时器
      longPressTimerRef.current = setTimeout(() => {
        isLongPressingRef.current = true
        onClick(config.name, 'press')
      }, LONG_PRESS_DELAY_MS)
    }
    
    const handleTouchEnd = (e: TouchEvent) => {
      e.preventDefault()
      e.stopPropagation()
      
      // 清除长按定时器
      if (longPressTimerRef.current) {
        clearTimeout(longPressTimerRef.current)
        longPressTimerRef.current = null
      }
      
      // 如果已经触发了长按，发送释放事件
      if (isLongPressingRef.current) {
        onClick(config.name, 'release')
        isLongPressingRef.current = false
      } else {
        // 短按，发送点击事件
        const pressDuration = Date.now() - touchStartTimeRef.current
        if (pressDuration < LONG_PRESS_DELAY_MS) {
          onClick(config.name, 'tap')
        }
      }
    }
    
    const handleTouchCancel = (e: TouchEvent) => {
      e.preventDefault()
      e.stopPropagation()
      
      // 清除长按定时器
      if (longPressTimerRef.current) {
        clearTimeout(longPressTimerRef.current)
        longPressTimerRef.current = null
      }
      
      // 如果正在长按，发送释放事件
      if (isLongPressingRef.current) {
        onClick(config.name, 'release')
        isLongPressingRef.current = false
      }
    }
    
    // 使用 { passive: false } 确保 preventDefault 可以工作
    button.addEventListener('touchstart', handleTouchStart, { passive: false })
    button.addEventListener('touchend', handleTouchEnd, { passive: false })
    button.addEventListener('touchcancel', handleTouchCancel, { passive: false })
    
    return () => {
      button.removeEventListener('touchstart', handleTouchStart)
      button.removeEventListener('touchend', handleTouchEnd)
      button.removeEventListener('touchcancel', handleTouchCancel)
      if (longPressTimerRef.current) {
        clearTimeout(longPressTimerRef.current)
      }
    }
  }, [config.name, onClick])
  
  const handleContextMenu = useCallback((e: React.MouseEvent) => {
    e.preventDefault()
    e.stopPropagation()
  }, [])
  
  return (
    <button
      ref={buttonRef}
      className={`flex items-center justify-center active:scale-95 transition-all ${
        isBottomButton ? 'relative' : 'absolute'
      }`}
      style={{
        touchAction: 'none',
        WebkitUserSelect: 'none',
        userSelect: 'none',
        ...config.style,
      }}
      onContextMenu={handleContextMenu}
    >
      {IconComponent ? (
        <IconComponent
          className="w-full h-full transition-all duration-200"
          style={{
            filter: isActive ? BUTTON_FEEDBACK.filter : 'none',
            WebkitFontSmoothing: 'antialiased',
            MozOsxFontSmoothing: 'grayscale',
            textRendering: 'optimizeLegibility',
            imageRendering: 'auto',
            shapeRendering: 'geometricPrecision',
            transform: 'scale(1)',
            WebkitTransform: 'scale(1)',
          }}
        />
      ) : (
        <img
          src={config.icon as string}
          alt={config.alt}
          className="w-full h-full transition-all duration-200"
          style={{
            filter: isActive ? BUTTON_FEEDBACK.filter : 'none',
            imageRendering: 'auto',
            WebkitFontSmoothing: 'antialiased',
            MozOsxFontSmoothing: 'grayscale',
          }}
          draggable={false}
        />
      )}
    </button>
  )
}

interface VirtualJoystickProps {
  stick: VirtualJoystickState
  radius: number
  maxDistance: number
}

function VirtualJoystick({ stick, radius, maxDistance }: VirtualJoystickProps) {
  if (!stick.isActive) return null

  const displayX = Math.max(-maxDistance, Math.min(maxDistance, stick.displayX || 0))
  const displayY = Math.max(-maxDistance, Math.min(maxDistance, stick.displayY || 0))

  return (
    <div
      className="absolute pointer-events-none"
      style={{
        left: `${stick.touchX - radius}px`,
        top: `${stick.touchY - radius}px`,
        width: `${radius * 2}px`,
        height: `${radius * 2}px`,
      }}
    >
      <div className="w-full h-full rounded-full bg-white/20 border-2 border-white/40 flex items-center justify-center">
        <div
          className="w-12 h-12 rounded-full bg-white/60 border-2 border-white"
          style={{
            transform: `translate(${displayX}px, ${displayY}px)`,
            transition: 'transform 0.05s linear',
          }}
        />
      </div>
    </div>
  )
}

export function MobileVirtualController({
  sessionId,
  isVisible,
  onBack,
  onRefresh,
  isStatsEnabled = false,
  onStatsToggle,
}: MobileVirtualControllerProps) {
  const { t } = useTranslation()
  const { isMobile } = useDevice()
  const [leftStick, setLeftStick] = useState<VirtualJoystickState>({
    x: 0,
    y: 0,
    displayX: 0,
    displayY: 0,
    isActive: false,
    touchX: 0,
    touchY: 0,
  })
  const [rightStick, setRightStick] = useState<VirtualJoystickState>({
    x: 0,
    y: 0,
    displayX: 0,
    displayY: 0,
    isActive: false,
    touchX: 0,
    touchY: 0,
  })
  const [activeButton, setActiveButton] = useState<string | null>(null)
  const [isBottomBarVisible, setIsBottomBarVisible] = useState(false)

  const activeTouchIdRef = useRef<{ left: number | null; right: number | null }>({
    left: null,
    right: null,
  })
  const bottomBarToggleTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const sticksValueRef = useRef<{ left: { x: number; y: number }; right: { x: number; y: number } }>({
    left: { x: 0, y: 0 },
    right: { x: 0, y: 0 },
  })
  const stickInitialPosRef = useRef<{ left: { x: number; y: number } | null; right: { x: number; y: number } | null }>({
    left: null,
    right: null,
  })
  const calculateStickValue = useCallback(
    (touchX: number, touchY: number, initialX: number, initialY: number) => {
      const dx = touchX - initialX
      const dy = touchY - initialY
      const distance = Math.sqrt(dx * dx + dy * dy)

      const clampedDisplayDistance = Math.min(distance, STICK_CONFIG.displayMaxDistance)
      const displayX = distance > 0 ? (dx / distance) * clampedDisplayDistance : 0
      const displayY = distance > 0 ? (dy / distance) * clampedDisplayDistance : 0

      let x = 0
      let y = 0
      if (distance > 0) {
        const normalizedDistance = Math.min(distance / STICK_CONFIG.maxDistance, 1)
        const responseValue = Math.pow(normalizedDistance, STICK_CONFIG.responseCurve)
        
        x = (dx / distance) * responseValue
        y = (dy / distance) * responseValue
      }

      return {
        x: Math.max(-1, Math.min(1, x)),
        y: Math.max(-1, Math.min(1, y)),
        displayX,
        displayY,
      }
    },
    []
  )

  const lastSentRef = useRef<{ leftX: number; leftY: number; rightX: number; rightY: number; timestamp: number }>({
    leftX: 0,
    leftY: 0,
    rightX: 0,
    rightY: 0,
    timestamp: 0,
  })
  const stickIntervalRef = useRef<ReturnType<typeof setInterval> | null>(null)

  const updateSticksValue = useCallback(
    (leftX: number, leftY: number, rightX: number, rightY: number) => {
      sticksValueRef.current.left = { x: leftX, y: leftY }
      sticksValueRef.current.right = { x: rightX, y: rightY }
    },
    []
  )

  useEffect(() => {
    if (!sessionId) {
      if (stickIntervalRef.current !== null) {
        clearInterval(stickIntervalRef.current)
        stickIntervalRef.current = null
      }
      return
    }

    const sendLatest = () => {
      const now = performance.now()
      const current = sticksValueRef.current
      const lastSent = lastSentRef.current
      
      const stickDiff =
        Math.abs(current.left.x - lastSent.leftX) +
        Math.abs(current.left.y - lastSent.leftY) +
        Math.abs(current.right.x - lastSent.rightX) +
        Math.abs(current.right.y - lastSent.rightY)
      
      const shouldHeartbeat = now - lastSent.timestamp >= MAX_HEARTBEAT_INTERVAL_MS
      const shouldSendSticks = stickDiff > AXIS_DEADZONE || shouldHeartbeat

      if (shouldSendSticks) {
        controllerService.sendSticks(current.left.x, current.left.y, current.right.x, current.right.y).catch(() => {})
        lastSentRef.current = {
          leftX: current.left.x,
          leftY: current.left.y,
          rightX: current.right.x,
          rightY: current.right.y,
          timestamp: now,
        }
      }
    }

    sendLatest()
    stickIntervalRef.current = window.setInterval(sendLatest, MOBILE_SEND_INTERVAL_MS)

    return () => {
      if (stickIntervalRef.current !== null) {
        clearInterval(stickIntervalRef.current)
        stickIntervalRef.current = null
      }
    }
  }, [sessionId])

  const updateStick = useCallback(
    (
      stickType: 'left' | 'right',
      touch: Touch,
      initialX: number,
      initialY: number
    ) => {
      const { x, y, displayX, displayY } = calculateStickValue(
        touch.clientX,
        touch.clientY,
        initialX,
        initialY
      )
      const setter = stickType === 'left' ? setLeftStick : setRightStick

      setter((prev) => {
        const newState = {
          x,
          y,
          displayX,
          displayY,
          isActive: true,
          touchX: prev.touchX,
          touchY: prev.touchY,
        }
        const otherStick = stickType === 'left' ? sticksValueRef.current.right : sticksValueRef.current.left
        if (stickType === 'left') {
          updateSticksValue(x, y, otherStick.x, otherStick.y)
        } else {
          updateSticksValue(otherStick.x, otherStick.y, x, y)
        }
        return newState
      })
    },
    [calculateStickValue, updateSticksValue]
  )

  const handleGlobalTouchStart = useCallback(
    (e: TouchEvent) => {
      if (!isMobile || !sessionId) return

      for (const touch of Array.from(e.touches)) {
        const touchX = touch.clientX
        const touchY = touch.clientY
        const viewportWidth = window.innerWidth
        const viewportHeight = window.innerHeight

        const target = e.target as HTMLElement
        if (
          target &&
          (target.tagName === 'BUTTON' ||
            target.closest('button') !== null ||
            target.closest('[role="button"]') !== null)
        ) {
          continue
        }

        const isLeftButtonArea =
          touchX < JOYSTICK_AREA.buttonExclude.width && touchY < JOYSTICK_AREA.buttonExclude.height
        const isRightButtonArea =
          touchX > viewportWidth - JOYSTICK_AREA.buttonExclude.width &&
          touchY < JOYSTICK_AREA.buttonExclude.height

        if (isLeftButtonArea || isRightButtonArea) {
          continue
        }

        const isLeftArea =
          touchX < viewportWidth * JOYSTICK_AREA.left.maxX &&
          touchX > viewportWidth * JOYSTICK_AREA.left.minX
        const isRightArea =
          touchX > viewportWidth * JOYSTICK_AREA.right.minX &&
          touchX < viewportWidth * JOYSTICK_AREA.right.maxX
        const isBottomMiddleVertical =
          touchY > viewportHeight * JOYSTICK_AREA.vertical.minY &&
          touchY < viewportHeight * JOYSTICK_AREA.vertical.maxY

        if (isLeftArea && isBottomMiddleVertical && activeTouchIdRef.current.left === null) {
          activeTouchIdRef.current.left = touch.identifier
          stickInitialPosRef.current.left = { x: touchX, y: touchY }
          setLeftStick({ x: 0, y: 0, displayX: 0, displayY: 0, isActive: true, touchX, touchY })
          sticksValueRef.current.left = { x: 0, y: 0 }
        } else if (isRightArea && isBottomMiddleVertical && activeTouchIdRef.current.right === null) {
          activeTouchIdRef.current.right = touch.identifier
          stickInitialPosRef.current.right = { x: touchX, y: touchY }
          setRightStick({ x: 0, y: 0, displayX: 0, displayY: 0, isActive: true, touchX, touchY })
          sticksValueRef.current.right = { x: 0, y: 0 }
        }
      }
    },
    [isMobile, sessionId]
  )

  const handleGlobalTouchMove = useCallback(
    (e: TouchEvent) => {
      if (!isMobile || !sessionId) return

      if (activeTouchIdRef.current.left !== null && stickInitialPosRef.current.left) {
        const touch = Array.from(e.touches).find((t) => t.identifier === activeTouchIdRef.current.left)
        if (touch) {
          const initialPos = stickInitialPosRef.current.left
          updateStick('left', touch, initialPos.x, initialPos.y)
        }
      }

      if (activeTouchIdRef.current.right !== null && stickInitialPosRef.current.right) {
        const touch = Array.from(e.touches).find((t) => t.identifier === activeTouchIdRef.current.right)
        if (touch) {
          const initialPos = stickInitialPosRef.current.right
          updateStick('right', touch, initialPos.x, initialPos.y)
        }
      }
    },
    [isMobile, sessionId, updateStick]
  )

  const handleGlobalTouchEnd = useCallback(
    (e: TouchEvent) => {
      if (!isMobile) return

      if (activeTouchIdRef.current.left !== null) {
        const touchStillActive = Array.from(e.touches).some(
          (t) => t.identifier === activeTouchIdRef.current.left
        )
        if (!touchStillActive) {
          activeTouchIdRef.current.left = null
          stickInitialPosRef.current.left = null
          sticksValueRef.current.left = { x: 0, y: 0 }
          setLeftStick({ x: 0, y: 0, displayX: 0, displayY: 0, isActive: false, touchX: 0, touchY: 0 })
        }
      }

      if (activeTouchIdRef.current.right !== null) {
        const touchStillActive = Array.from(e.touches).some(
          (t) => t.identifier === activeTouchIdRef.current.right
        )
        if (!touchStillActive) {
          activeTouchIdRef.current.right = null
          stickInitialPosRef.current.right = null
          sticksValueRef.current.right = { x: 0, y: 0 }
          setRightStick({ x: 0, y: 0, displayX: 0, displayY: 0, isActive: false, touchX: 0, touchY: 0 })
        }
      }
    },
    [isMobile]
  )

  useEffect(() => {
    if (typeof window === 'undefined' || !isMobile || !isVisible) return

    window.addEventListener('touchstart', handleGlobalTouchStart, { passive: false })
    window.addEventListener('touchmove', handleGlobalTouchMove, { passive: false })
    window.addEventListener('touchend', handleGlobalTouchEnd, { passive: false })

    return () => {
      window.removeEventListener('touchstart', handleGlobalTouchStart)
      window.removeEventListener('touchmove', handleGlobalTouchMove)
      window.removeEventListener('touchend', handleGlobalTouchEnd)
    }
  }, [isMobile, isVisible, handleGlobalTouchStart, handleGlobalTouchMove, handleGlobalTouchEnd])

  const activeButtonTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  
  const handleButtonClick = useCallback(
    async (buttonName: string, action: 'press' | 'release' | 'tap' = 'tap') => {
      if (!sessionId) return

      // 清除之前的定时器
      if (activeButtonTimeoutRef.current) {
        clearTimeout(activeButtonTimeoutRef.current)
        activeButtonTimeoutRef.current = null
      }

      // 触发震动反馈
      vibrate(VIBRATION_PATTERNS[action])

      // 对于 press 和 tap，显示按钮反馈
      if (action === 'press') {
        // 长按：保持激活状态直到释放
        setActiveButton(buttonName)
      } else if (action === 'tap') {
        // 短按：短暂显示反馈
        setActiveButton(buttonName)
        activeButtonTimeoutRef.current = setTimeout(() => {
          setActiveButton(null)
        }, BUTTON_FEEDBACK.duration)
      } else if (action === 'release') {
        // 释放：清除激活状态
        setActiveButton(null)
      }

      try {
        const streamingButtonName = getStreamingButtonName(buttonName as any)
        await controllerService.sendButton(streamingButtonName, action, action === 'tap' ? 50 : 0)
      } catch {}
    },
    [sessionId]
  )
  
  // 清理定时器
  useEffect(() => {
    return () => {
      if (activeButtonTimeoutRef.current) {
        clearTimeout(activeButtonTimeoutRef.current)
      }
    }
  }, [])

  const handleBottomBarToggle = useCallback(() => {
    setIsBottomBarVisible((prev) => !prev)
  }, [])

  useEffect(() => {
    if (isBottomBarVisible) {
      if (bottomBarToggleTimeoutRef.current) {
        clearTimeout(bottomBarToggleTimeoutRef.current)
      }
      bottomBarToggleTimeoutRef.current = setTimeout(() => {
        setIsBottomBarVisible(false)
      }, 3000)
    }
    return () => {
      if (bottomBarToggleTimeoutRef.current) {
        clearTimeout(bottomBarToggleTimeoutRef.current)
      }
    }
  }, [isBottomBarVisible])

  const handleBottomBarInteraction = useCallback(() => {
    if (isBottomBarVisible && bottomBarToggleTimeoutRef.current) {
      clearTimeout(bottomBarToggleTimeoutRef.current)
      bottomBarToggleTimeoutRef.current = setTimeout(() => {
        setIsBottomBarVisible(false)
      }, 3000)
    }
  }, [isBottomBarVisible])

  if (!isMobile || !isVisible) {
    return null
  }

  return (
    <div className="fixed inset-0 pointer-events-none z-[100]" style={{ touchAction: 'none' }}>
      <VirtualJoystick
        stick={leftStick}
        radius={STICK_CONFIG.radius}
        maxDistance={STICK_CONFIG.displayMaxDistance}
      />
      <VirtualJoystick
        stick={rightStick}
        radius={STICK_CONFIG.radius}
        maxDistance={STICK_CONFIG.displayMaxDistance}
      />

      <div className="absolute pointer-events-auto" style={{ top: '0', left: '16px' }}>
        {LEFT_BUTTONS.map((config) => (
          <VirtualButton
            key={config.name}
            config={config}
            isActive={activeButton === config.name}
            onClick={handleButtonClick}
          />
        ))}

        <div
          className="relative pointer-events-auto"
          style={{
            width: '88px',
            height: '88px',
            marginTop: '100px',
          }}
        >
          {DPAD_BUTTONS.map((config) => (
            <VirtualButton
              key={config.name}
              config={config}
              isActive={activeButton === config.name}
              onClick={handleButtonClick}
            />
          ))}
        </div>
      </div>

      <div className="absolute pointer-events-auto" style={{ top: '0', right: '16px' }}>
        {RIGHT_BUTTONS.map((config) => (
          <VirtualButton
            key={config.name}
            config={config}
            isActive={activeButton === config.name}
            onClick={handleButtonClick}
          />
        ))}

        <div
          className="relative pointer-events-auto"
          style={{
            width: '88px',
            height: '88px',
            marginTop: '100px',
          }}
        >
          {ACTION_BUTTONS.map((config) => (
            <VirtualButton
              key={config.name}
              config={config}
              isActive={activeButton === config.name}
              onClick={handleButtonClick}
            />
          ))}
        </div>
      </div>

      <button
        onClick={(e) => {
          e.preventDefault()
          e.stopPropagation()
          handleBottomBarToggle()
        }}
        className="fixed z-[100] w-8 h-8 rounded-full bg-black/80 backdrop-blur-md border border-white/40 flex items-center justify-center text-white shadow-lg active:scale-90 transition-all pointer-events-auto"
        style={{
          touchAction: 'manipulation',
          right: '12px',
          bottom: isBottomBarVisible ? '48px' : '12px',
          WebkitTapHighlightColor: 'transparent',
        }}
        aria-label={isBottomBarVisible ? t('streaming.menu.hide', '隐藏菜单') : t('streaming.menu.show', '显示菜单')}
      >
        {isBottomBarVisible ? (
          <ChevronDown className="h-4 w-4" strokeWidth={2} />
        ) : (
          <ChevronUp className="h-4 w-4" strokeWidth={2} />
        )}
      </button>

      <div
        className={`fixed bottom-0 left-0 right-0 z-[100] pointer-events-auto transition-transform duration-300 ease-out ${
          isBottomBarVisible ? 'translate-y-0' : 'translate-y-full'
        }`}
        onTouchStart={handleBottomBarInteraction}
        onClick={handleBottomBarInteraction}
      >
        <div className="bg-black/30 backdrop-blur-sm border-t border-white/20 px-4 py-1.5">
          <div className="flex items-center justify-between">
          <div className="flex items-center gap-3">
            {onBack && (
              <button
                onClick={(e) => {
                  e.stopPropagation()
                  onBack()
                }}
                className="flex items-center justify-center text-white/80 active:text-white active:scale-95 transition-all"
                style={{
                  touchAction: 'manipulation',
                  width: '32px',
                  height: '32px',
                }}
                aria-label={t('streaming.menu.back', '返回')}
              >
                <ArrowLeft className="h-4 w-4" />
              </button>
            )}

            {onRefresh && (
              <button
                onClick={(e) => {
                  e.stopPropagation()
                  onRefresh()
                }}
                className="flex items-center justify-center text-white/80 active:text-white active:scale-95 transition-all"
                style={{
                  touchAction: 'manipulation',
                  width: '32px',
                  height: '32px',
                }}
                aria-label={t('streaming.refresh.label', '刷新串流')}
              >
                <RotateCw className="h-4 w-4" />
              </button>
            )}

            {onStatsToggle && (
              <button
                onClick={(e) => {
                  e.stopPropagation()
                  onStatsToggle(!isStatsEnabled)
                }}
                className={`flex items-center justify-center active:scale-95 transition-all ${
                  isStatsEnabled ? 'text-white' : 'text-white/80 active:text-white'
                }`}
                style={{
                  touchAction: 'manipulation',
                  width: '32px',
                  height: '32px',
                }}
                aria-label={
                  isStatsEnabled
                    ? t('streaming.monitor.disable', '关闭统计')
                    : t('streaming.monitor.enable', '显示统计')
                }
              >
                <Activity className="h-4 w-4" />
              </button>
            )}
          </div>

            <div className="absolute left-1/2 transform -translate-x-1/2 flex items-center gap-6">
              {BOTTOM_BUTTONS.map((config) => (
                <VirtualButton
                  key={config.name}
                  config={config}
                  isActive={activeButton === config.name}
                  onClick={handleButtonClick}
                />
              ))}
            </div>

            <div className="flex items-center gap-3" style={{ width: '120px' }}></div>
          </div>
        </div>
      </div>
    </div>
  )
}
