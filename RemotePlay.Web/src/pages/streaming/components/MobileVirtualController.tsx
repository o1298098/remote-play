import { useRef, useState, useCallback, useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import { useDevice } from '@/hooks/use-device'
import { controllerService } from '@/service/controller.service'
import { getStreamingButtonName } from '@/types/controller-mapping'
import { ArrowLeft, Activity, RotateCw, ChevronUp, ChevronDown } from 'lucide-react'
// PNG 图标导入
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
  icon: string | React.ComponentType<React.SVGProps<SVGSVGElement>>
  alt: string
  style: React.CSSProperties
}

// ==================== 常量配置 ====================
const STICK_CONFIG = {
  radius: 60,
  maxDistance: 15, // 响应距离：手指只需移动15px就能达到最大值（用于计算发送值）
  displayMaxDistance: 50, // 显示距离：摇杆视觉显示的最大移动距离（用于视觉反馈）
  throttleMs: 16, // 降低到 16ms (约 60fps) 以获得更流畅的响应
  responseCurve: 0.25, // 极其激进的响应曲线，极小移动就有高响应
} as const

const BUTTON_FEEDBACK = {
  duration: 200,
  filter: 'brightness(1.3) saturate(3) hue-rotate(200deg) drop-shadow(0 0 15px rgba(50, 150, 255, 1))',
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
      // UP: viewBox 17×23，保持高度 55px，宽度按比例
      width: `${(55 * 17) / 23}px`, // ≈ 40.65px
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
      // LEFT: viewBox 23×17，保持宽度 55px，高度按比例
      width: '55px',
      height: `${(55 * 17) / 23}px`, // ≈ 40.65px
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
      // RIGHT: viewBox 23×17，保持宽度 55px，高度按比例
      width: '55px',
      height: `${(55 * 17) / 23}px`, // ≈ 40.65px
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
      // DOWN: viewBox 17×23，保持高度 55px，宽度按比例
      width: `${(55 * 17) / 23}px`, // ≈ 40.65px
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

// ==================== 可复用组件 ====================
interface VirtualButtonProps {
  config: ButtonConfig
  isActive: boolean
  onClick: (buttonName: string) => void
}

function VirtualButton({ config, isActive, onClick }: VirtualButtonProps) {
  const isBottomButton = config.name === 'PS' || config.name === 'SHARE' || config.name === 'OPTIONS'
  const IconComponent = typeof config.icon === 'string' ? null : config.icon
  
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
  // 使用 ref 存储最新的摇杆值，避免闭包陷阱
  const sticksValueRef = useRef<{ left: { x: number; y: number }; right: { x: number; y: number } }>({
    left: { x: 0, y: 0 },
    right: { x: 0, y: 0 },
  })
  // 使用 ref 存储摇杆的初始触摸位置，避免闭包陷阱
  const stickInitialPosRef = useRef<{ left: { x: number; y: number } | null; right: { x: number; y: number } | null }>({
    left: null,
    right: null,
  })

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
      let pendingFrame: number | null = null
      return (leftX: number, leftY: number, rightX: number, rightY: number) => {
        // 更新 ref 中的最新值
        sticksValueRef.current.left = { x: leftX, y: leftY }
        sticksValueRef.current.right = { x: rightX, y: rightY }

        const now = Date.now()
        if (now - lastSendTime >= STICK_CONFIG.throttleMs && sessionId) {
          lastSendTime = now
          // 取消待处理的帧
          if (pendingFrame !== null) {
            cancelAnimationFrame(pendingFrame)
            pendingFrame = null
          }
          controllerService.sendSticks(leftX, leftY, rightX, rightY).catch(() => {
            // 静默处理错误
          })
        } else if (sessionId && pendingFrame === null) {
          // 如果还在节流期内，使用 requestAnimationFrame 确保在下一帧发送
          pendingFrame = requestAnimationFrame(() => {
            pendingFrame = null
            const current = sticksValueRef.current
            const now = Date.now()
            if (now - lastSendTime >= STICK_CONFIG.throttleMs) {
              lastSendTime = now
              controllerService.sendSticks(current.left.x, current.left.y, current.right.x, current.right.y).catch(() => {
            // 静默处理错误
              })
            }
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
        // 使用 ref 获取最新的另一个摇杆值，避免闭包陷阱
        const otherStick = stickType === 'left' ? sticksValueRef.current.right : sticksValueRef.current.left
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

      // 处理左摇杆
      if (activeTouchIdRef.current.left !== null && stickInitialPosRef.current.left) {
        const touch = Array.from(e.touches).find((t) => t.identifier === activeTouchIdRef.current.left)
        if (touch) {
          // 使用 ref 获取初始位置，避免依赖过时的状态
          const initialPos = stickInitialPosRef.current.left
          updateStick('left', touch, initialPos.x, initialPos.y)
        }
      }

      // 处理右摇杆
      if (activeTouchIdRef.current.right !== null && stickInitialPosRef.current.right) {
        const touch = Array.from(e.touches).find((t) => t.identifier === activeTouchIdRef.current.right)
        if (touch) {
          // 使用 ref 获取初始位置，避免依赖过时的状态
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

      // 检查左摇杆
      if (activeTouchIdRef.current.left !== null) {
        const touchStillActive = Array.from(e.touches).some(
          (t) => t.identifier === activeTouchIdRef.current.left
        )
        if (!touchStillActive) {
          activeTouchIdRef.current.left = null
          stickInitialPosRef.current.left = null
          setLeftStick(() => {
            if (sessionId) {
              // 使用 ref 获取最新的右摇杆值
              const rightStick = sticksValueRef.current.right
              sendSticksThrottled(0, 0, rightStick.x, rightStick.y)
            }
            return { x: 0, y: 0, displayX: 0, displayY: 0, isActive: false, touchX: 0, touchY: 0 }
          })
          // 更新 ref
          sticksValueRef.current.left = { x: 0, y: 0 }
        }
      }

      // 检查右摇杆
      if (activeTouchIdRef.current.right !== null) {
        const touchStillActive = Array.from(e.touches).some(
          (t) => t.identifier === activeTouchIdRef.current.right
        )
        if (!touchStillActive) {
          activeTouchIdRef.current.right = null
          stickInitialPosRef.current.right = null
          setRightStick(() => {
            if (sessionId) {
              // 使用 ref 获取最新的左摇杆值
              const leftStick = sticksValueRef.current.left
              sendSticksThrottled(leftStick.x, leftStick.y, 0, 0)
            }
            return { x: 0, y: 0, displayX: 0, displayY: 0, isActive: false, touchX: 0, touchY: 0 }
          })
          // 更新 ref
          sticksValueRef.current.right = { x: 0, y: 0 }
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
