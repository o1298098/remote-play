import { createContext, useContext, useEffect, useState, useRef, ReactNode } from 'react'
import { gamepadService, type GamepadConnectionState, type GamepadInfo, type GamepadInputEvent } from '@/service/gamepad.service'
import { useToast } from '@/hooks/use-toast'

interface GamepadContextType {
  isConnected: boolean
  connectedGamepads: GamepadInfo[]
  hasGamepad: () => boolean
  getGamepad: (index: number) => Gamepad | null
  isEnabled: boolean
  setEnabled: (enabled: boolean) => void
  disconnectGamepad: (index: number) => void
  disconnectGamepadById: (gamepadId: string) => boolean
}

const GamepadContext = createContext<GamepadContextType | undefined>(undefined)

export function GamepadProvider({ children }: { children: ReactNode }) {
  const { toast } = useToast()
  const [connectionState, setConnectionState] = useState<GamepadConnectionState>(() => 
    gamepadService.getConnectionState()
  )
  const [isEnabled, setIsEnabled] = useState<boolean>(() => {
    // 从 localStorage 读取保存的状态
    const saved = localStorage.getItem('gamepad_enabled')
    return saved !== 'false' // 默认为 true
  })
  const previousGamepadCountRef = useRef<number>(0)
  const previousGamepadIdsRef = useRef<Set<string>>(new Set())

  useEffect(() => {
    let isInitialized = false

    // 初始化时获取当前状态（不显示提醒）
    const initialState = gamepadService.getConnectionState()
    setConnectionState(initialState)
    previousGamepadCountRef.current = initialState.connectedGamepads.length
    initialState.connectedGamepads.forEach(g => previousGamepadIdsRef.current.add(g.id))

    // 延迟标记为已初始化，避免初始化时显示提醒
    const initTimer = setTimeout(() => {
      isInitialized = true
    }, 500)

    // 订阅手柄状态变化
    const unsubscribe = gamepadService.onStateChange((state) => {
      // 如果还未初始化完成，只更新状态，不显示提醒
      if (!isInitialized) {
        setConnectionState(state)
        previousGamepadCountRef.current = state.connectedGamepads.length
        previousGamepadIdsRef.current = new Set(state.connectedGamepads.map(g => g.id))
        return
      }

      const currentCount = state.connectedGamepads.length
      const previousIds = previousGamepadIdsRef.current
      const currentIds = new Set(state.connectedGamepads.map(g => g.id))

      // 检测新连接的手柄
      state.connectedGamepads.forEach((gamepad) => {
        if (!previousIds.has(gamepad.id)) {
          // 新手柄连接
          const gamepadName = gamepad.id || '游戏手柄'
          // 简化手柄名称显示（移除一些技术细节）
          const displayName = gamepadName
            .replace(/\(.*?\)/g, '') // 移除括号内容
            .replace(/\s+/g, ' ') // 合并多个空格
            .trim() || '游戏手柄'
          
          toast({
            title: '🎮 手柄已连接',
            description: `${displayName} 已成功连接到电脑`,
            duration: 3000,
          })
          console.log('🎮 手柄已连接:', gamepad.id, '索引:', gamepad.index)
        }
      })

      // 检测断开的手柄
      previousIds.forEach((gamepadId) => {
        if (!currentIds.has(gamepadId)) {
          // 手柄断开
          toast({
            title: '🎮 手柄已断开',
            description: '游戏手柄已从电脑断开连接',
            duration: 3000,
          })
          console.log('🎮 手柄已断开:', gamepadId)
        }
      })

      // 更新状态
      setConnectionState(state)
      previousGamepadCountRef.current = currentCount
      previousGamepadIdsRef.current = currentIds
    })

    return () => {
      clearTimeout(initTimer)
      unsubscribe()
    }
  }, [toast])

  const setEnabled = (enabled: boolean) => {
    setIsEnabled(enabled)
    localStorage.setItem('gamepad_enabled', enabled.toString())
  }

  const value: GamepadContextType = {
    isConnected: connectionState.isConnected,
    connectedGamepads: connectionState.connectedGamepads,
    hasGamepad: () => gamepadService.hasGamepad(),
    getGamepad: (index: number) => gamepadService.getGamepad(index),
    isEnabled,
    setEnabled,
    disconnectGamepad: (index: number) => gamepadService.disconnectGamepad(index),
    disconnectGamepadById: (gamepadId: string) => gamepadService.disconnectGamepadById(gamepadId),
  }

  return (
    <GamepadContext.Provider value={value}>
      {children}
    </GamepadContext.Provider>
  )
}

export function useGamepad() {
  const context = useContext(GamepadContext)
  if (context === undefined) {
    throw new Error('useGamepad must be used within a GamepadProvider')
  }
  return context
}

/**
 * Hook: 监听手柄输入事件
 */
export function useGamepadInput(
  onInput: (event: GamepadInputEvent) => void,
  enabled: boolean = true
) {
  useEffect(() => {
    if (!enabled) {
      return
    }

    const unsubscribe = gamepadService.onInput(onInput)

    return () => {
      unsubscribe()
    }
  }, [onInput, enabled])
}

