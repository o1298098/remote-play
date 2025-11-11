import * as signalR from '@microsoft/signalr'
import { ControllerRumbleEvent, ControllerRumblePayload } from '@/types/controller'

// API 基础配置
const DEFAULT_API_BASE_URL = `${window.location.origin}/api`

const resolvedApiBaseUrl = import.meta.env.VITE_API_BASE_URL
  ? new URL(import.meta.env.VITE_API_BASE_URL, window.location.origin)
  : new URL(DEFAULT_API_BASE_URL)

// API 请求使用完整的 /api 前缀
const API_BASE_URL = resolvedApiBaseUrl.toString().replace(/\/$/, '')

// SignalR Hub 使用去掉 /api 的根路径
const hubBaseUrl = new URL(resolvedApiBaseUrl.toString())
if (/\/api\/?$/i.test(hubBaseUrl.pathname)) {
  hubBaseUrl.pathname = hubBaseUrl.pathname.replace(/\/api\/?$/i, '/')
}

const CONTROLLER_HUB_URL = new URL('hubs/controller', hubBaseUrl).toString()

// 控制器按钮类型
export type ControllerButtonAction = 'press' | 'release' | 'tap'

// 控制器连接状态
export interface ControllerConnectionState {
  isConnected: boolean
  isConnecting: boolean
  error?: string
}

// SignalR 控制器连接类
export class ControllerService {
  private connection: signalR.HubConnection | null = null
  private isConnecting = false
  private isManualDisconnect = false
  private sessionId: string | null = null
  private connectionStateListeners: Set<(state: ControllerConnectionState) => void> = new Set()
  private rumbleListeners: Set<(event: ControllerRumbleEvent) => void> = new Set()

