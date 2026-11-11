/**
 * 游戏手柄服务
 * 使用 Web Gamepad API 检测和连接手柄设备
 */

// 手柄连接状态
export interface GamepadConnectionState {
  isConnected: boolean
  connectedGamepads: GamepadInfo[]
}

// 手柄信息
export interface GamepadInfo {
  index: number
  id: string
  mapping: string
  axes: number
  buttons: number
  timestamp: number
}

// 手柄按钮状态
export interface GamepadButtonState {
  pressed: boolean
  value: number
  touched: boolean
}

// 手柄输入事件
export interface GamepadInputEvent {
  gamepadIndex: number
  buttonIndex?: number
  axisIndex?: number
  buttonState?: GamepadButtonState
  axisValue?: number
}

// 手柄按钮映射（标准 Xbox/PS 手柄）
export enum GamepadButton {
  // 标准按钮
  A = 0, // 底部按钮（Xbox A, PS X）
  B = 1, // 右侧按钮（Xbox B, PS Circle）
  X = 2, // 左侧按钮（Xbox X, PS Square）
  Y = 3, // 顶部按钮（Xbox Y, PS Triangle）
  
  // 肩部按钮
  LeftShoulder = 4, // LB/L1
  RightShoulder = 5, // RB/R1
  LeftTrigger = 6, // LT/L2（通常作为轴）
  RightTrigger = 7, // RT/R2（通常作为轴）
  
  // 功能按钮
  Back = 8, // Select/Share
  Start = 9, // Start/Options
  LeftStick = 10, // 左摇杆按下
  RightStick = 11, // 右摇杆按下
  
  // D-Pad（通常作为按钮 12-15）
  DPadUp = 12,
  DPadDown = 13,
  DPadLeft = 14,
  DPadRight = 15,

  // PlayStation 手柄在标准映射中额外提供的按钮
  Home = 16, // PS
  Touchpad = 17, // 触摸板按下
}

// 手柄轴索引
export enum GamepadAxis {
  LeftStickX = 0,
  LeftStickY = 1,
  RightStickX = 2,
  RightStickY = 3,
}

// PlayStation 按钮名称映射
export const PS5_BUTTON_MAP: Record<number, string> = {
  [GamepadButton.A]: 'cross', // A/X 按钮
  [GamepadButton.Y]: 'triangle', // Y/Triangle 按钮
  [GamepadButton.X]: 'square', // B/Square 按钮
  [GamepadButton.B]: 'circle', // A/Circle 按钮
  [GamepadButton.LeftShoulder]: 'l1',
  [GamepadButton.RightShoulder]: 'r1',
  [GamepadButton.LeftTrigger]: 'l2',
  [GamepadButton.RightTrigger]: 'r2',
  [GamepadButton.Back]: 'share',
  [GamepadButton.Start]: 'options',
  [GamepadButton.LeftStick]: 'l3',
  [GamepadButton.RightStick]: 'r3',
  [GamepadButton.DPadUp]: 'up',
  [GamepadButton.DPadDown]: 'down',
  [GamepadButton.DPadLeft]: 'left',
  [GamepadButton.DPadRight]: 'right',
  [GamepadButton.Home]: 'ps',
  [GamepadButton.Touchpad]: 'touchpad',
}

export class GamepadService {
  private gamepads: Map<number, Gamepad> = new Map()
  private connectedGamepads: GamepadInfo[] = []
  private isPolling = false
  private pollingInterval: ReturnType<typeof requestAnimationFrame> | null = null
  private stateListeners: Set<(state: GamepadConnectionState) => void> = new Set()
  private inputListeners: Set<(event: GamepadInputEvent) => void> = new Set()
  private previousButtonStates: Map<number, boolean[]> = new Map()
  private previousButtonValues: Map<number, number[]> = new Map()
  private previousAxisStates: Map<number, number[]> = new Map()

  constructor() {
    this.setupEventListeners()
  }

