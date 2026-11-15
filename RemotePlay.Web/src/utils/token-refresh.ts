import { getRefreshToken, saveAuthTokens } from '@/utils/auth-storage'

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || '/api'

export interface RefreshedAuthTokens {
  token: string
  refreshToken: string
  expiresAt?: string
  refreshExpiresAt?: string
  username?: string
  email?: string
}

type RefreshListener = (tokens: RefreshedAuthTokens) => void

let refreshPromise: Promise<RefreshedAuthTokens | null> | null = null
const listeners = new Set<RefreshListener>()

export function onAuthTokensRefreshed(listener: RefreshListener): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

function notifyListeners(tokens: RefreshedAuthTokens) {
  listeners.forEach((listener) => {
    try {
      listener(tokens)
    } catch (error) {
      console.warn('auth refresh listener failed', error)
    }
  })
}

/**
 * 使用 refresh token 换取新的访问令牌。并发调用会合并为一次请求。
 */
export async function refreshAccessToken(): Promise<RefreshedAuthTokens | null> {
  if (refreshPromise) {
    return refreshPromise
  }

  refreshPromise = (async () => {
    const refreshToken = getRefreshToken()
    if (!refreshToken) {
      return null
    }

    try {
      const response = await fetch(`${API_BASE_URL}/auth/refresh`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({ refreshToken }),
      })

      const text = await response.text()
      if (!response.ok || !text) {
        return null
      }

      const data = JSON.parse(text)
      const payload = data?.data || data?.result || data
      const token = payload?.token
      const nextRefreshToken = payload?.refreshToken
      if (!token || !nextRefreshToken) {
        return null
      }

      const tokens: RefreshedAuthTokens = {
        token,
        refreshToken: nextRefreshToken,
        expiresAt: payload.expiresAt,
        refreshExpiresAt: payload.refreshExpiresAt,
        username: payload.username,
        email: payload.email,
      }

      saveAuthTokens(tokens)
      notifyListeners(tokens)
      return tokens
    } catch (error) {
      console.warn('刷新登录状态失败', error)
      return null
    } finally {
      refreshPromise = null
    }
  })()

  return refreshPromise
}
