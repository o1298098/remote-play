import { useRef, useState, useCallback, useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import { useDevice } from '@/hooks/use-device'
import { controllerService } from '@/service/controller.service'
import { getStreamingButtonName } from '@/types/controller-mapping'
import { ArrowLeft, Activity, RotateCw, ChevronUp, ChevronDown } from 'lucide-react'
import plainL2Icon from '@/assets/plain-L2.svg'
import plainL1Icon from '@/assets/plain-L1.svg'
import plainR2Icon from '@/assets/plain-R2.svg'
import plainR1Icon from '@/assets/plain-R1.svg'
import triangleIcon from '@/assets/triangle.svg'
import squareIcon from '@/assets/square.svg'
import circleIcon from '@/assets/circle.svg'
import crossIcon from '@/assets/cross.svg'
import directionUpIcon from '@/assets/direction-up.svg'
import directionLeftIcon from '@/assets/direction-left.svg'
import directionRightIcon from '@/assets/direction-right.svg'
import directionDownIcon from '@/assets/direction-down.svg'
import psIcon from '@/assets/outline-PS.svg'
import shareIcon from '@/assets/plain-small-share.svg'
import optionsIcon from '@/assets/plain-small-option.svg'

// ==================== 类型定义 ====================
interface VirtualJoystickState {
  x: number // 响应曲线处理后的值（-1 到 1），用于发送
  y: number // 响应曲线处理后的值（-1 到 1），用于发送
  displayX: number // 实际物理移动距离（像素），用于显示
  displayY: number // 实际物理移动距离（像素），用于显示
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
  icon: string
  alt: string
  style: React.CSSProperties
}

// ==================== 常量配置 ====================
const STICK_CONFIG = {
  radius: 60,
  maxDistance: 15, // 响应距离：手指只需移动15px就能达到最大值（用于计算发送值）
  displayMaxDistance: 50, // 显示距离：摇杆视觉显示的最大移动距离（用于视觉反馈）
  throttleMs: 50,
  responseCurve: 0.25, // 极其激进的响应曲线，极小移动就有高响应
} as const

const BUTTON_FEEDBACK = {
  duration: 200,
  filter: 'brightness(1.1) saturate(2.5) hue-rotate(210deg) drop-shadow(0 0 12px rgba(50, 150, 255, 0.9))',
} as const

const JOYSTICK_AREA = {
  left: { minX: 0.05, maxX: 0.4 },
  right: { minX: 0.35, maxX: 0.7 },
  vertical: { minY: 0.5, maxY: 0.85 },
  buttonExclude: { width: 250, height: 400 },
} as const

// ==================== 按钮配置 ====================
const LEFT_BUTTONS: ButtonConfig[] = [
  {
    name: 'L2',
    icon: plainL2Icon,
    alt: 'L2',
    style: {
      top: '20%',
      left: '60%',
      transform: 'rotate(-12deg)',
      width: '100px',
      height: '100px',
    },
  },
  {
    name: 'L1',
    icon: plainL1Icon,
    alt: 'L1',
    style: {
      top: '45%',
      left: '140%',
      transform: 'rotate(-12deg)',
      width: '100px',
      height: '100px',
    },
  },
]

const DPAD_BUTTONS: ButtonConfig[] = [
  {
    name: 'DPAD_UP',
    icon: directionUpIcon,
    alt: 'Up',
    style: {
      top: '130%',
      left: '150%',
      transform: 'translateX(-50%)',
      width: '55px',
      height: '55px',
    },
  },
  {
    name: 'DPAD_LEFT',
    icon: directionLeftIcon,
    alt: 'Left',
    style: {
      left: '70%',
      top: '200%',
      transform: 'translateY(-50%)',
      width: '55px',
      height: '55px',
    },
  },
  {
    name: 'DPAD_RIGHT',
    icon: directionRightIcon,
    alt: 'Right',
    style: {
      left: '170%',
      top: '200%',
      transform: 'translateY(-50%)',
      width: '55px',
      height: '55px',
    },
  },
  {
    name: 'DPAD_DOWN',
    icon: directionDownIcon,
    alt: 'Down',
    style: {
      top: '210%',
      left: '150%',
      transform: 'translateX(-50%)',
      width: '55px',
      height: '55px',
    },
  },
]

const RIGHT_BUTTONS: ButtonConfig[] = [
  {
    name: 'R2',
    icon: plainR2Icon,
    alt: 'R2',
    style: {
      top: '20%',
      right: '20%',
      transform: 'rotate(12deg)',
      width: '100px',
      height: '100px',
    },
  },
  {
    name: 'R1',
    icon: plainR1Icon,
    alt: 'R1',
    style: {
      top: '45%',
      right: '100%',
      transform: 'rotate(12deg)',
      width: '100px',
      height: '100px',
    },
  },
]

const ACTION_BUTTONS: ButtonConfig[] = [
  {
    name: 'TRIANGLE',
    icon: triangleIcon,
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
    icon: squareIcon,
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
    icon: circleIcon,
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
    icon: crossIcon,
    alt: 'Cross',
    style: {
      top: '230%',
      right: '70%',
      transform: 'translateX(-50%)',
      width: '45px',
      height: '45px',
    },
  },
]

const BOTTOM_BUTTONS: ButtonConfig[] = [
  {
    name: 'SHARE',
    icon: shareIcon,
    alt: 'Share',
    style: {
      width: '20px',
      height: '20px',
    },
  },
  {
    name: 'PS',
    icon: psIcon,
    alt: 'PS',
    style: {
      width: '20px',
      height: '20px',
    },
  },
  {
    name: 'OPTIONS',
    icon: optionsIcon,
    alt: 'Options',
    style: {
      width: '20px',
      height: '20px',
    },
  },
]

// ==================== 可复用组件 ====================
interface VirtualButtonProps {
  config: ButtonConfig
  isActive: boolean
  onClick: (buttonName: string) => void
}

function VirtualButton({ config, isActive, onClick }: VirtualButtonProps) {
  const isBottomButton = config.name === 'PS' || config.name === 'SHARE' || config.name === 'OPTIONS'
  
  return (
    <button
      className={`flex items-center justify-center active:scale-95 transition-all ${
        isBottomButton ? 'relative' : 'absolute'
      }`}
      style={{
        touchAction: 'manipulation',
        ...config.style,
      }}
      onTouchStart={(e) => {
        e.stopPropagation()
        onClick(config.name)
      }}
    >
      <img
        src={config.icon}
        alt={config.alt}
        className="w-full h-full transition-all duration-200"
        style={{
          filter: isActive ? BUTTON_FEEDBACK.filter : 'none',
        }}
      />
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

  // 使用实际的物理移动距离来显示，限制在显示范围内
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
            transition: 'transform 0.05s linear', // 快速响应，50ms 动画
          }}
        />
      </div>
    </div>
  )
}

