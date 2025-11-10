import { useEffect, useRef } from 'react'
import { useToast } from '@/hooks/use-toast'
import * as signalR from '@microsoft/signalr'
import type { UserDevice } from '@/service/playstation.service'
import { invalidateAuth, isAuthErrorMessage } from '@/utils/auth-invalidation'

// 模块级别的初始化标记，防止 StrictMode 下的重复连接
let signalRInitialized = false
// 模块级别的连接引用，用于跟踪当前活动的连接
let globalConnectionRef: signalR.HubConnection | null = null
// 连接建立时间，用于判断是否是 StrictMode 导致的过早清理
let connectionStartTime: number | null = null

interface UseDeviceStatusOptions {
  onDevicesUpdate: (devices: UserDevice[]) => void
  onStatusUpdate: (devices: UserDevice[]) => void
}

export function useDeviceStatus({ onDevicesUpdate, onStatusUpdate }: UseDeviceStatusOptions) {
  const { toast } = useToast()
  const hubConnectionRef = useRef<signalR.HubConnection | null>(null)
  const hasForcedLogoutRef = useRef(false)

  useEffect(() => {
    hasForcedLogoutRef.current = false

    // 防止 StrictMode 下的重复初始化
    if (signalRInitialized) {
      console.log('SignalR连接已初始化，跳过重复创建（StrictMode保护）')
      return
    }

    // 标记为已初始化
    signalRInitialized = true

    // 如果已有连接，先断开（防止重复连接）
    if (hubConnectionRef.current) {
      const currentState = hubConnectionRef.current.state
      console.log('检测到已有连接，先断开旧连接，状态:', currentState)
      if (currentState !== signalR.HubConnectionState.Disconnected) {
        hubConnectionRef.current.stop().catch((error) => {
          console.error('断开旧连接时出错:', error)
        })
      }
      hubConnectionRef.current = null
    }

    // 建立SignalR连接以接收设备状态更新
    const apiBaseUrl = import.meta.env.VITE_API_BASE_URL || '/api'
    const baseUrl = apiBaseUrl.replace(/\/api$/, '') || ''
    const hubUrl = `${baseUrl}/hubs/device-status`
    
    const token = localStorage.getItem('auth_token')
    
    // 检查是否有token
    if (!token) {
      console.warn('没有找到认证token，SignalR连接可能失败')
      signalRInitialized = false
      toast({
        title: '未登录',
        description: '请先登录后再使用设备状态更新功能',
        variant: 'destructive',
      })
      hasForcedLogoutRef.current = true
      invalidateAuth('缺少认证token，无法建立设备状态连接')
      return
    }
    
    console.log('准备建立SignalR连接:', {
      url: hubUrl,
      hasToken: !!token,
      tokenLength: token.length,
    })
    
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, {
        accessTokenFactory: () => {
          const currentToken = localStorage.getItem('auth_token')
          console.log('SignalR请求token:', { hasToken: !!currentToken })
          return currentToken || ''
        },
        transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.LongPolling,
      })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          const retryCount = retryContext.previousRetryCount || 0
          return Math.min(1000 * Math.pow(2, retryCount), 10000)
        },
      })
      .build()

    const forceLogout = (reason: string) => {
      if (hasForcedLogoutRef.current) {
        return
      }

      hasForcedLogoutRef.current = true
      console.warn('检测到认证错误，准备清理凭据并跳转登录:', reason)

      connection
        .stop()
        .catch((stopError) => console.warn('清理连接时出错（可忽略）:', stopError))

      invalidateAuth(reason)
    }

    // 立即保存连接引用
    hubConnectionRef.current = connection
    globalConnectionRef = connection

    // 监听已注册设备状态事件
    connection.on('RegisteredDevicesStatus', (devices: UserDevice[]) => {
      console.log('收到已注册设备状态:', devices)
      onDevicesUpdate(devices)
    })

    // 监听设备状态更新事件
    connection.on('DeviceStatusUpdated', (updatedDevices: UserDevice[]) => {
      console.log('收到设备状态更新:', {
        count: updatedDevices?.length || 0,
        devices: updatedDevices,
        connectionState: connection.state,
        connectionId: connection.connectionId,
      })
      
      if (!updatedDevices || updatedDevices.length === 0) {
        console.warn('收到设备状态更新，但设备列表为空')
        return
      }
      
      onStatusUpdate(updatedDevices)
    })

    // 监听错误事件
    connection.on('Error', (error: { message?: string }) => {
      console.error('SignalR Hub错误:', error)
      const errorMessage = error?.message || '未知错误'
      toast({
        title: '设备状态更新错误',
        description: errorMessage,
        variant: 'destructive',
      })

      if (isAuthErrorMessage(errorMessage)) {
        forceLogout(errorMessage)
      }
    })

    // 连接事件处理
    connection.onclose((error) => {
      if (error) {
        console.error('SignalR连接关闭（有错误）:', {
          error,
          message: error?.message,
          stack: error?.stack,
          connectionId: connection.connectionId,
          state: connection.state,
        })
      } else {
        console.log('SignalR连接已关闭（正常关闭）', {
          connectionId: connection.connectionId,
          state: connection.state,
          isGlobalConnection: globalConnectionRef === connection,
        })
      }
      
      if (hubConnectionRef.current === connection) {
        hubConnectionRef.current = null
      }
      if (globalConnectionRef === connection) {
        globalConnectionRef = null
      }
    })

    connection.onreconnecting((error) => {
      console.log('SignalR正在重连...', error)
    })

    connection.onreconnected((connectionId) => {
      console.log('SignalR重连成功:', connectionId)
      console.log('重连后验证事件监听器状态')
    })

    // 记录连接开始时间
    connectionStartTime = Date.now()
    
    // 启动连接
    connection.start()
      .then(() => {
        console.log('SignalR设备状态Hub连接成功', {
          connectionId: connection.connectionId,
          state: connection.state,
          url: hubUrl,
          elapsed: Date.now() - (connectionStartTime || 0),
        })
        
        console.log('SignalR连接已建立，等待接收设备状态更新', {
          connectionId: connection.connectionId,
          state: connection.state,
          url: hubUrl,
        })
        
        // 添加全局测试函数（用于调试）
        // @ts-ignore
        window.testSignalRConnection = () => {
          console.log('测试 SignalR 连接:', {
            connectionId: connection.connectionId,
            state: connection.state,
            url: hubUrl,
          })
          connection.invoke('GetRegisteredDevicesStatus')
            .then(() => console.log('手动获取设备状态请求已发送'))
            .catch((err) => console.error('手动获取设备状态失败:', err))
        }
        
        console.log('提示：可以在控制台运行 window.testSignalRConnection() 来测试连接')
      })
      .catch((error) => {
        signalRInitialized = false
        
        console.error('SignalR设备状态Hub连接失败:', {
          error,
          message: error?.message,
          stack: error?.stack,
          url: hubUrl,
          hasToken: !!token,
          connectionState: connection.state,
        })
        
        if (hubConnectionRef.current === connection) {
          hubConnectionRef.current = null
        }
        
        let errorMessage = '无法连接到设备状态更新服务，将使用手动刷新'
        if (error?.message) {
          if (error.message.includes('401') || error.message.includes('Unauthorized')) {
            errorMessage = '认证失败，请重新登录'
          } else if (error.message.includes('404') || error.message.includes('Not Found')) {
            errorMessage = '设备状态服务未找到，请检查后端服务是否运行'
      } else if (error.message.includes('Failed to start') || error.message.includes('stopped during negotiation')) {
            errorMessage = '连接在协商期间被中断，可能是页面刷新或重复连接导致'
          } else {
            errorMessage = `连接失败: ${error.message}`
          }
        }
        
        toast({
          title: '连接失败',
          description: errorMessage,
          variant: 'destructive',
        })

        if (isAuthErrorMessage(errorMessage)) {
          forceLogout(errorMessage)
        }
      })

    // 清理函数
    return () => {
      const currentConnection = connection
      
      if (currentConnection && globalConnectionRef === currentConnection) {
        const connectionState = currentConnection.state
        const connectionAge = connectionStartTime ? Date.now() - connectionStartTime : Infinity
        
        console.log('清理函数被调用，连接状态:', connectionState, {
          isGlobalConnection: globalConnectionRef === currentConnection,
          isRefConnection: hubConnectionRef.current === currentConnection,
          connectionAge,
        })
        
        // 如果连接建立时间很短（小于5秒），很可能是StrictMode导致的重复清理
        // 在这种情况下，完全忽略清理操作，不执行任何断开操作
        if (connectionAge < 5000) {
          console.log('清理函数：连接建立时间过短，可能是StrictMode导致的重复清理，忽略清理操作', {
            connectionAge,
            state: connectionState,
          })
          return // 直接返回，不执行任何断开操作
        }
        
        // 如果连接正在协商中，等待协商完成后再断开
        if (connectionState === signalR.HubConnectionState.Connecting) {
          console.log('清理函数：连接正在协商中，等待协商完成后再断开')
          
          const maxWaitTime = 2000
          const checkInterval = 100
          let elapsed = 0
          
          const checkConnection = setInterval(() => {
            elapsed += checkInterval
            
            if (globalConnectionRef !== currentConnection) {
              console.log('清理函数：连接已被替换，取消清理')
              clearInterval(checkConnection)
              return
            }
            
            if (currentConnection.state !== signalR.HubConnectionState.Connecting) {
              clearInterval(checkConnection)
              disconnectConnection(currentConnection)
            } else if (elapsed >= maxWaitTime) {
              clearInterval(checkConnection)
              console.log('清理函数：等待超时，强制断开连接')
              disconnectConnection(currentConnection)
            }
          }, checkInterval)
        } else {
          // 连接不在协商状态，直接断开
          disconnectConnection(currentConnection)
        }
      } else {
        // 不是当前活动的连接，只记录日志
        console.log('清理函数：不是当前活动的连接，忽略清理', {
          hasCurrentConnection: !!currentConnection,
          isGlobalConnection: globalConnectionRef === currentConnection,
        })
      }
    }
  }, [onDevicesUpdate, onStatusUpdate, toast])

  // 断开连接的辅助函数
  function disconnectConnection(connection: signalR.HubConnection) {
    const state = connection.state
    console.log('断开SignalR连接，当前状态:', state)
    
    if (state === signalR.HubConnectionState.Disconnected) {
      console.log('连接已断开，无需操作')
      return
    }
    
    // 再次检查连接建立时间，确保不是StrictMode导致的过早断开
    const connectionAge = connectionStartTime ? Date.now() - connectionStartTime : Infinity
    if (connectionAge < 5000) {
      console.log('断开函数：连接建立时间过短，可能是StrictMode导致的重复清理，取消断开操作', {
        connectionAge,
        state,
      })
      return // 取消断开操作
    }
    
    connection.stop().catch((error) => {
      console.error('断开SignalR连接时出错:', error)
    })
    
    // 清除引用
    if (hubConnectionRef.current === connection) {
      hubConnectionRef.current = null
    }
    if (globalConnectionRef === connection) {
      globalConnectionRef = null
      signalRInitialized = false
    }
  }
}