  /**
   * 设置事件监听器
   */
  private setupEventListeners(): void {
    // 监听手柄连接
    window.addEventListener('gamepadconnected', (e: GamepadEvent) => {
      console.log('🎮 手柄已连接:', e.gamepad.id, '索引:', e.gamepad.index)
      this.handleGamepadConnected(e.gamepad)
    })

    // 监听手柄断开
    window.addEventListener('gamepaddisconnected', (e: GamepadEvent) => {
      console.log('🎮 手柄已断开:', e.gamepad.id, '索引:', e.gamepad.index)
      this.handleGamepadDisconnected(e.gamepad.index)
    })

    // 初始化时检查已连接的手柄
    this.scanGamepads()
  }

  /**
   * 扫描已连接的手柄
   */
  private scanGamepads(): void {
    const gamepads = navigator.getGamepads()
    for (let i = 0; i < gamepads.length; i++) {
      const gamepad = gamepads[i]
      if (gamepad) {
        this.handleGamepadConnected(gamepad)
      }
    }
  }

  /**
   * 处理手柄连接
   */
  private handleGamepadConnected(gamepad: Gamepad): void {
    this.gamepads.set(gamepad.index, gamepad)
    
    const info: GamepadInfo = {
      index: gamepad.index,
      id: gamepad.id,
      mapping: gamepad.mapping,
      axes: gamepad.axes.length,
      buttons: gamepad.buttons.length,
      timestamp: gamepad.timestamp,
    }

    // 更新已连接手柄列表
    const existingIndex = this.connectedGamepads.findIndex(g => g.index === gamepad.index)
    if (existingIndex >= 0) {
      this.connectedGamepads[existingIndex] = info
    } else {
      this.connectedGamepads.push(info)
    }

    // 初始化按钮和轴状态
    this.previousButtonStates.set(gamepad.index, new Array(gamepad.buttons.length).fill(false))
    this.previousButtonValues.set(gamepad.index, new Array(gamepad.buttons.length).fill(0))
    this.previousAxisStates.set(gamepad.index, new Array(gamepad.axes.length).fill(0))

    this.notifyStateChange()
    this.startPolling()
  }

  /**
   * 处理手柄断开
   */
  private handleGamepadDisconnected(index: number): void {
    this.disconnectGamepad(index)
  }

  /**
   * 手动断开手柄连接（从内部状态中移除）
   */
  disconnectGamepad(index: number): void {
    console.log('🎮 手动断开手柄连接:', index)
    this.gamepads.delete(index)
    this.connectedGamepads = this.connectedGamepads.filter(g => g.index !== index)
    this.previousButtonStates.delete(index)
    this.previousButtonValues.delete(index)
    this.previousAxisStates.delete(index)

    this.notifyStateChange()

    // 如果没有连接的手柄，停止轮询
    if (this.connectedGamepads.length === 0) {
      this.stopPolling()
    }
  }

  /**
   * 通过手柄 ID 断开连接
   */
  disconnectGamepadById(gamepadId: string): boolean {
    const gamepad = this.connectedGamepads.find(g => g.id === gamepadId)
    if (gamepad) {
      this.disconnectGamepad(gamepad.index)
      return true
    }
    return false
  }

  /**
   * 开始轮询手柄输入
   */
  private startPolling(): void {
    if (this.isPolling) {
      return
    }

    this.isPolling = true
    const poll = () => {
      if (!this.isPolling) {
        return
      }

      // 更新所有手柄状态
      const gamepads = navigator.getGamepads()
      for (let i = 0; i < gamepads.length; i++) {
        const gamepad = gamepads[i]
        if (gamepad && this.gamepads.has(gamepad.index)) {
          this.updateGamepadState(gamepad)
        }
      }

      // 继续轮询
      this.pollingInterval = requestAnimationFrame(poll)
    }

    this.pollingInterval = requestAnimationFrame(poll)
  }

  /**
   * 停止轮询
   */
  private stopPolling(): void {
    this.isPolling = false
    if (this.pollingInterval !== null) {
      cancelAnimationFrame(this.pollingInterval)
      this.pollingInterval = null
    }
  }