  /**
   * 连接到控制器 Hub
   */
  async connect(sessionId: string): Promise<void> {
    // 防止并发连接
    if (this.isConnecting) {
      console.warn('SignalR 连接正在进行中，跳过重复连接')
      return
    }

    // 如果已经连接且状态正常，直接返回
    if (
      this.connection &&
      this.connection.state === signalR.HubConnectionState.Connected &&
      this.sessionId === sessionId
    ) {
      console.log('SignalR 已连接，跳过重复连接')
      this.notifyStateChange({ isConnected: true, isConnecting: false })
      return
    }

    this.isConnecting = true
    this.sessionId = sessionId
    this.notifyStateChange({ isConnected: false, isConnecting: true })

    try {
      // 如果已有连接但在非正常状态，先清理
      if (this.connection) {
        const currentState = this.connection.state
        if (currentState === signalR.HubConnectionState.Connecting) {
          console.warn('检测到连接正在进行中，等待完成...')
          // 等待最多3秒让连接完成
          for (let i = 0; i < 30; i++) {
            await new Promise((resolve) => setTimeout(resolve, 100))
            if (this.connection.state === signalR.HubConnectionState.Connected) {
              this.isConnecting = false
              this.notifyStateChange({ isConnected: true, isConnecting: false })
              return
            }
            if (this.connection.state === signalR.HubConnectionState.Disconnected) {
              break
            }
          }
        }

        // 只有在非 Connecting 状态时才断开
        if (this.connection.state !== signalR.HubConnectionState.Connecting) {
          await this.disconnect()
        } else {
          try {
            await this.connection.stop()
          } catch (e) {
            // 忽略错误
          }
          this.connection = null
        }
      }

      console.log('🔌 正在连接 SignalR 控制器...')

      const hubUrl = CONTROLLER_HUB_URL

      this.connection = new signalR.HubConnectionBuilder()
        .withUrl(hubUrl, {
          accessTokenFactory: () => {
            const token = localStorage.getItem('auth_token')
            return token || ''
          },
          transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.LongPolling,
        })
        .withAutomaticReconnect({
          nextRetryDelayInMilliseconds: (retryContext) => {
            // 重试策略：前3次快速重试，之后逐渐增加间隔
            if (retryContext.previousRetryCount < 3) {
              return 1000 // 1秒
            } else {
              return Math.min(30000, 1000 * Math.pow(2, retryContext.previousRetryCount - 2)) // 最多30秒
            }
          },
        })
        .build()

      // 注册事件（在 start 之前）
      this.connection.on('ControllerConnected', (success: boolean) => {
        if (success) {
          console.log('✅ 控制器已通过 SignalR 连接')
          this.notifyStateChange({ isConnected: true, isConnecting: false })
        } else {
          console.warn('⚠️ 控制器连接返回失败')
        }
      })

      this.connection.on('ControllerStarted', (success: boolean) => {
        if (success) {
          console.log('✅ 控制器已启动:', success)
          this.notifyStateChange({ isConnected: true, isConnecting: false })
        } else {
          console.warn('⚠️ 控制器启动失败:', success)
        }
      })

      this.connection.on('Error', (message: string) => {
        if (message && message.includes('已连接')) {
          console.log('ℹ️ SignalR 提示:', message)
          this.notifyStateChange({ isConnected: true, isConnecting: false })
        } else {
          console.error('❌ SignalR 错误:', message)
          this.notifyStateChange({ isConnected: false, isConnecting: false, error: message })
        }
      })

      this.connection.on('ControllerRumble', (payload: ControllerRumblePayload) => {
        const event = this.normalizeRumblePayload(payload)
        if (event) {
          this.notifyRumble(event)
        }
      })

      this.connection.onclose((error) => {
        if (error) {
          console.warn('⚠️ SignalR 连接已关闭（错误:', error.message, '）')
        } else {
          console.log('⚠️ SignalR 连接已关闭')
        }
        this.notifyStateChange({ isConnected: false, isConnecting: false })

        // 只有在非手动断开且不是连接失败时才尝试自动重连
        if (!this.isManualDisconnect && this.sessionId) {
          setTimeout(async () => {
            if (
              !this.isManualDisconnect &&
              this.sessionId &&
              (!this.connection ||
                this.connection.state === signalR.HubConnectionState.Disconnected)
            ) {
              console.log('🔄 SignalR 连接意外断开，尝试自动重连...')
              try {
                await this.connect(this.sessionId)
              } catch (reconnectError) {
                console.error('❌ 自动重连失败:', reconnectError)
              }
            }
          }, 2000) // 延迟2秒重连
        }
      })

      // 启动连接
      await this.connection.start()

      // 等待连接状态变为 Connected（最多等待2秒）
      let waitCount = 0
      while (
        this.connection.state !== signalR.HubConnectionState.Connected &&
        waitCount < 20
      ) {
        await new Promise((resolve) => setTimeout(resolve, 100))
        waitCount++
      }

      // 验证连接状态
      const state = this.connection.state
      if (state !== signalR.HubConnectionState.Connected) {
        throw new Error(`SignalR 连接未成功建立，当前状态: ${state}`)
      }

      console.log('✅ SignalR 连接已建立')

      // 连接控制器到会话
      try {
        await this.connection.invoke('ConnectController', sessionId)
        // 等待一下让 ControllerConnected 事件处理（最多等待500ms）
        for (let i = 0; i < 5; i++) {
          await new Promise((resolve) => setTimeout(resolve, 100))
          if (this.connection.state === signalR.HubConnectionState.Connected) {
            break
          }
        }
      } catch (connectError: any) {
        console.warn('⚠️ ConnectController 调用异常:', connectError.message, '，继续尝试启动')
      }

      // 启动控制器
      try {
        await this.connection.invoke('StartController', sessionId)
        // 等待一下让 ControllerStarted 事件处理（最多等待500ms）
        for (let i = 0; i < 5; i++) {
          await new Promise((resolve) => setTimeout(resolve, 100))
          if (this.connection.state === signalR.HubConnectionState.Connected) {
            break
          }
        }
      } catch (startError: any) {
        console.warn('⚠️ StartController 调用失败:', startError.message)
      }

      // 最终验证：如果连接状态正常，认为连接成功
      if (this.connection.state === signalR.HubConnectionState.Connected) {
        this.notifyStateChange({ isConnected: true, isConnecting: false })
        console.log('✅ 控制器连接验证成功')
      }
    } catch (error: any) {
      console.error('❌ SignalR 连接失败:', error.message)
      if (this.connection) {
        try {
          const currentState = this.connection.state
          if (
            currentState !== signalR.HubConnectionState.Disconnected &&
            currentState !== signalR.HubConnectionState.Disconnecting
          ) {
            if (currentState === signalR.HubConnectionState.Connecting) {
              await new Promise((resolve) => setTimeout(resolve, 500))
            }
            if (this.connection.state !== signalR.HubConnectionState.Disconnected) {
              await this.connection.stop()
            }
          }
        } catch (stopError) {
          console.warn('⚠️ 停止 SignalR 连接时出错（可忽略）:', stopError)
        }
      }
      this.connection = null
      this.notifyStateChange({ isConnected: false, isConnecting: false, error: error.message })
      throw error
    } finally {
      this.isConnecting = false
    }
  }

