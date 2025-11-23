import { useTranslation } from 'react-i18next'
import { useRef, useEffect, useState } from 'react'
import {
  type ControllerButton,
  type ButtonMapping,
  type LeftStickMapping,
  type StickDirection,
  LEFT_STICK_DIRECTIONS,
} from '@/types/controller-mapping'
import controllerSvgUrl from '@/assets/ps5-controller.svg'
import { useDevice } from '@/hooks/use-device'

interface PS5ControllerLayoutProps {
  mappings: Record<ControllerButton, ButtonMapping>
  leftStickMapping: LeftStickMapping
  onButtonClick: (button: ControllerButton) => void
  onLeftStickDirectionClick: (direction: StickDirection) => void
  onLeftStickDirectionClear: (direction: StickDirection) => void
  isListening: ControllerButton | null
  leftStickListening: StickDirection | null
}

interface ButtonPosition {
  x: number
  y: number
}

/**
 * 精准按钮坐标（基于你提供的 500x500 SVG）
 * 单位: SVG 坐标点 (x, y)
 */
const BUTTON_POSITIONS: Record<ControllerButton, ButtonPosition> = {
  // 左肩键
  L2: { x: 140, y: 130 },
  L1: { x: 140, y: 135 },
  // 左方向键
  DPAD_UP: { x: 135, y: 180 },
  DPAD_LEFT: { x: 115, y: 200 },
  DPAD_DOWN: { x: 135, y: 220 },
  DPAD_RIGHT: { x: 155, y: 200 },
  // 分享键 + 左摇杆
  SHARE: { x: 160, y: 155 },
  L3: { x: 190, y: 255 },
  // 触摸板
  TOUCHPAD: { x: 250, y: 180 },
  // 右肩键
  R2: { x: 375, y: 130 },
  R1: { x: 375, y: 135 },
  // 右侧按钮组
  TRIANGLE: { x: 370, y: 170 },
  CIRCLE: { x: 390, y: 200 },
  CROSS: { x: 365, y: 225 },
  SQUARE: { x: 340, y: 205 },
  // 选项键 + 右摇杆
  OPTIONS: { x: 342, y: 155 },
  R3: { x: 310, y: 255 },
  PS: { x: 260, y: 255 },
}

const LEFT_BUTTONS: ControllerButton[] = [
  'L2',
  'L1',
  'SHARE',
  'DPAD_UP',
  'DPAD_LEFT',
  'DPAD_RIGHT',
  'DPAD_DOWN',
  'TOUCHPAD',
  'L3',
]

const RIGHT_BUTTONS: ControllerButton[] = [
  'R2',
  'R1',
  'OPTIONS',
  'TRIANGLE',
  'CIRCLE',
  'SQUARE',
  'CROSS',
  'R3',
  'PS',
]

const LABEL_LIST_START_Y = 80
const LABEL_ITEM_HEIGHT = 40

