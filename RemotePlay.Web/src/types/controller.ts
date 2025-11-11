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