// ==================== 主组件 ====================
/**
 * 移动端虚拟控制器组件
 * 完全复刻 PlayStation Remote Play 虚拟按键布局
 */
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

  // ==================== 摇杆处理逻辑 ====================
  const calculateStickValue = useCallback(
    (touchX: number, touchY: number, initialX: number, initialY: number) => {
      const dx = touchX - initialX
      const dy = touchY - initialY
      const distance = Math.sqrt(dx * dx + dy * dy)

      // 计算实际的物理移动距离（用于显示）- 使用更大的显示范围
      const clampedDisplayDistance = Math.min(distance, STICK_CONFIG.displayMaxDistance)
      const displayX = distance > 0 ? (dx / distance) * clampedDisplayDistance : 0
      const displayY = distance > 0 ? (dy / distance) * clampedDisplayDistance : 0

      // 计算响应曲线处理后的值（用于发送）- 使用较小的响应范围以提高灵敏度
      let x = 0
      let y = 0
      if (distance > 0) {
        // 使用非常激进的响应曲线：极小移动就能达到高值
        const normalizedDistance = Math.min(distance / STICK_CONFIG.maxDistance, 1)
        
        // 使用指数 0.25 的响应曲线，让摇杆极其灵敏
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

  const sendSticksThrottled = useCallback(
    (() => {
      let lastSendTime = 0
      return (leftX: number, leftY: number, rightX: number, rightY: number) => {
        const now = Date.now()
        if (now - lastSendTime >= STICK_CONFIG.throttleMs && sessionId) {
          lastSendTime = now
          controllerService.sendSticks(leftX, leftY, rightX, rightY).catch(() => {
            // 静默处理错误
          })
        }
      }
    })(),
    [sessionId]
  )

  const updateStick = useCallback(
    (
      stickType: 'left' | 'right',
      touch: Touch,
      currentStick: VirtualJoystickState,
      otherStick: VirtualJoystickState
    ) => {
      const { x, y, displayX, displayY } = calculateStickValue(
        touch.clientX,
        touch.clientY,
        currentStick.touchX,
        currentStick.touchY
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
        if (stickType === 'left') {
          sendSticksThrottled(x, y, otherStick.x, otherStick.y)
        } else {
          sendSticksThrottled(otherStick.x, otherStick.y, x, y)
        }
        return newState
      })
    },
    [calculateStickValue, sendSticksThrottled]
  )

  const handleGlobalTouchStart = useCallback(
    (e: TouchEvent) => {
      if (!isMobile || !sessionId) return

      for (const touch of Array.from(e.touches)) {
        const touchX = touch.clientX
        const touchY = touch.clientY
        const viewportWidth = window.innerWidth
        const viewportHeight = window.innerHeight

        // 检查触摸目标是否是按钮元素
        const target = e.target as HTMLElement
        if (
          target &&
          (target.tagName === 'BUTTON' ||
            target.closest('button') !== null ||
            target.closest('[role="button"]') !== null)
        ) {
          continue
        }

        // 排除按钮区域
        const isLeftButtonArea =
          touchX < JOYSTICK_AREA.buttonExclude.width && touchY < JOYSTICK_AREA.buttonExclude.height
        const isRightButtonArea =
          touchX > viewportWidth - JOYSTICK_AREA.buttonExclude.width &&
          touchY < JOYSTICK_AREA.buttonExclude.height

        if (isLeftButtonArea || isRightButtonArea) {
          continue
        }

        // 判断摇杆区域
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
          setLeftStick({ x: 0, y: 0, displayX: 0, displayY: 0, isActive: true, touchX, touchY })
        } else if (isRightArea && isBottomMiddleVertical && activeTouchIdRef.current.right === null) {
          activeTouchIdRef.current.right = touch.identifier
          setRightStick({ x: 0, y: 0, displayX: 0, displayY: 0, isActive: true, touchX, touchY })
        }
      }
    },
    [isMobile, sessionId]
  )

  const handleGlobalTouchMove = useCallback(
    (e: TouchEvent) => {
      if (!isMobile || !sessionId) return

      // 处理左摇杆
      if (activeTouchIdRef.current.left !== null) {
        const touch = Array.from(e.touches).find((t) => t.identifier === activeTouchIdRef.current.left)
        if (touch) {
          updateStick('left', touch, leftStick, rightStick)
        }
      }

      // 处理右摇杆
      if (activeTouchIdRef.current.right !== null) {
        const touch = Array.from(e.touches).find((t) => t.identifier === activeTouchIdRef.current.right)
        if (touch) {
          updateStick('right', touch, rightStick, leftStick)
        }
      }
    },
    [isMobile, sessionId, leftStick, rightStick, updateStick]
  )

  const handleGlobalTouchEnd = useCallback(
    (e: TouchEvent) => {
      if (!isMobile) return

      // 检查左摇杆
      if (activeTouchIdRef.current.left !== null) {
        const touchStillActive = Array.from(e.touches).some(
          (t) => t.identifier === activeTouchIdRef.current.left
        )
        if (!touchStillActive) {
          activeTouchIdRef.current.left = null
          setLeftStick(() => {
            if (sessionId) {
              sendSticksThrottled(0, 0, rightStick.x, rightStick.y)
            }
            return { x: 0, y: 0, displayX: 0, displayY: 0, isActive: false, touchX: 0, touchY: 0 }
          })
        }
      }

      // 检查右摇杆
      if (activeTouchIdRef.current.right !== null) {
        const touchStillActive = Array.from(e.touches).some(
          (t) => t.identifier === activeTouchIdRef.current.right
        )
        if (!touchStillActive) {
          activeTouchIdRef.current.right = null
          setRightStick(() => {
            if (sessionId) {
              sendSticksThrottled(leftStick.x, leftStick.y, 0, 0)
            }
            return { x: 0, y: 0, displayX: 0, displayY: 0, isActive: false, touchX: 0, touchY: 0 }
          })
        }
      }
    },
    [isMobile, sessionId, leftStick, rightStick, sendSticksThrottled]
  )

  // 注册全局触摸事件
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

  // ==================== 按钮处理逻辑 ====================
  const handleButtonClick = useCallback(
    async (buttonName: string) => {
      if (!sessionId) return

      // 设置按钮激活状态（视觉反馈）
      setActiveButton(buttonName)
      setTimeout(() => {
        setActiveButton(null)
      }, BUTTON_FEEDBACK.duration)

      try {
        const streamingButtonName = getStreamingButtonName(buttonName as any)
        await controllerService.sendButton(streamingButtonName, 'tap', 50)
      } catch {
        // 静默处理错误
      }
    },
    [sessionId]
  )

  // ==================== 底部栏显示/隐藏逻辑 ====================
  const handleBottomBarToggle = useCallback(() => {
    setIsBottomBarVisible((prev) => !prev)
  }, [])

  // 自动隐藏底部栏（3秒后）
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

  // 点击底部栏时重置自动隐藏定时器
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
      {/* 摇杆 */}
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

      {/* 左侧按钮区域 */}
      <div className="absolute pointer-events-auto" style={{ top: '0', left: '16px' }}>
        {LEFT_BUTTONS.map((config) => (
          <VirtualButton
            key={config.name}
            config={config}
            isActive={activeButton === config.name}
            onClick={handleButtonClick}
          />
        ))}

        {/* D-pad */}
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

      {/* 右侧按钮区域 */}
      <div className="absolute pointer-events-auto" style={{ top: '0', right: '16px' }}>
        {RIGHT_BUTTONS.map((config) => (
          <VirtualButton
            key={config.name}
            config={config}
            isActive={activeButton === config.name}
            onClick={handleButtonClick}
          />
        ))}

        {/* 动作按钮 */}
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

      {/* 触发图标按钮 - 右下角，始终显示 */}
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

      {/* 底部栏按钮 */}
      <div
        className={`fixed bottom-0 left-0 right-0 z-[100] pointer-events-auto transition-transform duration-300 ease-out ${
          isBottomBarVisible ? 'translate-y-0' : 'translate-y-full'
        }`}
        onTouchStart={handleBottomBarInteraction}
        onClick={handleBottomBarInteraction}
      >
        <div className="bg-black/30 backdrop-blur-sm border-t border-white/20 px-4 py-1.5">
          <div className="flex items-center justify-between">
          {/* 左侧：原有底栏按键 */}
          <div className="flex items-center gap-3">
            {/* 返回按钮 */}
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

            {/* 刷新按钮 */}
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

            {/* 统计开关 */}
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

            {/* 中间：PS、Share、Options 按键 */}
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

            {/* 右侧：占位，保持布局平衡 */}
            <div className="flex items-center gap-3" style={{ width: '120px' }}></div>
          </div>
        </div>
      </div>
    </div>
  )
}