  /**
   * 断开控制器连接
   */
  async disconnect(): Promise<void> {
    if (this.connection) {
      try {
        this.isManualDisconnect = true

        // 检查连接状态，只有在连接状态正常时才尝试调用 DisconnectController
        if (this.connection.state === signalR.HubConnectionState.Connected) {
          if (this.sessionId) {
            try {
              // 在调用前再次检查连接状态，避免竞态条件
              if (this.connection.state === signalR.HubConnectionState.Connected) {
                await this.connection.invoke('DisconnectController', this.sessionId)
              }
            } catch (invokeError: any) {
              // 如果是连接已关闭的错误，静默处理（不显示警告）
              const errorMessage = invokeError?.message || String(invokeError)
              if (
                errorMessage.includes('connection being closed') ||
                errorMessage.includes('连接已关闭') ||
                errorMessage.includes('Invocation canceled')
              ) {
                // 连接已关闭，这是正常情况，不需要警告
              } else {
                // 其他错误才显示警告
                console.warn('⚠️ DisconnectController 调用失败（可忽略）:', invokeError)
              }
            }
          }
          // 再次检查状态后再停止连接
          try {
            await this.connection.stop()
          } catch (stopError) {
            // 停止连接时的错误可以忽略
          }
        }
        console.log('✅ SignalR 连接已断开')
      } catch (error: any) {
        console.warn('⚠️ 断开 SignalR 连接时出错:', error.message)
      } finally {
        this.connection = null
        this.notifyStateChange({ isConnected: false, isConnecting: false })
        // 重置手动断开标记（延迟一点，确保 onclose 事件已经处理）
        setTimeout(() => {
          this.isManualDisconnect = false
        }, 1000)
      }
    }
    this.sessionId = null
  }

  /**
   * 发送控制器按钮命令（SignalR）
   */
  async sendButton(
    buttonName: string,
    action: ControllerButtonAction = 'tap',
    delayMs: number = 0
  ): Promise<void> {
    // 如果正在连接，等待连接完成（最多等待2秒）
    if (this.isConnecting) {
      console.warn('⚠️ SignalR 正在连接中，等待完成...')
      for (let i = 0; i < 20; i++) {
        await new Promise((resolve) => setTimeout(resolve, 100))
        if (!this.isConnecting) {
          break
        }
      }
    }

    // 检查 SignalR 连接状态
    const isConnected =
      this.connection &&
      this.connection.state === signalR.HubConnectionState.Connected

    if (!isConnected) {
      // 如果正在连接，再等待一下
      if (this.isConnecting) {
        console.warn('⚠️ SignalR 连接仍在进行中，等待完成...')
        await new Promise((resolve) => setTimeout(resolve, 500))
      }

      // 再次检查连接状态
      const stillNotConnected =
        !this.connection ||
        this.connection.state !== signalR.HubConnectionState.Connected

      if (stillNotConnected && !this.isConnecting && this.sessionId) {
        // 只有在不在连接中时才尝试连接
        console.warn('⚠️ SignalR 连接断开，尝试重新连接...')
        await this.connect(this.sessionId)

        // 再次检查连接状态
        const reconnectSuccess =
          this.connection &&
          this.connection.state === signalR.HubConnectionState.Connected

        if (!reconnectSuccess) {
          throw new Error('SignalR 重连失败')
        }
      } else if (stillNotConnected) {
        throw new Error('SignalR 连接不可用')
      }
    }

    if (!this.connection || this.connection.state !== signalR.HubConnectionState.Connected) {
      throw new Error('SignalR 连接不可用')
    }

    if (!this.sessionId) {
      throw new Error('没有活动的 Remote Play Session')
    }

    const actualDelay = action === 'tap' ? delayMs || 100 : delayMs || 0
    console.log('📤 SignalR 调用 Button:', {
      sessionId: this.sessionId,
      buttonName,
      action,
      delayMs: actualDelay,
      connectionState: this.connection?.state,
    })
    await this.connection.invoke('Button', this.sessionId, buttonName, action, actualDelay)
    console.log('✅ SignalR Button 调用成功')
  }

