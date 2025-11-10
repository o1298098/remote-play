import { useCallback, useRef } from 'react'
import { GamepadAxis } from '@/service/gamepad.service'
import {
  QUICK_HOLD_ACTIVATE,
  QUICK_HOLD_RELEASE_THRESHOLD,
  QUICK_HOLD_DURATION_MS,
  STICK_RADIAL_DEADZONE,
  MOUSE_DECAY_HALF_LIFE_MS,
  MOUSE_IDLE_THRESHOLD,
  clamp,
  clamp01,
  getTimestamp,
} from './constants'

interface StickVector {
  x: number
  y: number
}

interface StickState {
  leftStick: StickVector
  rightStick: StickVector
}

interface TriggerState {
  l2: number
  r2: number
}

interface HoldAxisState {
  value: number
  expiry: number
}

interface StickHoldState {
  left: {
    x: HoldAxisState
    y: HoldAxisState
  }
  right: {
    x: HoldAxisState
    y: HoldAxisState
  }
}

interface MouseState {
  isPointerLocked: boolean
  velocityX: number
  velocityY: number
  lastUpdateTime: number
}

export interface NormalizedStickState {
  leftX: number
  leftY: number
  rightX: number
  rightY: number
  l2: number
  r2: number
}

const createStickHoldState = (): StickHoldState => ({
  left: {
    x: { value: 0, expiry: 0 },
    y: { value: 0, expiry: 0 },
  },
  right: {
    x: { value: 0, expiry: 0 },
    y: { value: 0, expiry: 0 },
  },
})

