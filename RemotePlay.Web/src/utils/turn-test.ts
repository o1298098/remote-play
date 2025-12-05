/**
 * TURN 服务器测试工具
 * 用于测试 TURN 服务器的延迟、可用性和连接质量
 */

export interface TurnServerTestResult {
  url: string
  status: 'success' | 'failed' | 'timeout'
  latency?: number // 毫秒
  bandwidth?: number // Mbps
  downloadSpeed?: number // Mbps
  uploadSpeed?: number // Mbps
  errorMessage?: string
  type?: 'relay' | 'srflx' | 'host'
}

export interface TurnServerConfig {
  url?: string | null
  username?: string | null
  credential?: string | null
}

/**
 * 测试单个 TURN 服务器的速度和可用性
 * @param server TURN 服务器配置
 * @param timeout 超时时间（毫秒）
 */
export async function testTurnServer(
  server: TurnServerConfig,
  timeout: number = 10000
): Promise<TurnServerTestResult> {
  const startTime = performance.now()
  
  // 检查 URL 是否有效
  if (!server.url || server.url.trim() === '') {
    return {
      url: server.url || 'unknown',
      status: 'failed',
      errorMessage: 'TURN 服务器 URL 为空',
    }
  }
  
  try {
    // 创建 RTCPeerConnection 配置
    const config: RTCConfiguration = {
      iceServers: [
        {
          urls: server.url,
          username: server.username || undefined,
          credential: server.credential || undefined,
        },
      ],
      iceTransportPolicy: 'relay', // 强制使用 TURN 中继
    }

    // 创建 PeerConnection
    const pc1 = new RTCPeerConnection(config)
    const pc2 = new RTCPeerConnection({ iceServers: [] })

    let testComplete = false
    let candidateFound = false
    let candidateType: 'relay' | 'srflx' | 'host' | undefined

    // 创建超时 Promise
    const timeoutPromise = new Promise<TurnServerTestResult>((_, reject) => {
      setTimeout(() => {
        if (!testComplete) {
          reject(new Error('测试超时'))
        }
      }, timeout)
    })

    // 测试 Promise
    const testPromise = new Promise<TurnServerTestResult>((resolve, reject) => {
      // 监听 ICE candidate
      pc1.onicecandidate = (event) => {
        if (event.candidate) {
          const candidate = event.candidate.candidate
          
          // 检查是否是 relay（TURN）候选
          if (candidate.includes('typ relay')) {
            candidateFound = true
            candidateType = 'relay'
            const latency = performance.now() - startTime
            
            testComplete = true
            resolve({
              url: server.url || 'unknown',
              status: 'success',
              latency: Math.round(latency),
              type: candidateType,
            })
          } else if (candidate.includes('typ srflx') && !candidateFound) {
            // STUN 服务器返回的候选
            candidateType = 'srflx'
          } else if (candidate.includes('typ host') && !candidateFound) {
            candidateType = 'host'
          }
        } else if (event.candidate === null && !candidateFound) {
          // ICE gathering 完成但没有找到 relay 候选
          testComplete = true
          resolve({
            url: server.url || 'unknown',
            status: 'failed',
            errorMessage: 'TURN 服务器无法建立中继连接',
            type: candidateType,
          })
        }
      }

      // 监听连接状态
      pc1.oniceconnectionstatechange = () => {
        if (pc1.iceConnectionState === 'failed' && !testComplete) {
          testComplete = true
          reject(new Error('ICE 连接失败'))
        }
      }

      // 创建数据通道以触发 ICE gathering
      pc1.createDataChannel('test')

      // 创建并设置 offer
      pc1.createOffer()
        .then((offer) => pc1.setLocalDescription(offer))
        .catch(reject)
    })

    // 使用 Promise.race 处理超时
    const result = await Promise.race([testPromise, timeoutPromise])
    
    // 清理
    pc1.close()
    pc2.close()
    
    return result
  } catch (error) {
    const latency = performance.now() - startTime
    
    if (error instanceof Error) {
      if (error.message === '测试超时') {
        return {
          url: server.url,
          status: 'timeout',
          errorMessage: `测试超时 (${timeout}ms)`,
        }
      }
      return {
        url: server.url,
        status: 'failed',
        latency: Math.round(latency),
        errorMessage: error.message,
      }
    }
    
    return {
      url: server.url,
      status: 'failed',
      latency: Math.round(latency),
      errorMessage: '未知错误',
    }
  }
}

/**
 * 测试多个 TURN 服务器
 * @param servers TURN 服务器列表
 * @param timeout 每个服务器的超时时间（毫秒）
 */