  /**
   * 发送摇杆输入（SignalR）
   */
  async sendStick(
    stickType: 'left' | 'right',
    x: number,
    y: number
  ): Promise<void> {
    // 快速检查连接状态，如果已断开或正在断开，直接返回（不抛出错误）
    if (!this.connection || !this.sessionId) {
      return // 静默返回，不记录错误
    }

    const connectionState = this.connection.state
    if (
      connectionState === signalR.HubConnectionState.Disconnected ||
      connectionState === signalR.HubConnectionState.Disconnecting
    ) {
      return // 连接已断开或正在断开，静默返回
    }

    // 如果正在连接，等待连接完成（最多等待2秒）
    if (this.isConnecting) {
      console.warn('⚠️ SignalR 正在连接中，等待完成...')
      for (let i = 0; i < 20; i++) {
        await new Promise((resolve) => setTimeout(resolve, 100))
        if (!this.isConnecting) {
          break
        }
      }
    }

    // 再次检查连接状态（可能在等待过程中连接已断开）
    if (
      !this.connection ||
      this.connection.state !== signalR.HubConnectionState.Connected ||
      !this.sessionId
    ) {
      return // 静默返回
    }

    // 确保值在 -1 到 1 之间
    const clampedX = Math.max(-1, Math.min(1, x))
    const clampedY = Math.max(-1, Math.min(1, y))

    // 减少日志输出，避免控制台刷屏（只在值较大时记录）
    try {
      // 调用后端的摇杆 API
      if (stickType === 'left') {
        await this.connection.invoke('SetLeftStick', this.sessionId, clampedX, clampedY)
      } else {
        await this.connection.invoke('SetRightStick', this.sessionId, clampedX, clampedY)
      }
    } catch (error: any) {
      // 如果错误是因为连接已关闭，静默处理（不记录错误）
      if (
        error?.message?.includes('connection being closed') ||
        error?.message?.includes('connection closed') ||
        error?.message?.includes('Invocation canceled')
      ) {
        // 连接已关闭，这是正常的断开流程，不需要记录错误
        return
      }

      console.error('❌ SignalR Stick 调用失败:', error)
      // 如果 SignalR 失败且连接仍然可用，尝试使用 HTTP API 备用方案
      if (this.connection && this.connection.state === signalR.HubConnectionState.Connected && this.sessionId) {
        try {
          await sendControllerStickHTTP(this.sessionId, stickType, clampedX, clampedY)
        } catch (httpError) {
          console.error('❌ HTTP Stick 调用也失败:', httpError)
          // 不抛出错误，静默失败
        }
      }
    }
  }

  /**
   * 同时发送左右摇杆数据（推荐方法，更高效）
   * @param leftX 左摇杆 X 轴 (-1 到 1)
   * @param leftY 左摇杆 Y 轴 (-1 到 1)
   * @param rightX 右摇杆 X 轴 (-1 到 1)
   * @param rightY 右摇杆 Y 轴 (-1 到 1)
   */
  async sendSticks(
    leftX: number,
    leftY: number,
    rightX: number,
    rightY: number
  ): Promise<void> {
    // 快速检查连接状态，如果已断开或正在断开，直接返回（不抛出错误）
    if (!this.connection || !this.sessionId) {
      return // 静默返回，不记录错误
    }

    const connectionState = this.connection.state
    if (
      connectionState === signalR.HubConnectionState.Disconnected ||
      connectionState === signalR.HubConnectionState.Disconnecting
    ) {
      return // 连接已断开或正在断开，静默返回
    }

    // 如果正在连接，等待连接完成（最多等待2秒）
    if (this.isConnecting) {
      console.warn('⚠️ SignalR 正在连接中，等待完成...')
      for (let i = 0; i < 20; i++) {
        await new Promise((resolve) => setTimeout(resolve, 100))
        if (!this.isConnecting) {
          break
        }
      }
    }

    // 再次检查连接状态（可能在等待过程中连接已断开）
    if (
      !this.connection ||
      this.connection.state !== signalR.HubConnectionState.Connected ||
      !this.sessionId
    ) {
      return // 静默返回
    }

    // 确保值在 -1 到 1 之间
    const clampedLeftX = Math.max(-1, Math.min(1, leftX))
    const clampedLeftY = Math.max(-1, Math.min(1, leftY))
    const clampedRightX = Math.max(-1, Math.min(1, rightX))
    const clampedRightY = Math.max(-1, Math.min(1, rightY))

    try {
      // 使用 SetSticks 方法同时发送左右摇杆（推荐方法）
      await this.connection.invoke('SetSticks', this.sessionId, clampedLeftX, clampedLeftY, clampedRightX, clampedRightY)
    } catch (error: any) {
      // 如果错误是因为连接已关闭，静默处理（不记录错误）
      if (
        error?.message?.includes('connection being closed') ||
        error?.message?.includes('connection closed') ||
        error?.message?.includes('Invocation canceled')
      ) {
        // 连接已关闭，这是正常的断开流程，不需要记录错误
        return
      }

      console.error('❌ SignalR SetSticks 调用失败:', error)
      // 如果 SetSticks 失败，尝试使用单独的 SetLeftStick 和 SetRightStick 方法
      if (this.connection && this.connection.state === signalR.HubConnectionState.Connected && this.sessionId) {
        try {
          await Promise.all([
            this.connection.invoke('SetLeftStick', this.sessionId, clampedLeftX, clampedLeftY),
            this.connection.invoke('SetRightStick', this.sessionId, clampedRightX, clampedRightY),
          ])
        } catch (fallbackError) {
          console.error('❌ 备用摇杆方法也失败:', fallbackError)
          // 不抛出错误，静默失败
        }
      }
    }
  }