export const useStickInputState = () => {
  const stickStateRef = useRef<StickState>({
    leftStick: { x: 0, y: 0 },
    rightStick: { x: 0, y: 0 },
  })
  const stickHoldRef = useRef<StickHoldState>(createStickHoldState())
  const triggerStateRef = useRef<TriggerState>({
    l2: 0,
    r2: 0,
  })
  const mouseStateRef = useRef<MouseState>({
    isPointerLocked: false,
    velocityX: 0,
    velocityY: 0,
    lastUpdateTime: getTimestamp(),
  })

  const applyMouseDecay = useCallback(() => {
    const mouseState = mouseStateRef.current
    const now = getTimestamp()

    if (!mouseState.isPointerLocked && mouseState.velocityX === 0 && mouseState.velocityY === 0) {
      mouseState.lastUpdateTime = now
      return
    }

    const delta = Math.max(0, now - mouseState.lastUpdateTime)
    if (delta === 0) {
      return
    }

    if (!mouseState.isPointerLocked) {
      mouseState.velocityX = 0
      mouseState.velocityY = 0
      mouseState.lastUpdateTime = now
      return
    }

    const decayFactor = Math.pow(0.5, delta / MOUSE_DECAY_HALF_LIFE_MS)

    mouseState.velocityX *= decayFactor
    mouseState.velocityY *= decayFactor

    if (Math.abs(mouseState.velocityX) < MOUSE_IDLE_THRESHOLD) {
      mouseState.velocityX = 0
    }
    if (Math.abs(mouseState.velocityY) < MOUSE_IDLE_THRESHOLD) {
      mouseState.velocityY = 0
    }

    mouseState.lastUpdateTime = now
  }, [])

  const applyHold = useCallback((stick: 'left' | 'right', axis: 'x' | 'y', value: number, now: number) => {
    const hold = stickHoldRef.current[stick][axis]
    const absValue = Math.abs(value)
    const absHold = Math.abs(hold.value)

    if (absValue >= QUICK_HOLD_ACTIVATE) {
      hold.value = value
      hold.expiry = now + QUICK_HOLD_DURATION_MS
      return value
    }

    if (absValue <= QUICK_HOLD_RELEASE_THRESHOLD) {
      if (hold.expiry > now && absHold >= QUICK_HOLD_ACTIVATE) {
        return hold.value
      }
      hold.value = value
      hold.expiry = now
      return value
    }

    hold.value = value
    hold.expiry = now + QUICK_HOLD_DURATION_MS
    return value
  }, [])

  const applyRadialDeadzone = useCallback((x: number, y: number) => {
    const magnitude = Math.hypot(x, y)
    if (magnitude <= STICK_RADIAL_DEADZONE || magnitude === 0) {
      return { x: 0, y: 0 }
    }

    const scaledMagnitude = Math.min(1, (magnitude - STICK_RADIAL_DEADZONE) / (1 - STICK_RADIAL_DEADZONE))
    const scale = scaledMagnitude / magnitude
    return {
      x: x * scale,
      y: y * scale,
    }
  }, [])

  const getNormalizedState = useCallback((): NormalizedStickState => {
    applyMouseDecay()
    const { leftStick, rightStick } = stickStateRef.current
    const mouseState = mouseStateRef.current
    const triggerState = triggerStateRef.current
    const now = getTimestamp()

    const leftRaw = {
      x: clamp(leftStick.x),
      y: clamp(leftStick.y),
    }
    const rightGamepadRaw = {
      x: clamp(rightStick.x),
      y: clamp(rightStick.y),
    }
    const hasMouseInput = mouseState.isPointerLocked || mouseState.velocityX !== 0 || mouseState.velocityY !== 0
    const rightRaw = hasMouseInput
      ? {
          x: clamp(rightGamepadRaw.x + mouseState.velocityX),
          y: clamp(rightGamepadRaw.y + mouseState.velocityY),
        }
      : rightGamepadRaw

    const leftDeadzone = applyRadialDeadzone(leftRaw.x, leftRaw.y)
    const rightDeadzone = applyRadialDeadzone(rightRaw.x, rightRaw.y)

    return {
      leftX: applyHold('left', 'x', leftDeadzone.x, now),
      leftY: applyHold('left', 'y', leftDeadzone.y, now),
      rightX: applyHold('right', 'x', rightDeadzone.x, now),
      rightY: applyHold('right', 'y', rightDeadzone.y, now),
      l2: clamp01(triggerState.l2),
      r2: clamp01(triggerState.r2),
    }
  }, [applyHold, applyMouseDecay, applyRadialDeadzone])

  const snapshotGamepadAxes = useCallback((gamepad: Gamepad) => {
    if (gamepad.axes.length >= 2) {
      stickStateRef.current.leftStick.x = clamp(gamepad.axes[GamepadAxis.LeftStickX] ?? 0)
      stickStateRef.current.leftStick.y = clamp(gamepad.axes[GamepadAxis.LeftStickY] ?? 0)
    }

    if (gamepad.axes.length >= 4) {
      stickStateRef.current.rightStick.x = clamp(gamepad.axes[GamepadAxis.RightStickX] ?? 0)
      stickStateRef.current.rightStick.y = clamp(gamepad.axes[GamepadAxis.RightStickY] ?? 0)
    }
  }, [])

  const handleGamepadAxis = useCallback((axisIndex: number, axisValue: number) => {
    const value = clamp(axisValue)
    if (axisIndex === GamepadAxis.LeftStickX) {
      stickStateRef.current.leftStick.x = value
    } else if (axisIndex === GamepadAxis.LeftStickY) {
      stickStateRef.current.leftStick.y = value
    } else if (axisIndex === GamepadAxis.RightStickX) {
      stickStateRef.current.rightStick.x = value
    } else if (axisIndex === GamepadAxis.RightStickY) {
      stickStateRef.current.rightStick.y = value
    }
  }, [])

  const setPointerLock = useCallback((isLocked: boolean) => {
    const mouseState = mouseStateRef.current
    mouseState.isPointerLocked = isLocked
    mouseState.velocityX = 0
    mouseState.velocityY = 0
    mouseState.lastUpdateTime = getTimestamp()

  }, [])

  const setMouseVelocity = useCallback((x: number, y: number, timestamp?: number) => {
    const mouseState = mouseStateRef.current
    mouseState.velocityX = clamp(x)
    mouseState.velocityY = clamp(y)
    mouseState.lastUpdateTime = timestamp ?? getTimestamp()
  }, [])

  const setTriggerPressure = useCallback((trigger: 'l2' | 'r2', value: number) => {
    const clamped = clamp01(value)
    const triggerState = triggerStateRef.current
    triggerState[trigger] = clamped
  }, [])

  const isPointerLocked = useCallback(() => mouseStateRef.current.isPointerLocked, [])

  const reset = useCallback(() => {
    stickStateRef.current.leftStick.x = 0
    stickStateRef.current.leftStick.y = 0
    stickStateRef.current.rightStick.x = 0
    stickStateRef.current.rightStick.y = 0

    stickHoldRef.current = createStickHoldState()

    const mouseState = mouseStateRef.current
    mouseState.isPointerLocked = false
    mouseState.velocityX = 0
    mouseState.velocityY = 0
    mouseState.lastUpdateTime = getTimestamp()

    triggerStateRef.current.l2 = 0
    triggerStateRef.current.r2 = 0
  }, [])

  return {
    stickStateRef,
    getNormalizedState,
    snapshotGamepadAxes,
    handleGamepadAxis,
    setPointerLock,
    setMouseVelocity,
    setTriggerPressure,
    isPointerLocked,
    reset,
  }
}