  /**
   * 更新手柄状态并触发事件
   */
  private updateGamepadState(gamepad: Gamepad): void {
    const previousButtons = this.previousButtonStates.get(gamepad.index) || []
    const previousButtonValues = this.previousButtonValues.get(gamepad.index) || []
    const previousAxes = this.previousAxisStates.get(gamepad.index) || []

    // 检查按钮状态变化
    for (let i = 0; i < gamepad.buttons.length; i++) {
      const button = gamepad.buttons[i]
      const previousPressed = previousButtons[i] || false
      const previousValue = previousButtonValues[i] ?? 0
      const currentPressed = button.pressed
      const currentValue = button.value
      const valueChanged = Math.abs(currentValue - previousValue) > 0.00001

      if (previousPressed !== currentPressed || valueChanged) {
        this.notifyInput({
          gamepadIndex: gamepad.index,
          buttonIndex: i,
          buttonState: {
            pressed: currentPressed,
            value: currentValue,
            touched: button.touched,
          },
        })
        previousButtons[i] = currentPressed
        previousButtonValues[i] = currentValue
      } else {
        previousButtonValues[i] = currentValue
      }
    }

    // 检查轴状态变化（非常低的阈值，确保所有摇杆输入都被捕获）
    for (let i = 0; i < gamepad.axes.length; i++) {
      const axisValue = gamepad.axes[i]
      const previousValue = previousAxes[i] || 0
      const threshold = 0.001 // 非常低的阈值，几乎任何变化都会触发

      // 如果值有变化（超过阈值）或者值本身较大（超过死区），都触发事件
      if (Math.abs(axisValue - previousValue) > threshold || Math.abs(axisValue) > 0.001) {
        this.notifyInput({
          gamepadIndex: gamepad.index,
          axisIndex: i,
          axisValue: axisValue,
        })
        previousAxes[i] = axisValue
      }
    }

    // 更新状态
    this.previousButtonStates.set(gamepad.index, previousButtons)
    this.previousButtonValues.set(gamepad.index, previousButtonValues)
    this.previousAxisStates.set(gamepad.index, previousAxes)
  }

  /**
   * 获取连接状态
   */
  getConnectionState(): GamepadConnectionState {
    return {
      isConnected: this.connectedGamepads.length > 0,
      connectedGamepads: [...this.connectedGamepads],
    }
  }

  /**
   * 获取所有连接的手柄
   */
  getConnectedGamepads(): GamepadInfo[] {
    return [...this.connectedGamepads]
  }

  /**
   * 检查是否有手柄连接
   */
  hasGamepad(): boolean {
    return this.connectedGamepads.length > 0
  }

  /**
   * 获取手柄实例
   */
  getGamepad(index: number): Gamepad | null {
    const gamepads = navigator.getGamepads()
    return gamepads[index] || null
  }

  /**
   * 添加状态变化监听器
   */
  onStateChange(listener: (state: GamepadConnectionState) => void): () => void {
    this.stateListeners.add(listener)
    return () => {
      this.stateListeners.delete(listener)
    }
  }

  /**
   * 添加输入事件监听器
   */
  onInput(listener: (event: GamepadInputEvent) => void): () => void {
    this.inputListeners.add(listener)
    return () => {
      this.inputListeners.delete(listener)
    }
  }

  /**
   * 通知状态变化
   */
  private notifyStateChange(): void {
    const state = this.getConnectionState()
    this.stateListeners.forEach((listener) => listener(state))
  }

  /**
   * 通知输入事件
   */
  private notifyInput(event: GamepadInputEvent): void {
    this.inputListeners.forEach((listener) => listener(event))
  }

  /**
   * 清理资源
   */
  dispose(): void {
    this.stopPolling()
    this.stateListeners.clear()
    this.inputListeners.clear()
    this.gamepads.clear()
    this.connectedGamepads = []
    this.previousButtonStates.clear()
    this.previousAxisStates.clear()
  }
}

// 单例实例
export const gamepadService = new GamepadService()

