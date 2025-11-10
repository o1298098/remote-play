/**
 * 优化 SDP 以降低延迟
 */
export function optimizeSdpForLowLatency(
  sdp: string,
  options?: {
    preferLanCandidates?: boolean
  }
): string {
  try {
    if (!sdp || typeof sdp !== 'string' || sdp.length < 10) {
      return sdp
    }

    // 检查是否已经包含优化标记（避免重复添加）
    if (sdp.includes('a=x-google-flag:low-latency') && sdp.includes('a=minBufferedPlaybackTime')) {
      return sdp // 已经优化过了
    }

    const lines = sdp.split(/\r?\n/)
    const optimizedLines: string[] = []
    let foundVideoMedia = false
    let foundAudioMedia = false
    let videoOptimized = false
    let audioOptimized = false

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i]
      optimizedLines.push(line)

      // 检测媒体行
      if (line.trim().startsWith('m=video ')) {
        foundVideoMedia = true
        foundAudioMedia = false
        videoOptimized = false
      } else if (line.trim().startsWith('m=audio ')) {
        foundAudioMedia = true
        foundVideoMedia = false
        audioOptimized = false
      } else if (line.trim().startsWith('m=')) {
        // 其他媒体类型，重置状态
        foundVideoMedia = false
        foundAudioMedia = false
      }

      // 在视频媒体部分的第一个属性行后添加优化（确保格式正确）
      if (
        foundVideoMedia &&
        !videoOptimized &&
        line.trim().startsWith('a=') &&
        !line.trim().startsWith('a=rtcp:') &&
        line.trim().length > 2
      ) {
        // 只在第一个有效的属性行后添加
        if (!sdp.includes('a=x-google-flag:low-latency')) {
          optimizedLines.push('a=x-google-flag:low-latency')
        }
        if (!sdp.includes('a=minBufferedPlaybackTime')) {
          optimizedLines.push('a=minBufferedPlaybackTime:0')
        }
        videoOptimized = true
      }

      // 在音频媒体部分的第一个属性行后添加优化
      if (
        foundAudioMedia &&
        !audioOptimized &&
        line.trim().startsWith('a=') &&
        !line.trim().startsWith('a=rtcp:') &&
        line.trim().length > 2
      ) {
        if (!sdp.includes('a=minBufferedPlaybackTime')) {
          optimizedLines.push('a=minBufferedPlaybackTime:0')
        }
        audioOptimized = true
      }
    }

    const preferLan = options?.preferLanCandidates ?? true
    const finalLines = preferLan ? reorderCandidatesForLan(optimizedLines) : optimizedLines
    const result = finalLines.join('\r\n')

    // 验证结果
    if (!result || result.length < sdp.length * 0.5) {
      // 如果结果明显短于原始 SDP，可能出错了
      return sdp
    }

    // 确保 SDP 基本结构完整
    if (!result.includes('v=0') || !result.includes('m=')) {
      return sdp
    }

    return result
  } catch (error) {
    console.error('SDP 优化错误:', error)
    return sdp // 出错时返回原始 SDP
  }
}

/**
 * 优化视频元素以降低延迟（零缓冲模式）
 */
