import { createContext, useContext, useState, useEffect, useRef, useCallback, ReactNode } from 'react'
import {
  AUTH_INVALIDATED_EVENT,
  type AuthInvalidationDetail,
  resetInvalidationFlag,
} from '@/utils/auth-invalidation'
import {
  STORAGE_KEYS,
  getAccessToken,
  getAccessTokenExpiresAt,
  getRefreshToken,
  saveAuthTokens,
  clearAuthTokens,
} from '@/utils/auth-storage'
import { onAuthTokensRefreshed, refreshAccessToken } from '@/utils/token-refresh'

export interface User {
  id: string
  email: string
  username?: string
  avatar?: string
  name?: string
  [key: string]: any // 允许扩展其他用户字段
}

export interface AuthSessionPayload {
  token: string
  refreshToken?: string | null
  expiresAt?: string | null
  refreshExpiresAt?: string | null
}

interface AuthContextType {
  isAuthenticated: boolean
  user: User | null
  token: string | null
  login: (tokenOrSession: string | AuthSessionPayload, userData?: User) => void
  logout: () => void
  updateUser: (userData: Partial<User>) => void
}

const AuthContext = createContext<AuthContextType | undefined>(undefined)

/** 到期前多久主动续期 */
const REFRESH_AHEAD_MS = 60 * 60 * 1000
const MIN_REFRESH_DELAY_MS = 30 * 1000

export function AuthProvider({ children }: { children: ReactNode }) {
  // 从 localStorage 恢复用户信息
  const getStoredUser = (): User | null => {
    try {
      const stored = localStorage.getItem(STORAGE_KEYS.USER)
      return stored ? JSON.parse(stored) : null
    } catch {
      return null
    }
  }

  const [token, setToken] = useState<string | null>(getAccessToken())
  const [user, setUser] = useState<User | null>(getStoredUser())
  const refreshTimerRef = useRef<number | null>(null)

  const clearRefreshTimer = useCallback(() => {
    if (refreshTimerRef.current !== null) {
      window.clearTimeout(refreshTimerRef.current)
      refreshTimerRef.current = null
    }
  }, [])

  const scheduleProactiveRefresh = useCallback(() => {
    clearRefreshTimer()

    if (!getRefreshToken()) {
      return
    }

    const expiresAtRaw = getAccessTokenExpiresAt()
    let delay = REFRESH_AHEAD_MS

    if (expiresAtRaw) {
      const expiresAtMs = Date.parse(expiresAtRaw)
      if (!Number.isNaN(expiresAtMs)) {
        delay = Math.max(MIN_REFRESH_DELAY_MS, expiresAtMs - Date.now() - REFRESH_AHEAD_MS)
      }
    }

    refreshTimerRef.current = window.setTimeout(async () => {
      const refreshed = await refreshAccessToken()
      if (!refreshed) {
        // refresh 失败时不立刻踢出：等业务请求 401 再处理，避免网络抖动误登出
        scheduleProactiveRefresh()
      }
    }, delay)
  }, [clearRefreshTimer])

  useEffect(() => {
    resetInvalidationFlag()
  }, [])

  // 同步用户信息到 localStorage
  useEffect(() => {
    if (user) {
      localStorage.setItem(STORAGE_KEYS.USER, JSON.stringify(user))
    } else {
      localStorage.removeItem(STORAGE_KEYS.USER)
    }
  }, [user])

  useEffect(() => {
    const handleAuthInvalidated = (event: Event) => {
      const detail = (event as CustomEvent<AuthInvalidationDetail>).detail
      console.warn('接收到全局认证失效事件', detail)
      clearRefreshTimer()
      setToken(null)
      setUser(null)
    }

    window.addEventListener(
      AUTH_INVALIDATED_EVENT,
      handleAuthInvalidated as EventListener
    )

    return () => {
      window.removeEventListener(
        AUTH_INVALIDATED_EVENT,
        handleAuthInvalidated as EventListener
      )
    }
  }, [clearRefreshTimer])

  useEffect(() => {
    const unsubscribe = onAuthTokensRefreshed((tokens) => {
      setToken(tokens.token)
      scheduleProactiveRefresh()
    })
    return unsubscribe
  }, [scheduleProactiveRefresh])

  useEffect(() => {
    if (token && getRefreshToken()) {
      scheduleProactiveRefresh()
    } else {
      clearRefreshTimer()
    }
    return clearRefreshTimer
  }, [token, scheduleProactiveRefresh, clearRefreshTimer])

  const login = (tokenOrSession: string | AuthSessionPayload, userData?: User) => {
    const session: AuthSessionPayload =
      typeof tokenOrSession === 'string'
        ? { token: tokenOrSession }
        : tokenOrSession

    const existingRefresh = getRefreshToken()
    const refreshToken = session.refreshToken || existingRefresh
    if (!refreshToken) {
      // 兼容旧登录响应（无 refreshToken）
      localStorage.setItem(STORAGE_KEYS.TOKEN, session.token)
      setToken(session.token)
    } else {
      saveAuthTokens({
        token: session.token,
        refreshToken,
        expiresAt: session.expiresAt,
        refreshExpiresAt: session.refreshExpiresAt,
      })
      setToken(session.token)
    }

    if (userData) {
      setUser(userData)
    }
  }

  const logout = () => {
    clearRefreshTimer()
    clearAuthTokens()
    setToken(null)
    setUser(null)
  }

  const updateUser = (userData: Partial<User>) => {
    if (user) {
      setUser({ ...user, ...userData })
    }
  }

  return (
    <AuthContext.Provider
      value={{
        isAuthenticated: !!token,
        user,
        token,
        login,
        logout,
        updateUser,
      }}
    >
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth() {
  const context = useContext(AuthContext)
  if (context === undefined) {
    throw new Error('useAuth must be used within an AuthProvider')
  }
  return context
}
