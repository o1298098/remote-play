import type { TurnServerConfig } from '@/service/streaming.service'

export interface TurnServerTestResult {
  success: boolean
  server: TurnServerConfig
  error?: string
  connectionType?: 'relay' | 'host' | 'srflx'
  candidateType?: string
  latency?: number
}

/**
 * 测试单个 TURN 服务器是否有效
 * @param server TURN 服务器配置
 * @param timeout 超时时间（毫秒），默认 10 秒
 * @returns 测试结果
 */
export async function testTurnServer(
  server: TurnServerConfig,
  timeout: number = 10000
): Promise<TurnServerTestResult> {
  const result: TurnServerTestResult = {
    success: false,
    server,
  }

  // 验证 URL 格式
  if (!server.url || !server.url.trim()) {
    result.error = 'TURN 服务器 URL 不能为空'
    return result
  }

  // 解析 URL
  let turnUrl: string
  try {
    // 确保 URL 格式正确
    if (server.url.startsWith('turn:') || server.url.startsWith('turns:')) {
      turnUrl = server.url
    } else {
      // 尝试添加 turn: 前缀
      turnUrl = `turn:${server.url}`
    }
  } catch (error) {
    result.error = `无效的 TURN 服务器 URL: ${error instanceof Error ? error.message : '未知错误'}`
    return result
  }

  // 创建 RTCPeerConnection 配置
  const rtcConfig: RTCConfiguration = {
    iceServers: [
      {
        urls: turnUrl,
        username: server.username || undefined,
        credential: server.credential || undefined,
      },
    ],
    iceCandidatePoolSize: 0, // 不预取候选地址
  }

  let pc: RTCPeerConnection | null = null
  const startTime = Date.now()

  try {
    // 创建 RTCPeerConnection
    pc = new RTCPeerConnection(rtcConfig)

    // 设置超时
    let timeoutResolved = false
    const timeoutId = setTimeout(() => {
      timeoutResolved = true
      if (pc) {
        pc.close()
      }
      if (!result.success && !result.error) {
        result.error = '测试超时'
      }
    }, timeout)

    // 监听 ICE 候选地址
    const candidates: RTCIceCandidate[] = []
    let relayCandidateFound = false
    let connectionType: 'relay' | 'host' | 'srflx' | undefined

    pc.onicecandidate = (event) => {
      if (event.candidate) {
        candidates.push(event.candidate)
        
        // 检查是否是 relay 类型的候选地址（TURN 服务器）
        const candidateStr = event.candidate.candidate
        if (candidateStr.includes('typ relay')) {
          relayCandidateFound = true
          connectionType = 'relay'
          result.candidateType = 'relay'
        } else if (candidateStr.includes('typ srflx')) {
          if (!connectionType) {
            connectionType = 'srflx'
            result.candidateType = 'srflx'
          }
        } else if (candidateStr.includes('typ host')) {
          if (!connectionType) {
            connectionType = 'host'
            result.candidateType = 'host'
          }
        }
      } else {
        // 所有候选地址收集完成
        if (!timeoutResolved) {
          clearTimeout(timeoutId)
        }
        
        if (relayCandidateFound && !result.success) {
          result.success = true
          result.connectionType = 'relay'
          const latency = Date.now() - startTime
          result.latency = latency
        } else if (!result.success && !result.error) {
          if (candidates.length > 0) {
            // 有候选地址但不是 relay 类型，说明 TURN 服务器可能不可用
            result.error = '未找到 TURN relay 候选地址，服务器可能不可用'
            result.connectionType = connectionType
          } else {
            result.error = '未收集到任何 ICE 候选地址'
          }
        }
      }
    }

    // 监听 ICE 连接状态
    pc.oniceconnectionstatechange = () => {
      if (pc && !timeoutResolved) {
        const state = pc.iceConnectionState
        if (state === 'failed' || state === 'disconnected') {
          if (!timeoutResolved) {
            clearTimeout(timeoutId)
          }
          if (!result.success && !result.error) {
            result.error = `ICE 连接状态: ${state}`
          }
        }
      }
    }

    // 监听 ICE 收集状态
    pc.onicegatheringstatechange = () => {
      if (pc && pc.iceGatheringState === 'complete' && !timeoutResolved) {
        if (!timeoutResolved) {
          clearTimeout(timeoutId)
        }
        
        // 如果还没有设置结果，检查是否有 relay 候选地址
        if (!result.success && !result.error) {
          if (relayCandidateFound) {
            result.success = true
            result.connectionType = 'relay'
            const latency = Date.now() - startTime
            result.latency = latency
          } else if (candidates.length > 0) {
            result.error = '未找到 TURN relay 候选地址'
            result.connectionType = connectionType
          } else {
            result.error = '未收集到任何 ICE 候选地址'
          }
        }
      }
    }

    // 创建一个数据通道以触发 ICE 收集
    const dataChannel = pc.createDataChannel('test')
    
    // 创建 offer
    const offer = await pc.createOffer({
      offerToReceiveAudio: false,
      offerToReceiveVideo: false,
    })
    
    await pc.setLocalDescription(offer)

    // 等待 ICE 收集完成或超时
    await new Promise<void>((resolve) => {
      const checkInterval = setInterval(() => {
        if (pc) {
          if (pc.iceGatheringState === 'complete' || pc.iceConnectionState === 'failed') {
            clearInterval(checkInterval)
            resolve()
          }
        } else {
          clearInterval(checkInterval)
          resolve()
        }
      }, 100)

      setTimeout(() => {
        clearInterval(checkInterval)
        resolve()
      }, timeout)
    })

    clearTimeout(timeoutId)

    // 如果还没有结果，设置默认结果
    if (!result.success && !result.error) {
      if (relayCandidateFound) {
        result.success = true
        result.connectionType = 'relay'
        const latency = Date.now() - startTime
        result.latency = latency
      } else if (candidates.length > 0) {
        result.error = '未找到 TURN relay 候选地址，但收集到了其他类型的候选地址'
        result.connectionType = connectionType
      } else {
        result.error = '测试超时或未收集到候选地址'
      }
    }
  } catch (error) {
    result.error = error instanceof Error ? error.message : '未知错误'
  } finally {
    // 清理资源
    if (pc) {
      try {
        pc.close()
      } catch (e) {
        // 忽略关闭错误
      }
    }
  }

  return result
}

/**
 * 测试多个 TURN 服务器
 * @param servers TURN 服务器配置数组
 * @param timeout 每个服务器的超时时间（毫秒），默认 10 秒
 * @returns 测试结果数组
 */
export async function testTurnServers(
  servers: TurnServerConfig[],
  timeout: number = 10000
): Promise<TurnServerTestResult[]> {
  const results: TurnServerTestResult[] = []
  
  // 串行测试，避免同时创建太多连接
  for (const server of servers) {
    if (!server.url || !server.url.trim()) {
      results.push({
        success: false,
        server,
        error: 'TURN 服务器 URL 不能为空',
      })
      continue
    }
    
    const result = await testTurnServer(server, timeout)
    results.push(result)
  }
  
  return results
}

