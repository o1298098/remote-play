const STORAGE_KEYS = {
  TOKEN: 'auth_token',
  REFRESH_TOKEN: 'auth_refresh_token',
  EXPIRES_AT: 'auth_expires_at',
  REFRESH_EXPIRES_AT: 'auth_refresh_expires_at',
  USER: 'user_data',
} as const

export interface StoredAuthTokens {
  token: string
  refreshToken: string
  expiresAt?: string | null
  refreshExpiresAt?: string | null
}

function safeGet(key: string): string | null {
  try {
    return localStorage.getItem(key)
  } catch {
    return null
  }
}

function safeSet(key: string, value: string) {
  try {
    localStorage.setItem(key, value)
  } catch {
    // ignore
  }
}

function safeRemove(key: string) {
  try {
    localStorage.removeItem(key)
  } catch {
    // ignore
  }
}

export function getAccessToken(): string | null {
  return safeGet(STORAGE_KEYS.TOKEN)
}

export function getRefreshToken(): string | null {
  return safeGet(STORAGE_KEYS.REFRESH_TOKEN)
}

export function getAccessTokenExpiresAt(): string | null {
  return safeGet(STORAGE_KEYS.EXPIRES_AT)
}

export function saveAuthTokens(tokens: StoredAuthTokens) {
  safeSet(STORAGE_KEYS.TOKEN, tokens.token)
  safeSet(STORAGE_KEYS.REFRESH_TOKEN, tokens.refreshToken)
  if (tokens.expiresAt) {
    safeSet(STORAGE_KEYS.EXPIRES_AT, tokens.expiresAt)
  }
  if (tokens.refreshExpiresAt) {
    safeSet(STORAGE_KEYS.REFRESH_EXPIRES_AT, tokens.refreshExpiresAt)
  }
}

export function clearAuthTokens() {
  safeRemove(STORAGE_KEYS.TOKEN)
  safeRemove(STORAGE_KEYS.REFRESH_TOKEN)
  safeRemove(STORAGE_KEYS.EXPIRES_AT)
  safeRemove(STORAGE_KEYS.REFRESH_EXPIRES_AT)
  safeRemove(STORAGE_KEYS.USER)
}

export { STORAGE_KEYS }
