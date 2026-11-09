export interface ControllerRumblePayload {
  unknown?: number
  rawLeft?: number
  rawRight?: number
  left?: number
  right?: number
  multiplier?: number
  ps5RumbleIntensity?: number
  ps5TriggerIntensity?: number
  timestamp?: string
}

export interface ControllerTriggerEffectsEvent {
  typeLeft: number
  left: number[]
  typeRight: number
  right: number[]
  intensity: number
}

export interface ControllerLightbarEvent {
  red: number
  green: number
  blue: number
  playerIndex: number
}

export interface ControllerRumbleEvent {
  unknown: number
  rawLeft: number
  rawRight: number
  left: number
  right: number
  multiplier: number
  ps5RumbleIntensity: number
  ps5TriggerIntensity: number
  timestamp: string | null
}


