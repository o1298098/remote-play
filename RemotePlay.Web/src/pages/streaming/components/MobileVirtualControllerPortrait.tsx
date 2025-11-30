import { useRef, useState, useCallback, useEffect } from 'react'
import { useDevice } from '@/hooks/use-device'
import { ArrowLeft, Activity } from 'lucide-react'
import { controllerService } from '@/service/controller.service'
import { getStreamingButtonName } from '@/types/controller-mapping'
import { useStickInputState } from '@/hooks/use-streaming-connection/stick-input-state'
import { setVirtualStick, setVirtualStickActive } from '@/hooks/use-streaming-connection/virtual-joystick-state'
import { createKeyboardHandler } from '@/utils/keyboard-mapping'
import plainL2Gray from '@/assets/plain-L2-gray.svg'
import plainL1Gray from '@/assets/plain-L1-gray.svg'
import plainR2Gray from '@/assets/plain-R2-gray.svg'
import plainR1Gray from '@/assets/plain-R1-gray.svg'
import triangleGreen from '@/assets/outline-green-triangle.svg'
import squarePurple from '@/assets/outline-purple-square.svg'
import circleRed from '@/assets/outline-red-circle.svg'
import crossBlue from '@/assets/outline-blue-cross.svg'
import directionUpGray from '@/assets/direction-up-gray.svg'
import directionLeftGray from '@/assets/direction-left-gray.svg'
import directionRightGray from '@/assets/direction-right-gray.svg'
import directionDownGray from '@/assets/direction-down-gray.svg'
import sharePng from '@/assets/plain-small-share.png'
import optionsPng from '@/assets/plain-small-option.png'
import psPng from '@/assets/outline-PS.png'

interface MobileVirtualControllerPortraitProps {
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
  iconSize?: { width: string | number; height: string | number } // 可选的图标大小
}

const STICK_CONFIG = {
  radius: 35,
  maxDistance: 45,
  displayMaxDistance: 45,
  responseCurve: 0.95,
} as const

const BUTTON_FEEDBACK = {
  duration: 200,
  filter: 'brightness(1.3) saturate(3) hue-rotate(200deg) drop-shadow(0 0 15px rgba(50, 150, 255, 1))',
} as const

const VIBRATION_PATTERNS: Record<'tap' | 'press' | 'release', number[]> = {
  tap: [10],
  press: [15],
  release: [8],
}

function vibrate(pattern: number | number[]): void {
  if (typeof window === 'undefined' || !navigator.vibrate) {
    return
  }
  
  try {
    navigator.vibrate(pattern)
  } catch (error) {
    // 忽略震动错误
  }
}

// 竖屏布局：控制器在底部，摇杆区域在底部左右两侧
// 左摇杆占位符：bottom: '30%', left: '40%'（相对于左半部分，即整个屏幕的20%）
// 右摇杆占位符：bottom: '30%', right: '50%'（相对于右半部分，即整个屏幕的75%）
// 控制器从屏幕高度的50%开始，摇杆在控制器底部30%，即屏幕高度的70%位置

// 竖屏布局：左侧按钮（L2, L1, D-pad）
const LEFT_BUTTONS: ButtonConfig[] = [
  {
    name: 'L2',
    icon: plainL2Gray,
    alt: 'L2',
    style: {
      paddingTop: '50px',
      paddingLeft: '20px',
    },
    iconSize: { width: '80px', height: '80px' },
  },
  {
    name: 'L1',
    icon: plainL1Gray,
    alt: 'L1',
    style: {
      paddingTop: '85px',
      paddingLeft: '80px',
    },
    iconSize: { width: '80px', height: '80px' },
  },
]

