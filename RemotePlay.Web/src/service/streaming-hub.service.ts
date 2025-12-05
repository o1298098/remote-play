import * as signalR from '@microsoft/signalr'

const DEFAULT_API_BASE_URL = `${window.location.origin}/api`

const resolvedApiBaseUrl = import.meta.env.VITE_API_BASE_URL
  ? new URL(import.meta.env.VITE_API_BASE_URL, window.location.origin)
  : new URL(DEFAULT_API_BASE_URL)

const hubBaseUrl = new URL(resolvedApiBaseUrl.toString())
if (/\/api\/?$/i.test(hubBaseUrl.pathname)) {
  hubBaseUrl.pathname = hubBaseUrl.pathname.replace(/\/api\/?$/i, '/')
}

const STREAMING_HUB_URL = new URL('hubs/streaming', hubBaseUrl).toString()

export class StreamingHubService {
  private connection: signalR.HubConnection | null = null
  private connectingPromise: Promise<signalR.HubConnection> | null = null
  private pendingKeyframeResolvers: Array<(success: boolean) => void> = []
  private pendingReorderQueueResetResolvers: Array<(success: boolean) => void> = []
  
  // ✅ ICE Restart 事件回调
  public onIceRestartOffer?: (offerSdp: string) => void
  public onIceRestartFailed?: (reason: string) => void

