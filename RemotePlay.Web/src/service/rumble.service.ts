import { ControllerRumbleEvent } from '@/types/controller'

export interface RumbleSettings {
  enabled: boolean
  strength: number // 0 ~ 1
}

const DEFAULT_SETTINGS: RumbleSettings = {
  enabled: true,
  strength: 1,
}

const RUMBLE_ENABLED_KEY = 'controller_rumble_enabled'
const RUMBLE_STRENGTH_KEY = 'controller_rumble_strength'

type SettingsListener = (settings: RumbleSettings) => void

let currentSettings: RumbleSettings = readSettingsFromStorage()
const listeners = new Set<SettingsListener>()

function isBrowser(): boolean {
  return typeof window !== 'undefined' && typeof navigator !== 'undefined'
}

function clamp01(value: number): number {
  if (!Number.isFinite(value)) {
    return 0
  }
  if (value <= 0) return 0
  if (value >= 1) return 1
  return value
}

function readSettingsFromStorage(): RumbleSettings {
  if (!isBrowser()) {
    return { ...DEFAULT_SETTINGS }
  }

  const enabledRaw = window.localStorage.getItem(RUMBLE_ENABLED_KEY)
  const strengthRaw = window.localStorage.getItem(RUMBLE_STRENGTH_KEY)

  const enabled = enabledRaw === null ? DEFAULT_SETTINGS.enabled : enabledRaw !== 'false'
  const strengthValue =
    strengthRaw === null ? DEFAULT_SETTINGS.strength : clamp01(parseFloat(strengthRaw))

  return {
    enabled,
    strength: strengthValue,
  }
}

function persistSettings(settings: RumbleSettings) {
  if (!isBrowser()) {
    return
  }

  window.localStorage.setItem(RUMBLE_ENABLED_KEY, settings.enabled ? 'true' : 'false')
  window.localStorage.setItem(RUMBLE_STRENGTH_KEY, settings.strength.toString())
}

function notifyListeners() {
  listeners.forEach((listener) => {
    try {
      listener({ ...currentSettings })
    } catch (error) {
      console.warn('[rumble-service] listener error:', error)
    }
  })
}

export function getRumbleSettings(): RumbleSettings {
  return { ...currentSettings }
}

export function setRumbleSettings(settings: RumbleSettings) {
  currentSettings = {
    enabled: settings.enabled,
    strength: clamp01(settings.strength),
  }
  persistSettings(currentSettings)
  notifyListeners()
}

export function updateRumbleSettings(partial: Partial<RumbleSettings>) {
  setRumbleSettings({
    ...currentSettings,
    ...partial,
  })
}

export function onRumbleSettingsChange(listener: SettingsListener): () => void {
  listeners.add(listener)
  listener({ ...currentSettings })
  return () => {
    listeners.delete(listener)
  }
}

if (isBrowser()) {
  window.addEventListener('storage', (event) => {
    if (event.key && event.key !== RUMBLE_ENABLED_KEY && event.key !== RUMBLE_STRENGTH_KEY) {
      return
    }
    currentSettings = readSettingsFromStorage()
    notifyListeners()
  })
}

function playWithActuator(
  actuator: GamepadHapticActuator | undefined,
  strongMagnitude: number,
  weakMagnitude: number,
  duration: number
): boolean {
  if (!actuator) {
    return false
  }

  try {
    if (typeof actuator.playEffect === 'function') {
      void actuator.playEffect('dual-rumble', {
        startDelay: 0,
        duration,
        strongMagnitude,
        weakMagnitude,
      })
      return true
    }
    const pulse = (actuator as { pulse?: (value: number, duration: number) => Promise<unknown> | unknown }).pulse
    if (typeof pulse === 'function') {
      const average = Math.max(strongMagnitude, weakMagnitude)
      void pulse.call(actuator, average, duration)
      return true
    }
  } catch (error) {
    console.warn('[rumble-service] play actuator failed:', error)
  }

  return false
}

function stopActuator(actuator: GamepadHapticActuator | undefined): boolean {
  if (!actuator) {
    return false
  }

  try {
    if (typeof actuator.playEffect === 'function') {
      void actuator.playEffect('dual-rumble', {
        duration: 1,
        strongMagnitude: 0,
        weakMagnitude: 0,
      })
      return true
    }
    const pulse = (actuator as { pulse?: (value: number, duration: number) => Promise<unknown> | unknown }).pulse
    if (typeof pulse === 'function') {
      void pulse.call(actuator, 0, 1)
      return true
    }
  } catch (error) {
    console.warn('[rumble-service] stop actuator failed:', error)
  }

  return false
}