const DPAD_BUTTONS: ButtonConfig[] = [
  {
    name: 'DPAD_UP',
    icon: directionUpGray,
    alt: 'Up',
    style: {
      top: '10%',
      left: '50%',
      transform: 'translateX(-50%)',
    },
    iconSize: { width: '45px', height: '35px' },
  },
  {
    name: 'DPAD_LEFT',
    icon: directionLeftGray,
    alt: 'Left',
    style: {
      left: '10%',
      top: '50%',
      transform: 'translateY(-50%)',
    },
    iconSize: { width: '35px', height: '45px' },
  },
  {
    name: 'DPAD_RIGHT',
    icon: directionRightGray,
    alt: 'Right',
    style: {
      right: '10%',
      top: '50%',
      transform: 'translateY(-50%)',
    },
    iconSize: { width: '35px', height: '45px' },
  },
  {
    name: 'DPAD_DOWN',
    icon: directionDownGray,
    alt: 'Down',
    style: {
      bottom: '10%',
      left: '50%',
      transform: 'translateX(-50%)',
    },
    iconSize: { width: '45px', height: '35px' },
  },
]

// 竖屏布局：右侧按钮（R2, R1, 动作按钮）
const RIGHT_BUTTONS: ButtonConfig[] = [
  {
    name: 'R2',
    icon: plainR2Gray,
    alt: 'R2',
    style: {
      top: '50px',
      right: '20px',
    },
    iconSize: { width: '80px', height: '80px' },
  },
  {
    name: 'R1',
    icon: plainR1Gray,
    alt: 'R1',
    style: {
      top: '85px',
      right: '80px',
    },
    iconSize: { width: '80px', height: '80px' },
  },
]

const ACTION_BUTTONS: ButtonConfig[] = [
  {
    name: 'TRIANGLE',
    icon: triangleGreen,
    alt: 'Triangle',
    style: {
      top: '5%',
      left: '50%',
      transform: 'translateX(-50%)',
    },
    iconSize: { width: '30px', height: '30px' },
  },
  {
    name: 'SQUARE',
    icon: squarePurple,
    alt: 'Square',
    style: {
      left: '5%',
      top: '50%',
      transform: 'translateY(-50%)',
    },
    iconSize: { width: '30px', height: '30px' },
  },
  {
    name: 'CIRCLE',
    icon: circleRed,
    alt: 'Circle',
    style: {
      right: '5%',
      top: '50%',
      transform: 'translateY(-50%)',
    },
    iconSize: { width: '30px', height: '30px' },
  },
  {
    name: 'CROSS',
    icon: crossBlue,
    alt: 'Cross',
    style: {
      bottom: '5%',
      left: '50%',
      transform: 'translateX(-50%)',
    },
    iconSize: { width: '30px', height: '30px' },
  },
]

const BOTTOM_BUTTONS: ButtonConfig[] = [
  {
    name: 'SHARE',
    icon: sharePng,
    alt: 'Share',
    style: {
    },
    iconSize: { width: '20px', height: '20px' },
  },
  {
    name: 'PS',
    icon: psPng,
    alt: 'PS',
    style: {
    },
    iconSize: { width: '20px', height: '20px' },
  },
  {
    name: 'OPTIONS',
    icon: optionsPng,
    alt: 'Options',
    style: {
    },
    iconSize: { width: '20px', height: '20px' },
  },
]

interface VirtualButtonProps {
  config: ButtonConfig
  isActive: boolean
  onClick: (buttonName: string, action: 'press' | 'release' | 'tap') => void
}

const LONG_PRESS_DELAY_MS = 200