  /**
   * 发送扳机压力（L2/R2）
   */
  async sendTriggers(l2: number, r2: number): Promise<void> {
    if (!this.sessionId) {
      throw new Error('没有活动的 Remote Play Session')
    }

    if (!this.connection) {
      return
    }

    const connectionState = this.connection.state
    if (
      connectionState === signalR.HubConnectionState.Disconnected ||
      connectionState === signalR.HubConnectionState.Disconnecting
    ) {
      return
    }

    if (this.isConnecting) {
      console.warn('⚠️ SignalR 正在连接中，等待完成...')
      for (let i = 0; i < 20; i++) {
        await new Promise((resolve) => setTimeout(resolve, 100))
        if (!this.isConnecting) {
          break
        }
      }
    }

    if (
      !this.connection ||
      this.connection.state !== signalR.HubConnectionState.Connected ||
      !this.sessionId
    ) {
      return
    }

    const clampedL2 = Math.max(0, Math.min(1, l2))
    const clampedR2 = Math.max(0, Math.min(1, r2))

    try {
      await this.connection.invoke('SetTriggers', this.sessionId, clampedL2, clampedR2)
    } catch (error: any) {
      if (
        error?.message?.includes('connection being closed') ||
        error?.message?.includes('connection closed') ||
        error?.message?.includes('Invocation canceled')
      ) {
        return
      }

      console.error('❌ SignalR SetTriggers 调用失败:', error)
      if (this.connection && this.connection.state === signalR.HubConnectionState.Connected && this.sessionId) {
        console.log('🔄 尝试使用 HTTP Trigger 备用方案...')
        try {
          await sendControllerTriggersHTTP(this.sessionId, clampedL2, clampedR2)
          console.log('✅ HTTP Trigger 调用成功')
        } catch (httpError) {
          console.error('❌ HTTP Trigger 调用也失败:', httpError)
        }
      }
    }
  }

  /**
   * 检查连接状态
   */
  isConnected(): boolean {
    return (
      !!this.connection &&
      this.connection.state === signalR.HubConnectionState.Connected &&
      !!this.sessionId
    )
  }

  /**
   * 注册震动事件监听
   */
  onRumble(listener: (event: ControllerRumbleEvent) => void): () => void {
    this.rumbleListeners.add(listener)
    return () => {
      this.rumbleListeners.delete(listener)
    }
  }

  /**
   * 添加连接状态监听器
   */
  onStateChange(listener: (state: ControllerConnectionState) => void): () => void {
    this.connectionStateListeners.add(listener)
    return () => {
      this.connectionStateListeners.delete(listener)
    }
  }

  /**
   * 通知状态变化
   */
  private notifyStateChange(state: ControllerConnectionState): void {
    this.connectionStateListeners.forEach((listener) => listener(state))
  }

  private normalizeRumblePayload(payload: ControllerRumblePayload | null | undefined): ControllerRumbleEvent | null {
    if (!payload || typeof payload !== 'object') {
      return null
    }

    const ensureNumber = (value: unknown, fallback: number): number =>
      typeof value === 'number' && Number.isFinite(value) ? value : fallback

    const clampToByte = (value: number): number => {
      if (!Number.isFinite(value)) {
        return 0
      }
      if (value <= 0) return 0
      if (value >= 255) return 255
      return Math.round(value)
    }

    const rawLeft = clampToByte(ensureNumber(payload.rawLeft ?? payload.left, 0))
    const rawRight = clampToByte(ensureNumber(payload.rawRight ?? payload.right, 0))
    const adjustedLeft = clampToByte(ensureNumber(payload.left ?? payload.rawLeft, rawLeft))
    const adjustedRight = clampToByte(ensureNumber(payload.right ?? payload.rawRight, rawRight))

    return {
      unknown: clampToByte(ensureNumber(payload.unknown, 0)),
      rawLeft,
      rawRight,
      left: adjustedLeft,
      right: adjustedRight,
      multiplier: ensureNumber(payload.multiplier, 1),
      ps5RumbleIntensity: ensureNumber(payload.ps5RumbleIntensity, 0),
      ps5TriggerIntensity: ensureNumber(payload.ps5TriggerIntensity, 0),
      timestamp: typeof payload.timestamp === 'string' ? payload.timestamp : null,
    }
  }