export function PS5ControllerLayout({
  mappings,
  leftStickMapping,
  onButtonClick,
  onLeftStickDirectionClick,
  onLeftStickDirectionClear,
  isListening,
  leftStickListening,
}: PS5ControllerLayoutProps) {
  const { t, i18n } = useTranslation()
  const { isMobile, isTablet } = useDevice()
  const containerRef = useRef<HTMLDivElement>(null)
  const leftLabelRef = useRef<HTMLDivElement>(null)
  const controllerRef = useRef<HTMLDivElement>(null)
  const rightLabelRef = useRef<HTMLDivElement>(null)
  const [layoutPositions, setLayoutPositions] = useState<{
    leftLabelRight: number
    controllerLeft: number
    controllerTop: number
    controllerWidth: number
    controllerHeight: number
    rightLabelLeft: number
    leftLabelTop: number
    rightLabelTop: number
  } | null>(null)

  useEffect(() => {
    const updateLayout = () => {
      if (containerRef.current && leftLabelRef.current && controllerRef.current && rightLabelRef.current) {
        const containerRect = containerRef.current.getBoundingClientRect()
        const leftLabelRect = leftLabelRef.current.getBoundingClientRect()
        const controllerRect = controllerRef.current.getBoundingClientRect()
        const rightLabelRect = rightLabelRef.current.getBoundingClientRect()
        setLayoutPositions({
          leftLabelRight: leftLabelRect.right - containerRect.left,
          controllerLeft: controllerRect.left - containerRect.left,
          controllerTop: controllerRect.top - containerRect.top,
          controllerWidth: controllerRect.width,
          controllerHeight: controllerRect.height,
          rightLabelLeft: rightLabelRect.left - containerRect.left,
          leftLabelTop: leftLabelRect.top - containerRect.top,
          rightLabelTop: rightLabelRect.top - containerRect.top,
        })
      }
    }

    const timer = setTimeout(updateLayout, 100)
    window.addEventListener('resize', updateLayout)
    return () => {
      clearTimeout(timer)
      window.removeEventListener('resize', updateLayout)
    }
  }, [])

  const getButtonName = (button: ControllerButton): string => {
    const translationKey = `devices.controllerMapping.buttons.${button}`
    try {
      const translated = t(translationKey)
      // 如果翻译返回的是键名本身（说明翻译失败），使用回退值
      if (translated === translationKey) {
        // 回退值
        const currentLang = i18n.language || 'zh-CN'
        const isZh = currentLang.startsWith('zh')
        const fallbackNames: Record<ControllerButton, string> = {
          CROSS: isZh ? 'X / 确认' : 'X / Confirm',
          CIRCLE: isZh ? 'O / 取消' : 'O / Cancel',
          TRIANGLE: isZh ? '△ / 菜单' : '△ / Menu',
          SQUARE: isZh ? '□ / 选项' : '□ / Option',
          L1: 'L1',
          R1: 'R1',
          L2: 'L2',
          R2: 'R2',
          L3: isZh ? 'L3（左摇杆按下）' : 'L3 (Left Stick Press)',
          R3: isZh ? 'R3（右摇杆按下）' : 'R3 (Right Stick Press)',
          DPAD_UP: isZh ? '方向键 上' : 'D-Pad Up',
          DPAD_DOWN: isZh ? '方向键 下' : 'D-Pad Down',
          DPAD_LEFT: isZh ? '方向键 左' : 'D-Pad Left',
          DPAD_RIGHT: isZh ? '方向键 右' : 'D-Pad Right',
          OPTIONS: isZh ? '选项键' : 'Options',
          SHARE: isZh ? '分享键' : 'Share',
          TOUCHPAD: isZh ? '触摸板' : 'Touchpad',
          PS: isZh ? 'PS键' : 'PS Button',
        }
        return fallbackNames[button] || button
      }
      return translated
    } catch {
      // 错误回退
      const currentLang = i18n.language || 'zh-CN'
      const isZh = currentLang.startsWith('zh')
      const fallbackNames: Record<ControllerButton, string> = {
        CROSS: isZh ? 'X / 确认' : 'X / Confirm',
        CIRCLE: isZh ? 'O / 取消' : 'O / Cancel',
        TRIANGLE: isZh ? '△ / 菜单' : '△ / Menu',
        SQUARE: isZh ? '□ / 选项' : '□ / Option',
        L1: 'L1',
        R1: 'R1',
        L2: 'L2',
        R2: 'R2',
        L3: isZh ? 'L3（左摇杆按下）' : 'L3 (Left Stick Press)',
        R3: isZh ? 'R3（右摇杆按下）' : 'R3 (Right Stick Press)',
        DPAD_UP: isZh ? '方向键 上' : 'D-Pad Up',
        DPAD_DOWN: isZh ? '方向键 下' : 'D-Pad Down',
        DPAD_LEFT: isZh ? '方向键 左' : 'D-Pad Left',
        DPAD_RIGHT: isZh ? '方向键 右' : 'D-Pad Right',
        OPTIONS: isZh ? '选项键' : 'Options',
        SHARE: isZh ? '分享键' : 'Share',
        TOUCHPAD: isZh ? '触摸板' : 'Touchpad',
        PS: isZh ? 'PS键' : 'PS Button',
      }
      return fallbackNames[button] || button
    }
  }

  const getMappingDisplay = (mapping: ButtonMapping | undefined): string => {
    if (!mapping) return ''
    
    // 处理鼠标按钮
    if (mapping.isMouse && mapping.mouseButton !== undefined) {
      const mouseKey = `devices.controllerMapping.mouseButtons.${mapping.mouseButton}`
      try {
        const translated = t(mouseKey)
        return translated !== mouseKey ? translated : ''
      } catch {
        // 回退值
        const mouseLabels: Record<number, string> = {
          0: i18n.language?.startsWith('zh') ? '鼠标左键' : 'Left Mouse',
          1: i18n.language?.startsWith('zh') ? '鼠标中键' : 'Middle Mouse',
          2: i18n.language?.startsWith('zh') ? '鼠标右键' : 'Right Mouse',
        }
        return mouseLabels[mapping.mouseButton] || ''
      }
    }
    
    // 处理键盘按键
    if (mapping.key) {
      const keyMap: Record<string, string> = {
        Enter: 'Enter',
        Escape: 'Esc',
        Space: 'Space',
        ArrowUp: '↑',
        ArrowDown: '↓',
        ArrowLeft: '←',
        ArrowRight: '→',
        KeyZ: 'Z',
        KeyQ: 'Q',
        KeyC: 'C',
        KeyE: 'E',
        KeyX: 'X',
        KeyY: 'Y',
        KeyV: 'V',
        KeyN: 'N',
        KeyM: 'M',
        KeyF: 'F',
      }
      return keyMap[mapping.key] || mapping.key.replace(/^Key/, '')
    }
    
    return ''
  }

  const formatKeyLabel = (key?: string): string => {
    if (!key) return ''
    const keyMap: Record<string, string> = {
      Enter: 'Enter',
      Escape: 'Esc',
      Space: 'Space',
      ArrowUp: '↑',
      ArrowDown: '↓',
      ArrowLeft: '←',
      ArrowRight: '→',
    }
    if (keyMap[key]) {
      return keyMap[key]
    }
    if (key.startsWith('Key')) {
      return key.slice(3)
    }
    return key
  }

  const getLeftStickDirectionLabel = (direction: StickDirection): string =>
    t(`devices.controllerMapping.leftStick.directions.${direction}`)

  const getLeftStickDisplay = (direction: StickDirection): string => {
    const keyCode = leftStickMapping[direction]
    if (!keyCode) {
      return t('devices.controllerMapping.notMapped')
    }
    return formatKeyLabel(keyCode)
  }

  const getLabelColor = (button: ControllerButton): string => {
    if (isListening === button) return '#2563eb' // blue-600
    const mapping = mappings[button]
    if (mapping && (mapping.key || mapping.mouseButton !== undefined)) return '#2563eb' // blue-600
    return '#9ca3af' // gray-400
  }

  // 根据设备类型调整尺寸
  const controllerWidth = isMobile ? 300 : isTablet ? 400 : 500
  const controllerHeight = isMobile ? 180 : isTablet ? 240 : 300
  const labelColumnWidth = isMobile ? 100 : isTablet ? 120 : 140
  const maxColumnLength = Math.max(LEFT_BUTTONS.length, RIGHT_BUTTONS.length)
  const totalHeight = LABEL_LIST_START_Y + maxColumnLength * LABEL_ITEM_HEIGHT

  return (
    <div className="w-full max-w-6xl mx-auto relative rounded-lg p-2 sm:p-4 md:p-6">
      <div
        ref={containerRef}
        className={`relative flex ${isMobile ? 'flex-col gap-4' : 'gap-4 md:gap-8'} items-start justify-center`}
        style={{ minHeight: `${Math.max(controllerHeight, totalHeight)}px` }}
      >
        {layoutPositions && (
          <svg className="absolute inset-0 w-full h-full pointer-events-none" style={{ zIndex: 1 }}>
            {/* 左侧连线 */}
            {LEFT_BUTTONS.map((button, index) => {
              const bp = BUTTON_POSITIONS[button]
              const bx = layoutPositions.controllerLeft + (bp.x / 500) * layoutPositions.controllerWidth
              const by = layoutPositions.controllerTop + (bp.y / 500) * layoutPositions.controllerHeight
              const lx = layoutPositions.leftLabelRight - 8
              const ly = layoutPositions.leftLabelTop + LABEL_LIST_START_Y + index * LABEL_ITEM_HEIGHT + LABEL_ITEM_HEIGHT / 2
              return (
                <line
                  key={button}
                  x1={bx}
                  y1={by}
                  x2={lx}
                  y2={ly}
                  stroke="#2563eb"
                  strokeWidth="1.5"
                  strokeDasharray="3,3"
                  opacity="0.6"
                />
              )
            })}
            {/* 右侧连线 */}
            {RIGHT_BUTTONS.map((button, index) => {
              const bp = BUTTON_POSITIONS[button]
              const bx = layoutPositions.controllerLeft + (bp.x / 500) * layoutPositions.controllerWidth
              const by = layoutPositions.controllerTop + (bp.y / 500) * layoutPositions.controllerHeight
              const rx = layoutPositions.rightLabelLeft + 8
              const ry = layoutPositions.rightLabelTop + LABEL_LIST_START_Y + index * LABEL_ITEM_HEIGHT + LABEL_ITEM_HEIGHT / 2
              return (
                <line
                  key={button}
                  x1={bx}
                  y1={by}
                  x2={rx}
                  y2={ry}
                  stroke="#2563eb"
                  strokeWidth="1.5"
                  strokeDasharray="3,3"
                  opacity="0.6"
                />
              )
            })}
          </svg>
        )}

        {/* 左侧标签 */}
        {!isMobile && (
          <div ref={leftLabelRef} className="flex-1 flex justify-end" style={{ zIndex: 2 }}>
            <div className="min-w-0" style={{ width: `${labelColumnWidth}px`, paddingTop: `${LABEL_LIST_START_Y}px` }}>
              {LEFT_BUTTONS.map((button) => {
                const mapping = mappings[button]
                const display = getMappingDisplay(mapping)
                const color = getLabelColor(button)
                return (
                  <div
                    key={button}
                    className="flex items-center justify-end gap-2 px-2 py-1.5 cursor-pointer hover:bg-gray-100 dark:hover:bg-gray-800 rounded transition-colors min-w-0"
                    style={{ height: `${LABEL_ITEM_HEIGHT}px` }}
                    onClick={() => onButtonClick(button)}
                  >
                    {display && <span className="text-base font-bold text-blue-600 dark:text-blue-400 flex-shrink-0">{display}</span>}
                    <span className="text-sm font-medium truncate" style={{ color }}>
                      {getButtonName(button)}
                    </span>
                  </div>
                )
              })}
            </div>
          </div>
        )}

        {/* 中间手柄 */}
        <div ref={controllerRef} className="flex-shrink-0 relative" style={{ width: `${controllerWidth}px`, zIndex: 2 }}>
          <img 
            src={controllerSvgUrl} 
            alt="PS5 Controller" 
            className="w-full h-auto"
            style={{
              filter: 'brightness(0) saturate(100%) invert(27%) sepia(96%) saturate(2000%) hue-rotate(200deg) brightness(0.9) contrast(1.2)',
            }}
          />
        </div>

        {/* 右侧标签 */}
        {!isMobile && (
          <div ref={rightLabelRef} className="flex-1 flex justify-start" style={{ zIndex: 2 }}>
            <div className="min-w-0" style={{ width: `${labelColumnWidth}px`, paddingTop: `${LABEL_LIST_START_Y}px` }}>
              {RIGHT_BUTTONS.map((button) => {
                const mapping = mappings[button]
                const display = getMappingDisplay(mapping)
                const color = getLabelColor(button)
                return (
                  <div
                    key={button}
                    className="flex items-center gap-2 px-2 py-1.5 cursor-pointer hover:bg-gray-100 dark:hover:bg-gray-800 rounded transition-colors min-w-0"
                    style={{ height: `${LABEL_ITEM_HEIGHT}px` }}
                    onClick={() => onButtonClick(button)}
                  >
                    <span className="text-sm font-medium truncate" style={{ color }}>
                      {getButtonName(button)}
                    </span>
                    {display && <span className="text-base font-bold text-blue-600 dark:text-blue-400 flex-shrink-0">{display}</span>}
                  </div>
                )
              })}
            </div>
          </div>
        )}
      </div>

      <div className={`mt-4 sm:mt-6 md:mt-8 grid grid-cols-1 ${isMobile ? '' : 'lg:grid-cols-2'} gap-3 sm:gap-4`} style={{ zIndex: 3 }}>
        <div className="rounded-lg border border-blue-100 dark:border-blue-900/40 bg-white/70 dark:bg-slate-900/60 p-3 sm:p-4 shadow-sm">
          <div className="flex flex-col">
            <h4 className="text-xs sm:text-sm font-semibold text-gray-900 dark:text-white">
              {t('devices.controllerMapping.leftStick.title')}
            </h4>
            <p className="mt-1 text-xs text-gray-500 dark:text-gray-400">
              {t('devices.controllerMapping.leftStick.description')}
            </p>
          </div>

          <div className="mt-3 sm:mt-4 grid grid-cols-2 gap-2 sm:gap-3">
            {LEFT_STICK_DIRECTIONS.map((direction) => {
              const isActive = leftStickListening === direction
              const hasMapping = Boolean(leftStickMapping[direction])
              const display = getLeftStickDisplay(direction)

              return (
                <div
                  key={direction}
                  className={`rounded-lg border px-3 py-2 cursor-pointer transition-all ${
                    isActive
                      ? 'border-blue-500 dark:border-blue-400 bg-blue-50 dark:bg-blue-900/30 shadow-sm'
                      : hasMapping
                      ? 'border-blue-200/70 dark:border-blue-700/50 bg-white dark:bg-slate-900 hover:border-blue-400 dark:hover:border-blue-500'
                      : 'border-gray-200 dark:border-gray-700 bg-white dark:bg-slate-900 hover:border-blue-300/70 dark:hover:border-blue-500/50'
                  }`}
                  onClick={() => onLeftStickDirectionClick(direction)}
                >
                  <div className="flex items-center justify-between gap-3">
                    <span className="text-sm font-medium text-gray-700 dark:text-gray-300">
                      {getLeftStickDirectionLabel(direction)}
                    </span>
                    <div className="flex items-center gap-2">
                      <span
                        className={`text-base font-semibold ${
                          isActive
                            ? 'text-blue-600 dark:text-blue-400'
                            : hasMapping
                            ? 'text-blue-600 dark:text-blue-400'
                            : 'text-gray-400 dark:text-gray-500'
                        }`}
                      >
                        {isActive ? t('devices.controllerMapping.listening') : display}
                      </span>
                      {hasMapping && (
                        <button
                          type="button"
                          className="text-xs text-gray-400 hover:text-red-500 dark:hover:text-red-400"
                          onClick={(event) => {
                            event.stopPropagation()
                            onLeftStickDirectionClear(direction)
                          }}
                        >
                          {t('devices.controllerMapping.leftStick.clear')}
                        </button>
                      )}
                    </div>
                  </div>
                  <div className="mt-2 text-xs text-gray-500 dark:text-gray-400">
                    {t('devices.controllerMapping.leftStick.mapHint')}
                  </div>
                </div>
              )
            })}
          </div>
        </div>

        <div className="rounded-lg border border-gray-200 dark:border-gray-800 bg-white/70 dark:bg-slate-900/60 p-3 sm:p-4 shadow-sm">
          <h4 className="text-xs sm:text-sm font-semibold text-gray-900 dark:text-white">
            {t('devices.controllerMapping.rightStick.title')}
          </h4>
          <p className="mt-1 text-xs text-gray-500 dark:text-gray-400">
            {t('devices.controllerMapping.rightStick.description')}
          </p>

          <ul className="mt-3 sm:mt-4 space-y-2 text-xs sm:text-sm text-gray-700 dark:text-gray-300">
            <li className="flex items-center justify-between rounded-md bg-gray-100/70 dark:bg-slate-800/60 px-3 py-2">
              <span>{t('devices.controllerMapping.rightStick.axisX')}</span>
              <span className="font-semibold text-blue-600 dark:text-blue-400">
                {t('devices.controllerMapping.rightStick.axisXValue')}
              </span>
            </li>
            <li className="flex items-center justify-between rounded-md bg-gray-100/70 dark:bg-slate-800/60 px-3 py-2">
              <span>{t('devices.controllerMapping.rightStick.axisY')}</span>
              <span className="font-semibold text-blue-600 dark:text-blue-400">
                {t('devices.controllerMapping.rightStick.axisYValue')}
              </span>
            </li>
          </ul>
        </div>
      </div>
    </div>
  )
}
