export type ControllerButton =
  | 'CROSS'
  | 'CIRCLE'
  | 'TRIANGLE'
  | 'SQUARE'
  | 'L1'
  | 'R1'
  | 'L2'
  | 'R2'
  | 'L3'
  | 'R3'
  | 'DPAD_UP'
  | 'DPAD_DOWN'
  | 'DPAD_LEFT'
  | 'DPAD_RIGHT'
  | 'OPTIONS'
  | 'SHARE'
  | 'TOUCHPAD'
  | 'PS'

export interface ButtonMapping {
  button: ControllerButton
  key?: string
  mouseButton?: number
  isMouse?: boolean
}

export type StickDirection = 'UP' | 'DOWN' | 'LEFT' | 'RIGHT'

export type LeftStickMapping = Record<StickDirection, string | undefined>

export interface ControllerMappings {
  buttons: Record<ControllerButton, ButtonMapping>
  leftStick: LeftStickMapping
}

export const CONTROLLER_MAPPING_STORAGE_KEY = 'controller_mappings'
export const CONTROLLER_MAPPING_CHANGED_EVENT = 'controller-mapping-changed'

export const CONTROLLER_BUTTONS: ControllerButton[] = [
  'CROSS',
  'CIRCLE',
  'TRIANGLE',
  'SQUARE',
  'L1',
  'R1',
  'L2',
  'R2',
  'L3',
  'R3',
  'DPAD_UP',
  'DPAD_DOWN',
  'DPAD_LEFT',
  'DPAD_RIGHT',
  'OPTIONS',
  'SHARE',
  'TOUCHPAD',
  'PS',
]

export const LEFT_STICK_DIRECTIONS: StickDirection[] = ['UP', 'DOWN', 'LEFT', 'RIGHT']

const DEFAULT_BUTTON_MAPPINGS: Record<ControllerButton, ButtonMapping> = {
  CROSS: { button: 'CROSS', key: 'Enter' },
  CIRCLE: { button: 'CIRCLE', key: 'Escape' },
  TRIANGLE: { button: 'TRIANGLE', key: 'KeyY' },
  SQUARE: { button: 'SQUARE', key: 'KeyX' },
  L1: { button: 'L1', key: 'KeyQ' },
  R1: { button: 'R1', key: 'KeyE' },
  L2: { button: 'L2', key: 'KeyZ' },
  R2: { button: 'R2', key: 'KeyC' },
  L3: { button: 'L3', key: 'KeyF' },
  R3: { button: 'R3', key: 'KeyV' },
  DPAD_UP: { button: 'DPAD_UP', key: 'ArrowUp' },
  DPAD_DOWN: { button: 'DPAD_DOWN', key: 'ArrowDown' },
  DPAD_LEFT: { button: 'DPAD_LEFT', key: 'ArrowLeft' },
  DPAD_RIGHT: { button: 'DPAD_RIGHT', key: 'ArrowRight' },
  OPTIONS: { button: 'OPTIONS', key: 'KeyM' },
  SHARE: { button: 'SHARE', key: 'KeyN' },
  TOUCHPAD: { button: 'TOUCHPAD', key: 'KeyT' },
  PS: { button: 'PS', key: 'KeyP' },
}

type RawMapping = Partial<ButtonMapping> & { [key: string]: unknown }
type RawControllerMappings = Partial<ControllerMappings> & { [key: string]: unknown }

const DEFAULT_LEFT_STICK_MAPPING: LeftStickMapping = {
  UP: 'KeyW',
  DOWN: 'KeyS',
  LEFT: 'KeyA',
  RIGHT: 'KeyD',
}

const DPAD_STREAMING_NAME_MAP: Record<ControllerButton, string> = {
  DPAD_UP: 'UP',
  DPAD_DOWN: 'DOWN',
  DPAD_LEFT: 'LEFT',
  DPAD_RIGHT: 'RIGHT',
  CROSS: 'CROSS',
  CIRCLE: 'CIRCLE',
  TRIANGLE: 'TRIANGLE',
  SQUARE: 'SQUARE',
  L1: 'L1',
  R1: 'R1',
  L2: 'L2',
  R2: 'R2',
  L3: 'L3',
  R3: 'R3',
  OPTIONS: 'OPTIONS',
  SHARE: 'SHARE',
  TOUCHPAD: 'TOUCHPAD',
  PS: 'PS',
}