function hasUsableActuator(actuator: GamepadHapticActuator | undefined): boolean {
  if (!actuator) {
    return false
  }
  if (typeof actuator.playEffect === 'function') {
    return true
  }
  const pulse = (actuator as { pulse?: (value: number, duration: number) => Promise<unknown> | unknown }).pulse
  return typeof pulse === 'function'
}

function getGamepads(): (Gamepad | null)[] {
  if (!isBrowser()) {
    return []
  }

  const getter = (navigator as Navigator & { getGamepads?: () => Gamepad[] | (Gamepad | null)[] }).getGamepads
  if (typeof getter !== 'function') {
    return []
  }

  const pads = getter.call(navigator) ?? []
  return Array.from(pads as ArrayLike<Gamepad | null>, (pad) => pad ?? null)
}

function getHapticActuators(pad: Gamepad): readonly GamepadHapticActuator[] {
  const anyPad = pad as Gamepad & { hapticActuators?: readonly GamepadHapticActuator[] }
  return anyPad.hapticActuators ?? []
}

export function hasRumbleCapableGamepad(): boolean {
  return getGamepads().some((pad) => {
    if (!pad) return false
    const vibrationActuator = (pad as Gamepad & { vibrationActuator?: GamepadHapticActuator }).vibrationActuator
    if (hasUsableActuator(vibrationActuator)) {
      return true
    }
    const firstActuator = getHapticActuators(pad)[0]
    return hasUsableActuator(firstActuator)
  })
}

export interface ApplyRumbleOptions {
  duration?: number
  gamepadIndex?: number
  settings?: RumbleSettings
}

export function applyControllerRumbleToGamepads(
  event: ControllerRumbleEvent,
  options: ApplyRumbleOptions = {}
): boolean {
  const settings = options.settings ?? currentSettings
  const { enabled, strength } = settings
  const duration = options.duration ?? 80

  const leftValue = clamp01((event.left ?? event.rawLeft) / 255)
  const rightValue = clamp01((event.right ?? event.rawRight) / 255)

  const magnitudeScale = clamp01(strength)
  const strongMagnitude = clamp01(leftValue * magnitudeScale)
  const weakMagnitude = clamp01(rightValue * magnitudeScale)

  const gamepads = getGamepads()

  if (!enabled || gamepads.length === 0) {
    return false
  }

  let applied = false
  const shouldStop = strongMagnitude === 0 && weakMagnitude === 0

  for (const pad of gamepads) {
    if (!pad) continue
    if (typeof options.gamepadIndex === 'number' && pad.index !== options.gamepadIndex) {
      continue
    }

    const primaryActuator = (pad as Gamepad & { vibrationActuator?: GamepadHapticActuator }).vibrationActuator
    const fallbackActuator = getHapticActuators(pad)[0]
    const targetActuator = primaryActuator || fallbackActuator
    if (!targetActuator) {
      continue
    }

    const ok = shouldStop
      ? stopActuator(targetActuator)
      : playWithActuator(targetActuator, strongMagnitude, weakMagnitude, duration)

    applied = applied || ok

    if (typeof options.gamepadIndex === 'number') {
      break
    }
  }

  if (!applied && !shouldStop && typeof navigator.vibrate === 'function') {
    navigator.vibrate(duration)
    applied = true
  }

  return applied
}

export function runRumbleTest(settings?: RumbleSettings): boolean {
  const applied = applyControllerRumbleToGamepads(
    {
      unknown: 0,
      rawLeft: 255,
      rawRight: 220,
      left: 255,
      right: 220,
      multiplier: 1,
      ps5RumbleIntensity: 0,
      ps5TriggerIntensity: 0,
      timestamp: null,
    },
    {
      duration: 280,
      settings: settings ?? currentSettings,
    }
  )

  if (applied) {
    setTimeout(() => {
      applyControllerRumbleToGamepads(
        {
          unknown: 0,
          rawLeft: 0,
          rawRight: 0,
          left: 0,
          right: 0,
          multiplier: 1,
          ps5RumbleIntensity: 0,
          ps5TriggerIntensity: 0,
          timestamp: null,
        },
        {
          duration: 16,
          settings: settings ?? currentSettings,
        }
      )
    }, 320)
  }

  return applied
}