function VirtualButton({ config, isActive, onClick }: VirtualButtonProps) {
  const isBottomButton = config.name === 'PS' || config.name === 'SHARE' || config.name === 'OPTIONS'
  const IconComponent = typeof config.icon === 'string' ? null : config.icon
  const longPressTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const isLongPressingRef = useRef(false)
  const touchStartTimeRef = useRef(0)
  const buttonRef = useRef<HTMLButtonElement>(null)
  
  useEffect(() => {
    const button = buttonRef.current
    if (!button) return

    const handleTouchStart = (e: TouchEvent) => {
      e.preventDefault()
      e.stopPropagation()
      
      touchStartTimeRef.current = Date.now()
      isLongPressingRef.current = false
      
      if (longPressTimerRef.current) {
        clearTimeout(longPressTimerRef.current)
        longPressTimerRef.current = null
      }
      
      longPressTimerRef.current = setTimeout(() => {
        isLongPressingRef.current = true
        onClick(config.name, 'press')
      }, LONG_PRESS_DELAY_MS)
    }
    
    const handleTouchEnd = (e: TouchEvent) => {
      e.preventDefault()
      e.stopPropagation()
      
      if (longPressTimerRef.current) {
        clearTimeout(longPressTimerRef.current)
        longPressTimerRef.current = null
      }
      
      if (isLongPressingRef.current) {
        onClick(config.name, 'release')
        isLongPressingRef.current = false
      } else {
        const pressDuration = Date.now() - touchStartTimeRef.current
        if (pressDuration < LONG_PRESS_DELAY_MS) {
          onClick(config.name, 'tap')
        }
      }
    }
    
    const handleTouchCancel = (e: TouchEvent) => {
      e.preventDefault()
      e.stopPropagation()
      
      if (longPressTimerRef.current) {
        clearTimeout(longPressTimerRef.current)
        longPressTimerRef.current = null
      }
      
      if (isLongPressingRef.current) {
        onClick(config.name, 'release')
        isLongPressingRef.current = false
      }
    }
    
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
  
  // 确定图标大小：如果配置了 iconSize 就使用它，否则使用 w-full h-full
  const iconSizeStyle = config.iconSize
    ? {
        width: typeof config.iconSize.width === 'number' ? `${config.iconSize.width}px` : config.iconSize.width,
        height: typeof config.iconSize.height === 'number' ? `${config.iconSize.height}px` : config.iconSize.height,
      }
    : {}
  
  const iconClassName = config.iconSize ? '' : 'w-full h-full'
  
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
          className={`${iconClassName} transition-all duration-200`}
          style={{
            ...iconSizeStyle,
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
          className={`${iconClassName} transition-all duration-200`}
          style={{
            ...iconSizeStyle,
            filter: isActive ? BUTTON_FEEDBACK.filter : 'none',
            imageRendering: 'auto',
            WebkitFontSmoothing: 'antialiased',
            MozOsxFontSmoothing: 'grayscale',
            objectFit: 'contain',
          }}
          draggable={false}
        />
      )}
    </button>
  )
}

// VirtualJoystick 组件已移除，摇杆现在直接渲染在占位符内部

export function MobileVirtualControllerPortrait({
  sessionId,
  isVisible,
  onBack,
  onRefresh: _onRefresh,
  isStatsEnabled = false,
  onStatsToggle,
}: MobileVirtualControllerPortraitProps) {
  const { isMobile } = useDevice()
  
  const {
    setKeyboardLeftStick,
    reset: resetStickInput,
  } = useStickInputState()
  
  const keyboardCleanupRef = useRef<(() => void) | null>(null)
  
  const [leftStickActive, setLeftStickActive] = useState(false)
  const [rightStickActive, setRightStickActive] = useState(false)
  
  const [activeButton, setActiveButton] = useState<string | null>(null)
  const [hasPhysicalGamepad, setHasPhysicalGamepad] = useState(false)
  const [showVirtualController, setShowVirtualController] = useState(true)

  const leftStickDataRef = useRef<{ x: number; y: number; displayX: number; displayY: number }>({
    x: 0,
    y: 0,
    displayX: 0,
    displayY: 0,
  })
  const rightStickDataRef = useRef<{ x: number; y: number; displayX: number; displayY: number }>({
    x: 0,
    y: 0,
    displayX: 0,
    displayY: 0,
  })

  const activeTouchIdRef = useRef<{ left: number | null; right: number | null }>({
    left: null,
    right: null,
  })
  const latestTouchPosRef = useRef<{ left: { x: number; y: number } | null; right: { x: number; y: number } | null }>({
    left: null,
    right: null,
  })
  const leftStickInnerRef = useRef<HTMLDivElement>(null)
  const rightStickInnerRef = useRef<HTMLDivElement>(null)
  const leftStickContainerRef = useRef<HTMLDivElement>(null)
  const rightStickContainerRef = useRef<HTMLDivElement>(null)
  const leftStickPlaceholderRef = useRef<HTMLDivElement>(null)
  const rightStickPlaceholderRef = useRef<HTMLDivElement>(null)
  const stickInitialPosRef = useRef<{ left: { x: number; y: number } | null; right: { x: number; y: number } | null }>({
    left: null,
    right: null,
  })
  const stickFixedPosRef = useRef<{ left: { x: number; y: number } | null; right: { x: number; y: number } | null }>({
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
        x = (dx / distance) * normalizedDistance
        y = (dy / distance) * normalizedDistance
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

  useEffect(() => {
    if (!sessionId) {
      if (keyboardCleanupRef.current) {
        keyboardCleanupRef.current()
        keyboardCleanupRef.current = null
      }
      return
    }

    const cleanup = createKeyboardHandler(
      async (buttonName: string, action: 'press' | 'release') => {
        if (!sessionId) return
        try {
          const streamingButtonName = getStreamingButtonName(buttonName as any)
          await controllerService.sendButton(streamingButtonName, action, action === 'press' ? 50 : 0)
        } catch (error) {
          console.error('❌ 键盘控制失败:', error, '按钮:', buttonName, '动作:', action)
        }
      },
      {
        onLeftStickChange: (x: number, y: number) => {
          setKeyboardLeftStick(x, y)
        },
      }
    )

    keyboardCleanupRef.current = cleanup

    return () => {
      if (keyboardCleanupRef.current) {
        keyboardCleanupRef.current()
        keyboardCleanupRef.current = null
      }
    }
  }, [sessionId, setKeyboardLeftStick])

  useEffect(() => {
    if (!sessionId) {
      resetStickInput()
      return
    }
  }, [sessionId, resetStickInput])

  useEffect(() => {
    if (!isMobile) {
      return
    }

    const checkGamepads = () => {
      try {
        const gamepads = navigator.getGamepads?.()
        if (!gamepads) {
          setHasPhysicalGamepad(false)
          return
        }

        let hasGamepad = false
        let hasButtonInput = false
        
        for (let i = 0; i < gamepads.length; i++) {
          const gamepad = gamepads[i]
          if (gamepad) {
            hasGamepad = true
            
            if (gamepad.buttons) {
              for (let j = 0; j < gamepad.buttons.length; j++) {
                const button = gamepad.buttons[j]
                if (button && button.pressed) {
                  hasButtonInput = true
                  break
                }
              }
            }
            
            if (gamepad.axes) {
              for (let j = 0; j < gamepad.axes.length; j++) {
                const axis = gamepad.axes[j]
                if (axis && Math.abs(axis) > 0.1) {
                  hasButtonInput = true
                  break
                }
              }
            }
            
            if (hasButtonInput) {
              break
            }
          }
        }
        
        setHasPhysicalGamepad(hasGamepad)
        
        if (hasButtonInput && hasGamepad) {
          setShowVirtualController(false)
        }
      } catch (error) {
        setHasPhysicalGamepad(false)
      }
    }

    checkGamepads()

    const handleGamepadConnected = () => {
      setHasPhysicalGamepad(true)
      setShowVirtualController(false)
    }

    const handleGamepadDisconnected = () => {
      checkGamepads()
    }

    window.addEventListener('gamepadconnected', handleGamepadConnected)
    window.addEventListener('gamepaddisconnected', handleGamepadDisconnected)

    const checkInterval = setInterval(checkGamepads, 100)

    return () => {
      window.removeEventListener('gamepadconnected', handleGamepadConnected)
      window.removeEventListener('gamepaddisconnected', handleGamepadDisconnected)
      clearInterval(checkInterval)
    }
  }, [isMobile])

  useEffect(() => {
    if (hasPhysicalGamepad) {
      setShowVirtualController(false)
    }
  }, [hasPhysicalGamepad])

  const performStickUpdate = useCallback(
    (stickType: 'left' | 'right', touch: Touch) => {
      // 使用固定位置作为摇杆中心，计算相对于固定位置的偏移
      const fixedPos = stickType === 'left' ? stickFixedPosRef.current.left : stickFixedPosRef.current.right
      if (!fixedPos) return
      
      // 计算触摸位置相对于固定位置的偏移
      const { x, y, displayX, displayY } = calculateStickValue(
        touch.clientX,
        touch.clientY,
        fixedPos.x,
        fixedPos.y
      )
      
      const stickDataRef = stickType === 'left' ? leftStickDataRef : rightStickDataRef
      stickDataRef.current = { x, y, displayX, displayY }
      
      setVirtualStick(stickType, x, y)
      
      const innerRef = stickType === 'left' ? leftStickInnerRef : rightStickInnerRef
      if (innerRef.current) {
        const clampedDisplayX = Math.max(-STICK_CONFIG.displayMaxDistance, Math.min(STICK_CONFIG.displayMaxDistance, displayX))
        const clampedDisplayY = Math.max(-STICK_CONFIG.displayMaxDistance, Math.min(STICK_CONFIG.displayMaxDistance, displayY))
        innerRef.current.style.transform = `translate(${clampedDisplayX}px, ${clampedDisplayY}px)`
      }
    },
    [calculateStickValue, setVirtualStick]
  )

  const updateStick = useCallback(
    (stickType: 'left' | 'right', touch: Touch) => {
      performStickUpdate(stickType, touch)
    },
    [performStickUpdate]
  )

  const handleGlobalTouchStart = useCallback(
    (e: TouchEvent) => {
      if (!isMobile || !sessionId) return

      if (!showVirtualController && hasPhysicalGamepad) {
        setShowVirtualController(true)
        return
      }

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

        // 检查是否在摇杆触发区域（优先检查，避免被按钮区域排除）
        // 使用占位符的实际位置来计算触发区域
        let leftStickCenterX = viewportWidth * 0.2 // 左摇杆中心：左半部分的40% = 整个屏幕的20%
        let leftStickCenterY = viewportHeight * 0.7 // 左摇杆中心：控制器底部30% = 屏幕高度的70%
        let rightStickCenterX = viewportWidth * 0.75 // 右摇杆中心：右半部分的50% = 整个屏幕的75%
        let rightStickCenterY = viewportHeight * 0.7 // 右摇杆中心：控制器底部30% = 屏幕高度的70%
        
        // 如果占位符元素存在，使用其实际位置
        if (leftStickPlaceholderRef.current) {
          const rect = leftStickPlaceholderRef.current.getBoundingClientRect()
          leftStickCenterX = rect.left + rect.width / 2
          leftStickCenterY = rect.top + rect.height / 2
        }
        if (rightStickPlaceholderRef.current) {
          const rect = rightStickPlaceholderRef.current.getBoundingClientRect()
          rightStickCenterX = rect.left + rect.width / 2
          rightStickCenterY = rect.top + rect.height / 2
        }
        
        // 使用像素距离判断是否在摇杆触发范围内
        const stickTriggerRadius = STICK_CONFIG.radius * 2.5 // 触发半径是摇杆半径的2.5倍
        const leftDistance = Math.sqrt(
          Math.pow(touchX - leftStickCenterX, 2) + Math.pow(touchY - leftStickCenterY, 2)
        )
        const rightDistance = Math.sqrt(
          Math.pow(touchX - rightStickCenterX, 2) + Math.pow(touchY - rightStickCenterY, 2)
        )
        
        const isLeftStickArea = leftDistance <= stickTriggerRadius && touchY > viewportHeight * 0.5
        const isRightStickArea = rightDistance <= stickTriggerRadius && touchY > viewportHeight * 0.5

        // 检查是否在按钮区域（排除摇杆区域）
        const isLeftButtonArea =
          touchX < viewportWidth * 0.5 && 
          touchY > viewportHeight * 0.5 &&
          !isLeftStickArea
        const isRightButtonArea =
          touchX > viewportWidth * 0.5 && 
          touchY > viewportHeight * 0.5 &&
          !isRightStickArea

        if (isLeftButtonArea || isRightButtonArea) {
          continue
        }

        // 使用摇杆区域检查
        const isLeftArea = isLeftStickArea
        const isRightArea = isRightStickArea
        const isBottomMiddleVertical = true // 已经在摇杆区域检查中包含了垂直范围

        if (isLeftArea && isBottomMiddleVertical && activeTouchIdRef.current.left === null) {
          // 计算左摇杆的固定位置（占位符中心）
          let fixedX = viewportWidth * 0.2 // 左侧区域的中心位置（约20%）
          let fixedY = viewportHeight * 0.7 // 控制器区域底部30%位置
          
          // 如果占位符元素存在，使用其实际位置（屏幕坐标）
          if (leftStickPlaceholderRef.current) {
            const placeholderRect = leftStickPlaceholderRef.current.getBoundingClientRect()
            fixedX = placeholderRect.left + placeholderRect.width / 2
            fixedY = placeholderRect.top + placeholderRect.height / 2
          }
          
          activeTouchIdRef.current.left = touch.identifier
          stickInitialPosRef.current.left = { x: touchX, y: touchY }
          stickFixedPosRef.current.left = { x: fixedX, y: fixedY }
          latestTouchPosRef.current.left = { x: touchX, y: touchY }
          setVirtualStickActive('left', true)
          setVirtualStick('left', 0, 0)
          leftStickDataRef.current = { x: 0, y: 0, displayX: 0, displayY: 0 }
          setLeftStickActive(true)
        } else if (isRightArea && isBottomMiddleVertical && activeTouchIdRef.current.right === null) {
          // 计算右摇杆的固定位置（占位符中心）
          let fixedX = viewportWidth * 0.75 // 右侧区域的中心位置（约75%）
          let fixedY = viewportHeight * 0.7 // 控制器区域底部30%位置
          
          // 如果占位符元素存在，使用其实际位置（屏幕坐标）
          if (rightStickPlaceholderRef.current) {
            const placeholderRect = rightStickPlaceholderRef.current.getBoundingClientRect()
            fixedX = placeholderRect.left + placeholderRect.width / 2
            fixedY = placeholderRect.top + placeholderRect.height / 2
          }
          
          activeTouchIdRef.current.right = touch.identifier
          stickInitialPosRef.current.right = { x: touchX, y: touchY }
          stickFixedPosRef.current.right = { x: fixedX, y: fixedY }
          latestTouchPosRef.current.right = { x: touchX, y: touchY }
          setVirtualStickActive('right', true)
          setVirtualStick('right', 0, 0)
          rightStickDataRef.current = { x: 0, y: 0, displayX: 0, displayY: 0 }
          setRightStickActive(true)
        }
      }
    },
    [isMobile, sessionId, setVirtualStick, setVirtualStickActive, showVirtualController, hasPhysicalGamepad]
  )

  const handleGlobalTouchMove = useCallback(
    (e: TouchEvent) => {
      if (!isMobile || !sessionId) return
      e.preventDefault()

      if (activeTouchIdRef.current.left !== null) {
        const touch = Array.from(e.touches).find((t) => t.identifier === activeTouchIdRef.current.left)
        if (touch) {
          latestTouchPosRef.current.left = { x: touch.clientX, y: touch.clientY }
          updateStick('left', touch)
        } else {
          latestTouchPosRef.current.left = null
        }
      }

      if (activeTouchIdRef.current.right !== null) {
        const touch = Array.from(e.touches).find((t) => t.identifier === activeTouchIdRef.current.right)
        if (touch) {
          latestTouchPosRef.current.right = { x: touch.clientX, y: touch.clientY }
          updateStick('right', touch)
        } else {
          latestTouchPosRef.current.right = null
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
          latestTouchPosRef.current.left = null
          stickInitialPosRef.current.left = null
          stickFixedPosRef.current.left = null
          setVirtualStickActive('left', false)
          setVirtualStick('left', 0, 0)
          leftStickDataRef.current = { x: 0, y: 0, displayX: 0, displayY: 0 }
          setLeftStickActive(false)
          if (leftStickInnerRef.current) {
            leftStickInnerRef.current.style.transform = `translate(0px, 0px)`
          }
        }
      }

      if (activeTouchIdRef.current.right !== null) {
        const touchStillActive = Array.from(e.touches).some(
          (t) => t.identifier === activeTouchIdRef.current.right
        )
        if (!touchStillActive) {
          activeTouchIdRef.current.right = null
          latestTouchPosRef.current.right = null
          stickInitialPosRef.current.right = null
          stickFixedPosRef.current.right = null
          setVirtualStickActive('right', false)
          setVirtualStick('right', 0, 0)
          rightStickDataRef.current = { x: 0, y: 0, displayX: 0, displayY: 0 }
          setRightStickActive(false)
          if (rightStickInnerRef.current) {
            rightStickInnerRef.current.style.transform = `translate(0px, 0px)`
          }
        }
      }
    },
    [isMobile, setVirtualStick, setVirtualStickActive]
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

      if (activeButtonTimeoutRef.current) {
        clearTimeout(activeButtonTimeoutRef.current)
        activeButtonTimeoutRef.current = null
      }

      vibrate(VIBRATION_PATTERNS[action])

      if (action === 'press') {
        setActiveButton(buttonName)
      } else if (action === 'tap') {
        setActiveButton(buttonName)
        activeButtonTimeoutRef.current = setTimeout(() => {
          setActiveButton(null)
        }, BUTTON_FEEDBACK.duration)
      } else if (action === 'release') {
        setActiveButton(null)
      }

      try {
        const streamingButtonName = getStreamingButtonName(buttonName as any)
        await controllerService.sendButton(streamingButtonName, action, action === 'tap' ? 50 : 0)
      } catch {}
    },
    [sessionId]
  )
  
  useEffect(() => {
    return () => {
      if (activeButtonTimeoutRef.current) {
        clearTimeout(activeButtonTimeoutRef.current)
      }
    }
  }, [])


  if (!isMobile || !isVisible) {
    return null
  }

  if (!showVirtualController && hasPhysicalGamepad) {
    return (
      <div className="fixed inset-0 pointer-events-none z-[100]" style={{ touchAction: 'none' }}>
      {/* 底部按键栏 - 直接显示 */}
      <div className="fixed bottom-0 left-0 right-0 z-[100] pointer-events-auto">
        <div className="px-4 py-1.5">
          <div className="flex items-center justify-between">
            {/* 左侧：返回按钮 */}
            <div className="flex items-center">
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
                  aria-label="返回"
                >
                  <ArrowLeft className="h-4 w-4" />
                </button>
              )}
            </div>

            {/* 中间：SHARE、PS、OPTIONS 按键 */}
            <div className="flex items-center gap-6">
              {BOTTOM_BUTTONS.map((config) => (
                <VirtualButton
                  key={config.name}
                  config={config}
                  isActive={activeButton === config.name}
                  onClick={handleButtonClick}
                />
              ))}
            </div>

            {/* 右侧：串流监控指标按钮 */}
            <div className="flex items-center">
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
                  aria-label={isStatsEnabled ? '关闭统计' : '显示统计'}
                >
                  <Activity className="h-4 w-4" />
                </button>
              )}
            </div>
          </div>
        </div>
      </div>
      </div>
    )
  }

  return (
    <div 
      className="fixed bottom-0 left-0 right-0 pointer-events-none z-[100] bg-black" 
      style={{ 
        touchAction: 'none',
        // 控制器自动靠底部，使用固定高度
        height: '500px',
        display: 'flex',
      }}
    >
      {/* 摇杆现在直接渲染在占位符内部，不再需要单独的VirtualJoystick组件 */}

      {/* 左侧按钮区域 - 使用相对定位，固定高度 */}
      <div className="relative pointer-events-auto" style={{ width: '50%', height: '500px', flexShrink: 0 }}>
        {LEFT_BUTTONS.map((config) => (
          <VirtualButton
            key={config.name}
            config={config}
            isActive={activeButton === config.name}
            onClick={handleButtonClick}
          />
        ))}

        <div
          className="absolute pointer-events-auto rounded-full border-2 border-white/20"
          style={{
            top: '160px',
            left: '30px',
            width: '130px',
            height: '130px',
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

        {/* 左摇杆占位符和摇杆 */}
        <div
          ref={leftStickPlaceholderRef}
          className="absolute pointer-events-none"
          style={{
            top: '300px',
            left: '80px',
            width: `${STICK_CONFIG.radius * 2}px`,
            height: `${STICK_CONFIG.radius * 2}px`,
          }}
        >
          {/* 占位符圆圈 */}
          <div className="w-full h-full rounded-full border-2 border-white/20" />
          {/* 摇杆显示在占位符上 */}
          {leftStickActive && (
            <div
              ref={leftStickContainerRef}
              className="absolute pointer-events-none"
              style={{
                top: 0,
                left: 0,
                width: `${STICK_CONFIG.radius * 2}px`,
                height: `${STICK_CONFIG.radius * 2}px`,
              }}
            >
              <div className="w-full h-full rounded-full bg-white/20 border-2 border-white/40 flex items-center justify-center">
                <div
                  ref={leftStickInnerRef}
                  className="w-10 h-10 rounded-full bg-white/60 border-2 border-white"
                  style={{
                    willChange: 'transform',
                    transform: `translate(0px, 0px)`,
                  }}
                />
              </div>
            </div>
          )}
        </div>
      </div>

      {/* 右侧按钮区域 - 使用相对定位，固定高度，靠右对齐 */}
      <div className="relative pointer-events-auto" style={{ width: '50%', height: '500px', flexShrink: 0, marginLeft: 'auto' }}>
        {RIGHT_BUTTONS.map((config) => (
          <VirtualButton
            key={config.name}
            config={config}
            isActive={activeButton === config.name}
            onClick={handleButtonClick}
          />
        ))}

        <div
          className="absolute pointer-events-auto rounded-full border-2 border-white/20"
          style={{
            width: '130px',
            height: '130px',
            top: '160px',
            right: '20px',
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

        {/* 右摇杆占位符和摇杆 */}
        <div
          ref={rightStickPlaceholderRef}
          className="absolute pointer-events-none"
          style={{
            top: '300px',
            right: '80px',
            width: `${STICK_CONFIG.radius * 2}px`,
            height: `${STICK_CONFIG.radius * 2}px`,
          }}
        >
          {/* 占位符圆圈 */}
          <div className="w-full h-full rounded-full border-2 border-white/20" />
          {/* 摇杆显示在占位符上 */}
          {rightStickActive && (
            <div
              ref={rightStickContainerRef}
              className="absolute pointer-events-none"
              style={{
                top: 0,
                left: 0,
                width: `${STICK_CONFIG.radius * 2}px`,
                height: `${STICK_CONFIG.radius * 2}px`,
              }}
            >
              <div className="w-full h-full rounded-full bg-white/20 border-2 border-white/40 flex items-center justify-center">
                <div
                  ref={rightStickInnerRef}
                  className="w-10 h-10 rounded-full bg-white/60 border-2 border-white"
                  style={{
                    willChange: 'transform',
                    transform: `translate(0px, 0px)`,
                  }}
                />
              </div>
            </div>
          )}
        </div>
      </div>

      {/* 底部按键栏 - 直接显示 */}
      <div className="fixed bottom-0 left-0 right-0 z-[100] pointer-events-auto">
        <div className="px-4 py-1.5">
          <div className="flex items-center justify-between">
            {/* 左侧：返回按钮 */}
            <div className="flex items-center">
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
                  aria-label="返回"
                >
                  <ArrowLeft className="h-4 w-4" />
                </button>
              )}
            </div>

            {/* 中间：SHARE、PS、OPTIONS 按键 */}
            <div className="flex items-center gap-6">
              {BOTTOM_BUTTONS.map((config) => (
                <VirtualButton
                  key={config.name}
                  config={config}
                  isActive={activeButton === config.name}
                  onClick={handleButtonClick}
                />
              ))}
            </div>

            {/* 右侧：串流监控指标按钮 */}
            <div className="flex items-center">
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
                  aria-label={isStatsEnabled ? '关闭统计' : '显示统计'}
                >
                  <Activity className="h-4 w-4" />
                </button>
              )}
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}