const DEFAULT_KEYBOARD_MAPPING: Record<string, string> = {
  ArrowUp: 'UP',
  ArrowDown: 'DOWN',
  ArrowLeft: 'LEFT',
  ArrowRight: 'RIGHT',
  Space: 'CROSS',
  Enter: 'CIRCLE',
  ShiftLeft: 'SQUARE',
  ShiftRight: 'SQUARE',
  ControlLeft: 'TRIANGLE',
  ControlRight: 'TRIANGLE',
  KeyQ: 'L1',
  KeyE: 'R1',
  KeyZ: 'L2',
  KeyC: 'R2',
  Tab: 'OPTIONS',
  Backspace: 'SHARE',
  Escape: 'PS',
}

const isBrowser = () => typeof window !== 'undefined'

const getDefaultButtonMappings = (): Record<ControllerButton, ButtonMapping> =>
  CONTROLLER_BUTTONS.reduce<Record<ControllerButton, ButtonMapping>>((acc, button) => {
    const defaultMapping = DEFAULT_BUTTON_MAPPINGS[button]
    acc[button] = defaultMapping ? { ...defaultMapping } : { button }
    return acc
  }, {} as Record<ControllerButton, ButtonMapping>)

export const getDefaultLeftStickMapping = (): LeftStickMapping => ({
  ...DEFAULT_LEFT_STICK_MAPPING,
})

export const getStreamingButtonName = (button: ControllerButton): string =>
  DPAD_STREAMING_NAME_MAP[button] ?? button

export const getDefaultControllerMappings = (): ControllerMappings => ({
  buttons: getDefaultButtonMappings(),
  leftStick: getDefaultLeftStickMapping(),
})

const sanitizeButtonMappings = (
  input: Record<string, RawMapping> | null | undefined
): Record<ControllerButton, ButtonMapping> => {
  const result = getDefaultButtonMappings()

  if (!input || typeof input !== 'object') {
    return result
  }

  CONTROLLER_BUTTONS.forEach((button) => {
    const raw = input[button] as RawMapping | undefined
    if (!raw || typeof raw !== 'object') {
      return
    }

    const hasKeyProp = Object.prototype.hasOwnProperty.call(raw, 'key')
    const hasMouseProp = Object.prototype.hasOwnProperty.call(raw, 'mouseButton')

    const mapping: ButtonMapping = { button }

    if (hasKeyProp && typeof raw.key === 'string') {
      mapping.key = raw.key
    }

    if (hasMouseProp) {
      const mouseButton =
        typeof raw.mouseButton === 'number' && Number.isFinite(raw.mouseButton)
          ? raw.mouseButton
          : undefined
      if (mouseButton !== undefined) {
        mapping.mouseButton = mouseButton
        mapping.isMouse = Boolean(raw.isMouse)
      }
    }

    if (!hasKeyProp && !hasMouseProp) {
      result[button] = { button }
      return
    }

    if (mapping.key === undefined && mapping.mouseButton === undefined) {
      result[button] = { button }
      return
    }

    if (!mapping.mouseButton) {
      delete mapping.isMouse
    }

    result[button] = mapping
  })

  return result
}

const sanitizeLeftStickMapping = (input: unknown): LeftStickMapping => {
  const defaults = getDefaultLeftStickMapping()

  if (!input || typeof input !== 'object') {
    return defaults
  }

  const raw = input as Record<string, unknown>
  const result: LeftStickMapping = { ...defaults }

  LEFT_STICK_DIRECTIONS.forEach((direction) => {
    const value = raw[direction]
    if (typeof value === 'string' && value.trim().length > 0) {
      result[direction] = value
    } else {
      result[direction] = undefined
    }
  })

  return result
}

const sanitizeMappings = (
  input: unknown
): ControllerMappings => {
  const defaults = getDefaultControllerMappings()

  if (!input || typeof input !== 'object') {
    return defaults
  }

  const raw = input as RawControllerMappings

  // 新格式
  if (raw.buttons || raw.leftStick) {
    return {
      buttons: sanitizeButtonMappings(raw.buttons as Record<string, RawMapping>),
      leftStick: sanitizeLeftStickMapping(raw.leftStick),
    }
  }

  // 兼容旧格式（仅包含按钮映射）
  return {
    buttons: sanitizeButtonMappings(input as Record<string, RawMapping>),
    leftStick: getDefaultLeftStickMapping(),
  }
}