export async function testTurnServers(
  servers: TurnServerConfig[],
  timeout: number = 10000
): Promise<TurnServerTestResult[]> {
  if (!servers || servers.length === 0) {
    return []
  }

  // 并行测试所有服务器
  const promises = servers.map((server) => testTurnServer(server, timeout))
  const results = await Promise.all(promises)
  
  return results
}

/**
 * 快速测试 TURN 服务器（使用较短的超时）
 */
export async function quickTestTurnServers(
  servers: TurnServerConfig[]
): Promise<TurnServerTestResult[]> {
  return testTurnServers(servers, 5000) // 5秒超时
}

/**
 * 获取最佳 TURN 服务器（延迟最低的成功服务器）
 */
export function getBestTurnServer(
  results: TurnServerTestResult[]
): TurnServerTestResult | null {
  const successResults = results.filter(
    (r) => r.status === 'success' && r.latency !== undefined
  )
  
  if (successResults.length === 0) {
    return null
  }
  
  // 按延迟排序，返回最低的
  return successResults.sort((a, b) => (a.latency || 0) - (b.latency || 0))[0]
}

/**
 * 测试 TURN 服务器的带宽（吞吐量）
 * @param server TURN 服务器配置
 * @param duration 测试持续时间（秒）
 * @param timeout 超时时间（毫秒）
 */
export async function testTurnServerBandwidth(
  server: TurnServerConfig,
  duration: number = 5,
  timeout: number = 30000
): Promise<TurnServerTestResult> {
  const startTime = performance.now()
  
  // 检查 URL 是否有效
  if (!server.url || server.url.trim() === '') {
    return {
      url: server.url || 'unknown',
      status: 'failed',
      errorMessage: 'TURN 服务器 URL 为空',
    }
  }
  
  try {
    // 创建 RTCPeerConnection 配置
    const config: RTCConfiguration = {
      iceServers: [
        {
          urls: server.url,
          username: server.username || undefined,
          credential: server.credential || undefined,
        },
      ],
      iceTransportPolicy: 'relay', // 强制使用 TURN 中继
    }

    const pc1 = new RTCPeerConnection(config)
    const pc2 = new RTCPeerConnection(config)

    let testComplete = false
    let candidateType: 'relay' | 'srflx' | 'host' | undefined
    let dataChannel: RTCDataChannel | null = null
    
    // 用于带宽测试的变量
    let bytesSent = 0
    let bytesReceived = 0
    let testStartTime = 0
    let connectionEstablished = false
    let connectionEstablishedTime = 0 // 记录连接建立的时间

    // 创建超时 Promise
    const timeoutPromise = new Promise<TurnServerTestResult>((_, reject) => {
      setTimeout(() => {
        if (!testComplete) {
          reject(new Error('测试超时'))
        }
      }, timeout)
    })

    // 测试 Promise
    const testPromise = new Promise<TurnServerTestResult>((resolve, reject) => {
      // 监听 ICE candidate
      pc1.onicecandidate = (event) => {
        if (event.candidate) {
          const candidate = event.candidate.candidate
          
          if (candidate.includes('typ relay')) {
            candidateType = 'relay'
          } else if (candidate.includes('typ srflx') && !candidateType) {
            candidateType = 'srflx'
          } else if (candidate.includes('typ host') && !candidateType) {
            candidateType = 'host'
          }
          
          pc2.addIceCandidate(event.candidate).catch(console.error)
        }
      }

      pc2.onicecandidate = (event) => {
        if (event.candidate) {
          pc1.addIceCandidate(event.candidate).catch(console.error)
        }
      }

      // 监听连接状态
      pc1.oniceconnectionstatechange = () => {
        if (pc1.iceConnectionState === 'connected' && !connectionEstablished) {
          connectionEstablished = true
          connectionEstablishedTime = performance.now() // 记录连接建立时间
          testStartTime = performance.now()
          console.log('WebRTC 连接已建立，开始带宽测试...')
          
          // 连接建立后开始发送数据
          if (dataChannel && dataChannel.readyState === 'open') {
            startBandwidthTest()
          }
        } else if (pc1.iceConnectionState === 'failed' && !testComplete) {
          testComplete = true
          reject(new Error('ICE 连接失败'))
        }
      }

      // 创建数据通道用于带宽测试
      dataChannel = pc1.createDataChannel('bandwidth-test', {
        ordered: false, // 无序传输以提高速度
        maxRetransmits: 0, // 不重传
      })

      // 准备测试数据（64KB 块）
      const chunkSize = 64 * 1024
      const testData = new ArrayBuffer(chunkSize)
      const dataView = new Uint8Array(testData)
      for (let i = 0; i < dataView.length; i++) {
        dataView[i] = i % 256
      }

      let sendInterval: any = null

      const startBandwidthTest = () => {
        console.log('开始发送数据...')
        
        // 持续发送数据
        sendInterval = setInterval(() => {
          if (dataChannel && dataChannel.readyState === 'open') {
            try {
              // 检查缓冲区，避免过度发送
              if (dataChannel.bufferedAmount < chunkSize * 10) {
                dataChannel.send(testData)
                bytesSent += chunkSize
              }
            } catch (e) {
              console.error('发送数据失败:', e)
            }
          }
        }, 10) // 每 10ms 发送一次

        // 持续时间后停止测试
        setTimeout(() => {
          clearInterval(sendInterval)
          
          // 等待一小段时间确保所有数据都被接收
          setTimeout(() => {
            const testDuration = (performance.now() - testStartTime) / 1000
            const uploadSpeed = (bytesSent * 8) / (testDuration * 1000000) // Mbps
            const downloadSpeed = (bytesReceived * 8) / (testDuration * 1000000) // Mbps
            // 使用连接建立的时间作为真正的延迟（从开始到连接建立）
            const latency = connectionEstablishedTime > 0 
              ? Math.round(connectionEstablishedTime - startTime) 
              : Math.round(performance.now() - startTime)
            
            testComplete = true
            resolve({
              url: server.url || 'unknown',
              status: 'success',
              latency,
              uploadSpeed: Math.round(uploadSpeed * 100) / 100,
              downloadSpeed: Math.round(downloadSpeed * 100) / 100,
              bandwidth: Math.round(((uploadSpeed + downloadSpeed) / 2) * 100) / 100,
              type: candidateType,
            })
          }, 1000)
        }, duration * 1000)
      }

      dataChannel.onopen = () => {
        console.log('数据通道已打开')
        if (connectionEstablished) {
          startBandwidthTest()
        }
      }

      dataChannel.onerror = (error) => {
        console.error('数据通道错误:', error)
      }

      // 接收端监听数据
      pc2.ondatachannel = (event) => {
        const receiveChannel = event.channel
        
        receiveChannel.onmessage = (msgEvent) => {
          if (msgEvent.data instanceof ArrayBuffer) {
            bytesReceived += msgEvent.data.byteLength
          }
        }
        
        receiveChannel.onerror = (error) => {
          console.error('接收通道错误:', error)
        }
      }

      // 创建并交换 offer/answer
      pc1.createOffer()
        .then((offer) => {
          return pc1.setLocalDescription(offer).then(() => offer)
        })
        .then((offer) => {
          return pc2.setRemoteDescription(offer)
        })
        .then(() => {
          return pc2.createAnswer()
        })
        .then((answer) => {
          return pc2.setLocalDescription(answer).then(() => answer)
        })
        .then((answer) => {
          return pc1.setRemoteDescription(answer)
        })
        .catch(reject)
    })

    // 使用 Promise.race 处理超时
    const result = await Promise.race([testPromise, timeoutPromise])
    
    // 清理
    pc1.close()
    pc2.close()
    
    return result
  } catch (error) {
    const latency = performance.now() - startTime
    
    if (error instanceof Error) {
      if (error.message === '测试超时') {
        return {
          url: server.url,
          status: 'timeout',
          errorMessage: `带宽测试超时 (${timeout}ms)`,
        }
      }
      return {
        url: server.url,
        status: 'failed',
        latency: Math.round(latency),
        errorMessage: error.message,
      }
    }
    
    return {
      url: server.url,
      status: 'failed',
      latency: Math.round(latency),
      errorMessage: '未知错误',
    }
  }
}