export function optimizeVideoForLowLatency(video: HTMLVideoElement): () => void {
  // 零缓冲初始化设置
  video.preload = 'none' // 禁用预加载
  video.autoplay = true // 自动播放
  video.playsInline = true // 内联播放

  // 监控缓冲并主动减少延迟（零缓冲策略）
  let lastBufferCheck = 0
  const bufferCheckInterval = 50 // 每50ms检查一次（更频繁）
  const maxBufferTime = 0.05 // 最大允许缓冲时间：50ms（接近零缓冲）

  const checkBufferAndOptimize = () => {
    if (video.buffered && video.buffered.length > 0) {
      const bufferedEnd = video.buffered.end(video.buffered.length - 1)
      const currentTime = video.currentTime
      const bufferAhead = bufferedEnd - currentTime

      // ✅ 零缓冲策略：如果缓冲超过50ms，立即跳转以减少延迟
      if (bufferAhead > maxBufferTime && currentTime > 0.01) {
        // 跳转到缓冲末尾，只保留最小缓冲（10ms）
        const targetTime = bufferedEnd - 0.01 // 只保留10ms缓冲
        if (targetTime > currentTime && targetTime < bufferedEnd) {
          try {
            video.currentTime = targetTime
            if (lastBufferCheck % 20 === 0) {
              console.log(
                `⚡ 零缓冲优化: ${(bufferAhead * 1000).toFixed(0)}ms -> 10ms`
              )
            }
          } catch (e) {
            // 忽略跳转错误（可能因为缓冲太小）
          }
        }
      }

      // 定期记录缓冲状态（每2秒一次）
      if (lastBufferCheck % 40 === 0) {
        console.log(`📊 视频缓冲: ${(bufferAhead * 1000).toFixed(0)}ms`)
      }
    }
    lastBufferCheck++
  }

  // 启动高频缓冲监控（零缓冲模式）
  const bufferMonitor = setInterval(checkBufferAndOptimize, bufferCheckInterval)

  // 返回清理函数
  return () => {
    clearInterval(bufferMonitor)
  }
}

function reorderCandidatesForLan(lines: string[]): string[] {
  const optimizedLines: string[] = []
  let candidateBuffer: string[] = []
  let collectingCandidates = false

  const flushBuffer = () => {
    if (candidateBuffer.length === 0) return
    candidateBuffer = candidateBuffer.sort((a, b) => scoreCandidate(b) - scoreCandidate(a))
    optimizedLines.push(...candidateBuffer)
    candidateBuffer = []
  }

  for (const line of lines) {
    const trimmed = line.trim()

    if (trimmed.startsWith('m=')) {
      flushBuffer()
      optimizedLines.push(line)
      collectingCandidates = false
      continue
    }

    if (trimmed.startsWith('a=candidate:')) {
      collectingCandidates = true
      candidateBuffer.push(line)
      continue
    }

    if (collectingCandidates && !trimmed.startsWith('a=candidate:')) {
      flushBuffer()
      collectingCandidates = false
    }

    optimizedLines.push(line)
  }

  flushBuffer()
  return optimizedLines
}

function scoreCandidate(candidateLine: string): number {
  const parts = candidateLine.split(/\s+/)
  const protocol = (parts[2] || '').toLowerCase()
  const address = parts[4] || ''
  const component = parts[1] || ''
  const typeIndex = parts.findIndex((part) => part === 'typ')
  const candidateType = typeIndex >= 0 ? (parts[typeIndex + 1] || '').toLowerCase() : ''

  let score = 0

  if (candidateType === 'host' && isPrivateAddress(address)) {
    score += 400
  } else if (candidateType === 'host') {
    score += 320
  } else if (candidateType === 'srflx') {
    score += 200
  } else if (candidateType === 'prflx') {
    score += 150
  } else if (candidateType === 'relay') {
    score += 50
  }

  if (protocol === 'udp') {
    score += 40
  }

  if (component === '1') {
    score += 10
  }

  return score
}

function isPrivateAddress(address: string): boolean {
  if (!address) {
    return false
  }

  // IPv6 链路本地或本地前缀
  if (address.includes(':')) {
    const lower = address.toLowerCase()
    return (
      lower.startsWith('fe80') || // 链路本地
      lower.startsWith('fd') || // ULA
      lower.startsWith('fc')
    )
  }

  // IPv4
  if (address.startsWith('10.')) return true
  if (address.startsWith('192.168.')) return true
  if (address.startsWith('169.254.')) return true

  if (address.startsWith('172.')) {
    const second = parseInt(address.split('.')[1], 10)
    if (!Number.isNaN(second) && second >= 16 && second <= 31) {
      return true
    }
  }

  return false
}