  private ensureConnection(): Promise<signalR.HubConnection> {
    if (this.connection && this.connection.state === signalR.HubConnectionState.Connected) {
      return Promise.resolve(this.connection)
    }

    if (this.connectingPromise) {
      return this.connectingPromise
    }

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(STREAMING_HUB_URL, {
        accessTokenFactory: () => localStorage.getItem('auth_token') || '',
        transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.LongPolling,
      })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          if (retryContext.previousRetryCount < 3) {
            return 1000
          }
          return Math.min(30000, 1000 * Math.pow(2, retryContext.previousRetryCount - 2))
        },
      })
      .build()

    connection.on('KeyframeRequested', (success: boolean) => {
      const resolver = this.pendingKeyframeResolvers.shift()
      if (resolver) {
        resolver(success)
      } else {
        console.debug('收到 KeyframeRequested 事件，但没有待处理请求', success)
      }
    })

    connection.on('ReorderQueueResetResult', (success: boolean) => {
      const resolver = this.pendingReorderQueueResetResolvers.shift()
      if (resolver) {
        resolver(success)
      } else {
        console.debug('收到 ReorderQueueResetResult 事件，但没有待处理请求', success)
      }
    })

    // ✅ 监听 ICE Restart Offer 事件
    connection.on('IceRestartOffer', (offerSdp: string) => {
      console.log('🔄 收到 ICE Restart Offer，触发重新协商')
      if (this.onIceRestartOffer) {
        this.onIceRestartOffer(offerSdp)
      }
    })

    // ✅ 监听 ICE Restart 失败事件
    connection.on('IceRestartFailed', (reason: string) => {
      console.warn('❌ ICE Restart 失败:', reason)
      if (this.onIceRestartFailed) {
        this.onIceRestartFailed(reason)
      }
    })

    connection.on('Error', (message: string) => {
      console.warn('StreamingHub 错误:', message)
    })

    connection.onclose((error) => {
      if (error) {
        console.warn('StreamingHub 连接关闭（错误）:', error.message)
      } else {
        console.log('StreamingHub 连接已关闭')
      }
      this.connection = null
      this.connectingPromise = null
      this.pendingKeyframeResolvers.splice(0, this.pendingKeyframeResolvers.length)
      this.pendingReorderQueueResetResolvers.splice(0, this.pendingReorderQueueResetResolvers.length)
    })

    connection.onreconnecting((error) => {
      console.warn('StreamingHub 正在重连...', error?.message)
    })

    connection.onreconnected((connectionId) => {
      console.log('StreamingHub 重连成功:', connectionId)
    })

    this.connection = connection
    this.connectingPromise = connection
      .start()
      .then(() => {
        this.connectingPromise = null
        return connection
      })
      .catch((error) => {
        console.error('StreamingHub 连接失败:', error)
        this.connection = null
        this.connectingPromise = null
        this.pendingKeyframeResolvers.splice(0, this.pendingKeyframeResolvers.length)
        this.pendingReorderQueueResetResolvers.splice(0, this.pendingReorderQueueResetResolvers.length)
        throw error
      })

    return this.connectingPromise
  }

  async requestKeyframe(sessionId: string): Promise<boolean> {
    if (!sessionId) {
      console.warn('请求关键帧失败：SessionId 为空')
      return false
    }

    try {
      const connection = await this.ensureConnection()
      return await new Promise<boolean>((resolve) => {
        const resolver = (success: boolean) => {
          window.clearTimeout(timeoutId)
          resolve(success)
        }
        this.pendingKeyframeResolvers.push(resolver)

        const timeoutId = window.setTimeout(() => {
          const index = this.pendingKeyframeResolvers.indexOf(resolver)
          if (index >= 0) {
            this.pendingKeyframeResolvers.splice(index, 1)
          }
          console.warn('请求关键帧超时')
          resolve(false)
        }, 5000)

        connection.invoke('RequestKeyframe', sessionId).catch((error) => {
          console.error('通过 StreamingHub 请求关键帧失败:', error)
          const index = this.pendingKeyframeResolvers.indexOf(resolver)
          if (index >= 0) {
            this.pendingKeyframeResolvers.splice(index, 1)
          }
          window.clearTimeout(timeoutId)
          resolve(false)
        })
      })
    } catch (error) {
      console.error('建立 StreamingHub 连接失败，无法请求关键帧:', error)
      return false
    }
  }

  async forceResetReorderQueue(sessionId: string): Promise<boolean> {
    if (!sessionId) {
      console.warn('强制重置 ReorderQueue 失败：SessionId 为空')
      return false
    }

    try {
      const connection = await this.ensureConnection()
      return await new Promise<boolean>((resolve) => {
        const resolver = (success: boolean) => {
          window.clearTimeout(timeoutId)
          resolve(success)
        }
        this.pendingReorderQueueResetResolvers.push(resolver)

        const timeoutId = window.setTimeout(() => {
          const index = this.pendingReorderQueueResetResolvers.indexOf(resolver)
          if (index >= 0) {
            this.pendingReorderQueueResetResolvers.splice(index, 1)
          }
          console.warn('强制重置 ReorderQueue 超时')
          resolve(false)
        }, 5000)

        connection.invoke('ForceResetReorderQueue', sessionId).catch((error) => {
          console.error('通过 StreamingHub 强制重置 ReorderQueue 失败:', error)
          const index = this.pendingReorderQueueResetResolvers.indexOf(resolver)
          if (index >= 0) {
            this.pendingReorderQueueResetResolvers.splice(index, 1)
          }
          window.clearTimeout(timeoutId)
          resolve(false)
        })
      })
    } catch (error) {
      console.error('建立 StreamingHub 连接失败，无法强制重置 ReorderQueue:', error)
      return false
    }
  }

  async disconnect(): Promise<void> {
    if (this.connection) {
      try {
        await this.connection.stop()
      } catch (error) {
        console.warn('断开 StreamingHub 连接时出错（可忽略）:', (error as Error).message)
      }
    }

    this.connection = null
    this.connectingPromise = null
    this.pendingKeyframeResolvers.splice(0, this.pendingKeyframeResolvers.length)
    this.pendingReorderQueueResetResolvers.splice(0, this.pendingReorderQueueResetResolvers.length)
  }

  /**
   * 获取待处理的 ICE Restart Offer
   */
  async getIceRestartOffer(sessionId: string): Promise<string | null> {
    if (!sessionId) {
      console.warn('获取 ICE Restart Offer 失败：SessionId 为空')
      return null
    }

    try {
      const connection = await this.ensureConnection()
      const offer = await connection.invoke<string | null>('GetIceRestartOffer', sessionId)
      return offer
    } catch (error) {
      console.error('获取 ICE Restart Offer 失败:', error)
      return null
    }
  }

  /**
   * 处理 ICE Restart（触发后端重新协商）
   */
  async handleIceRestart(sessionId: string): Promise<boolean> {
    if (!sessionId) {
      console.warn('处理 ICE Restart 失败：SessionId 为空')
      return false
    }

    try {
      const connection = await this.ensureConnection()
      await connection.invoke('HandleIceRestart', sessionId)
      return true
    } catch (error) {
      console.error('处理 ICE Restart 失败:', error)
      return false
    }
  }
}

export const streamingHubService = new StreamingHubService()