  private notifyRumble(event: ControllerRumbleEvent): void {
    this.rumbleListeners.forEach((listener) => {
      try {
        listener(event)
      } catch (error) {
        console.warn('⚠️ 震动事件处理失败:', error)
      }
    })
  }
}

// 单例实例
export const controllerService = new ControllerService()

/**
 * HTTP API 备用方案：发送控制器按钮命令
 */
export async function sendControllerButtonHTTP(
  sessionId: string,
  buttonName: string,
  action: ControllerButtonAction = 'tap',
  delayMs: number = 0
): Promise<void> {
  const actualDelay = action === 'tap' ? delayMs || 200 : delayMs || 0

  const token = localStorage.getItem('auth_token')
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
  }
  if (token) {
    headers.Authorization = `Bearer ${token}`
  }

  const response = await fetch(`${API_BASE_URL}/playstation/controller/button`, {
    method: 'POST',
    headers,
    body: JSON.stringify({
      sessionId,
      button: buttonName,
      action,
      delayMs: actualDelay,
    }),
  })

  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`)
  }

  const result = await response.json()
  if (!result.success) {
    throw new Error(result.errorMessage || result.message || '未知错误')
  }
}

/**
 * HTTP API 备用方案：发送摇杆输入
 */
export async function sendControllerStickHTTP(
  sessionId: string,
  stickType: 'left' | 'right',
  x: number,
  y: number
): Promise<void> {
  const token = localStorage.getItem('auth_token')
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
  }
  if (token) {
    headers.Authorization = `Bearer ${token}`
  }

  // 确保值在 -1 到 1 之间
  const clampedX = Math.max(-1, Math.min(1, x))
  const clampedY = Math.max(-1, Math.min(1, y))

  // 尝试多个可能的 API 端点
  const endpoints = [
    `${API_BASE_URL}/playstation/controller/stick`,
    `${API_BASE_URL}/playstation/controller/analog`,
    `${API_BASE_URL}/playstation/controller/joystick`,
  ]

  let lastError: Error | null = null

  for (const endpoint of endpoints) {
    try {
      const response = await fetch(endpoint, {
        method: 'POST',
        headers,
        body: JSON.stringify({
          sessionId,
          stickType,
          x: clampedX,
          y: clampedY,
        }),
      })

      if (response.ok) {
        const result = await response.json()
        if (result.success) {
          return
        } else {
          throw new Error(result.errorMessage || result.message || '未知错误')
        }
      } else {
        throw new Error(`HTTP ${response.status}`)
      }
    } catch (error) {
      lastError = error instanceof Error ? error : new Error(String(error))
      // 继续尝试下一个端点
      continue
    }
  }

  // 如果所有端点都失败，抛出最后一个错误
  throw lastError || new Error('所有 HTTP Stick API 端点都失败')
}

/**
 * HTTP API 备用方案：发送扳机压力
 */
export async function sendControllerTriggersHTTP(
  sessionId: string,
  l2?: number,
  r2?: number
): Promise<void> {
  if (typeof l2 !== 'number' && typeof r2 !== 'number') {
    return
  }

  const token = localStorage.getItem('auth_token')
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
  }
  if (token) {
    headers.Authorization = `Bearer ${token}`
  }

  const payload: Record<string, unknown> = {
    sessionId,
  }
  if (typeof l2 === 'number') {
    payload.l2 = Math.max(0, Math.min(1, l2))
  }
  if (typeof r2 === 'number') {
    payload.r2 = Math.max(0, Math.min(1, r2))
  }

  const response = await fetch(`${API_BASE_URL}/playstation/controller/trigger`, {
    method: 'POST',
    headers,
    body: JSON.stringify(payload),
  })

  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`)
  }

  const result = await response.json()
  if (!result.success) {
    throw new Error(result.errorMessage || result.message || '未知错误')
  }
}

