export const AXIS_DEADZONE = 0.002
export const SEND_INTERVAL_MS = 8
export const MAX_HEARTBEAT_INTERVAL_MS = 32

export const QUICK_HOLD_ACTIVATE = 0.35
export const QUICK_HOLD_RELEASE_THRESHOLD = 0.04
export const QUICK_HOLD_DURATION_MS = 120
export const STICK_RADIAL_DEADZONE = 0.08

export const MOUSE_SENSITIVITY = 0.003
export const MOUSE_DECAY_HALF_LIFE_MS = 60
export const MOUSE_IDLE_THRESHOLD = 0.0025

export const clamp = (value: number, min = -1, max = 1) => Math.max(min, Math.min(max, value))

export const getTimestamp = () => (typeof performance !== 'undefined' ? performance.now() : Date.now())