/**
 * 测试多个 TURN 服务器的带宽
 * @param servers TURN 服务器列表
 * @param duration 每个服务器的测试持续时间（秒）
 * @param timeout 每个服务器的超时时间（毫秒）
 */
export async function testTurnServersBandwidth(
  servers: TurnServerConfig[],
  duration: number = 5,
  timeout: number = 30000
): Promise<TurnServerTestResult[]> {
  if (!servers || servers.length === 0) {
    return []
  }

  // 串行测试以避免带宽竞争（并行测试会影响结果准确性）
  const results: TurnServerTestResult[] = []
  for (const server of servers) {
    const result = await testTurnServerBandwidth(server, duration, timeout)
    results.push(result)
  }
  
  return results
}

/**
 * 格式化测试结果为人类可读的文本
 */
export function formatTestResult(result: TurnServerTestResult): string {
  if (result.status === 'success') {
    let text = `✓ 成功 - 延迟: ${result.latency}ms`
    if (result.bandwidth) {
      text += ` | 带宽: ${result.bandwidth} Mbps`
    }
    if (result.type) {
      text += ` (${result.type})`
    }
    return text
  } else if (result.status === 'timeout') {
    return `✗ 超时 - ${result.errorMessage}`
  } else {
    return `✗ 失败 - ${result.errorMessage || '未知错误'}`
  }
}

