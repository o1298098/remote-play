import { createContext, useContext, useState, useEffect, ReactNode } from 'react'
import {
  AUTH_INVALIDATED_EVENT,
  type AuthInvalidationDetail,
  resetInvalidationFlag,
} from '@/utils/auth-invalidation'

export interface User {
  id: string
  email: string
  username?: string
  avatar?: string
  name?: string
  [key: string]: any // 允许扩展其他用户字段
}

interface AuthContextType {
  isAuthenticated: boolean
  user: User | null
  token: string | null
  login: (token: string, userData?: User) => void
  logout: () => void
  updateUser: (userData: Partial<User>) => void
}

const AuthContext = createContext<AuthContextType | undefined>(undefined)

const STORAGE_KEYS = {
  TOKEN: 'auth_token',
  USER: 'user_data',
}

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

  const [token, setToken] = useState<string | null>(
    localStorage.getItem(STORAGE_KEYS.TOKEN)
  )
  const [user, setUser] = useState<User | null>(getStoredUser())

  useEffect(() => {
    resetInvalidationFlag()
  }, [])

  // 同步 token 到 localStorage
  useEffect(() => {
    if (token) {
      localStorage.setItem(STORAGE_KEYS.TOKEN, token)
    } else {
      localStorage.removeItem(STORAGE_KEYS.TOKEN)
    }
  }, [token])

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
  }, [])

  const login = (newToken: string, userData?: User) => {
    setToken(newToken)
    if (userData) {
      setUser(userData)
    } else if (user) {
      // 如果已有用户信息但传入了新 token，保持现有用户信息
      // 或者可以在这里从 API 获取用户信息
    }
  }

  const logout = () => {
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