export const loadControllerMappings = (): ControllerMappings => {
  if (!isBrowser()) {
    return getDefaultControllerMappings()
  }

  const raw = window.localStorage.getItem(CONTROLLER_MAPPING_STORAGE_KEY)
  if (!raw) {
    return getDefaultControllerMappings()
  }

  try {
    const parsed = JSON.parse(raw) as RawControllerMappings
    return sanitizeMappings(parsed)
  } catch (error) {
    console.error('Failed to parse controller mappings:', error)
    return getDefaultControllerMappings()
  }
}

export const getSavedControllerMappings = (): ControllerMappings | null => {
  if (!isBrowser()) {
    return null
  }

  const raw = window.localStorage.getItem(CONTROLLER_MAPPING_STORAGE_KEY)
  if (!raw) {
    return null
  }

  try {
    const parsed = JSON.parse(raw) as RawControllerMappings
    return sanitizeMappings(parsed)
  } catch (error) {
    console.error('Failed to parse controller mappings:', error)
    return null
  }
}

export const saveControllerMappings = (mappings: ControllerMappings): void => {
  if (!isBrowser()) {
    return
  }

  const sanitized = sanitizeMappings(mappings)
  try {
    window.localStorage.setItem(
      CONTROLLER_MAPPING_STORAGE_KEY,
      JSON.stringify(sanitized)
    )
    window.dispatchEvent(
      new CustomEvent<ControllerMappings>(
        CONTROLLER_MAPPING_CHANGED_EVENT,
        {
          detail: sanitized,
        }
      )
    )
  } catch (error) {
    console.error('Failed to save controller mappings:', error)
  }
}

export const subscribeControllerMappings = (
  callback: (mappings: ControllerMappings) => void
): (() => void) => {
  if (!isBrowser()) {
    return () => {}
  }

  const handler: EventListener = (event) => {
    const customEvent = event as CustomEvent<ControllerMappings | undefined>
    const detail = customEvent.detail
    if (detail) {
      callback(detail)
    } else {
      callback(loadControllerMappings())
    }
  }

  window.addEventListener(CONTROLLER_MAPPING_CHANGED_EVENT, handler)
  return () => {
    window.removeEventListener(CONTROLLER_MAPPING_CHANGED_EVENT, handler)
  }
}

const applyOverride = (
  mapping: Record<string, string>,
  button: ControllerButton,
  key?: string
) => {
  const streamingName = getStreamingButtonName(button)

  Object.keys(mapping).forEach((existingKey) => {
    if (mapping[existingKey] === streamingName) {
      delete mapping[existingKey]
    }
  })

  if (key) {
    mapping[key] = streamingName
  }
}

export const getKeyboardToButtonMapping = (
  overrides?: ControllerMappings | null
): Record<string, string> => {
  const mapping = { ...DEFAULT_KEYBOARD_MAPPING }
  const saved = overrides ?? getSavedControllerMappings()

  if (!saved) {
    return mapping
  }

  for (const button of CONTROLLER_BUTTONS) {
    const buttonMapping = saved.buttons[button]
    if (!buttonMapping) {
      continue
    }

    if (buttonMapping.isMouse) {
      applyOverride(mapping, button, undefined)
      continue
    }

    applyOverride(mapping, button, buttonMapping.key)
  }

  return mapping
}

const LEFT_STICK_DIRECTION_VECTORS: Record<StickDirection, { x: number; y: number }> = {
  UP: { x: 0, y: -1 },
  DOWN: { x: 0, y: 1 },
  LEFT: { x: -1, y: 0 },
  RIGHT: { x: 1, y: 0 },
}

export const buildLeftStickKeyVectorMap = (leftStick: LeftStickMapping): Record<string, { x: number; y: number }> => {
  const map: Record<string, { x: number; y: number }> = {}

  LEFT_STICK_DIRECTIONS.forEach((direction) => {
    const key = leftStick[direction]
    if (key) {
      map[key] = LEFT_STICK_DIRECTION_VECTORS[direction]
    }
  })

  return map
}

export const getLeftStickKeyVectorMap = (overrides?: ControllerMappings | null): Record<string, { x: number; y: number }> => {
  const source = overrides ?? getSavedControllerMappings()
  if (!source) {
    return buildLeftStickKeyVectorMap(getDefaultLeftStickMapping())
  }
  return buildLeftStickKeyVectorMap(source.leftStick)
}
 