const AUTH_INVALIDATED_EVENT = 'auth:invalidated'

interface AuthInvalidationDetail {
  reason?: string
}

let invalidationInProgress = false

function safeRemoveItem(storage: Storage, key: string) {
  try {
    storage.removeItem(key)
  } catch {
    // 忽略存储异常
  }
}

export function clearStoredAuth() {
  if (typeof window === 'undefined') return

  if (typeof localStorage !== 'undefined') {
    safeRemoveItem(localStorage, 'auth_token')
    safeRemoveItem(localStorage, 'user_data')
  }
}

export function isAuthErrorMessage(message?: string | null): boolean {
  if (!message) return false
  const normalizedMessage = message.toLowerCase()
  return (
    normalizedMessage.includes('需要登录') ||
    normalizedMessage.includes('未登录') ||
    normalizedMessage.includes('登录后') ||
    normalizedMessage.includes('请登录') ||
    normalizedMessage.includes('登录才能') ||
    normalizedMessage.includes('unauthorized') ||
    normalizedMessage.includes('401') ||
    normalizedMessage.includes('forbidden') ||
    normalizedMessage.includes('token') ||
    normalizedMessage.includes('auth')
  )
}

export function invalidateAuth(reason?: string) {
  if (typeof window === 'undefined') return
  if (invalidationInProgress) return

  invalidationInProgress = true

  clearStoredAuth()

  const detail: AuthInvalidationDetail = { reason }
  try {
    window.dispatchEvent(new CustomEvent<AuthInvalidationDetail>(AUTH_INVALIDATED_EVENT, { detail }))
  } catch {
    // 忽略事件触发异常
  }

  try {
    if (typeof sessionStorage !== 'undefined' && reason) {
      sessionStorage.setItem('auth:lastInvalidReason', reason)
    }
  } catch {
    // 忽略存储异常
  }

  // 使用微任务确保其他清理逻辑先执行
  Promise.resolve().then(() => {
    if (window.location.pathname !== '/login') {
      window.location.replace('/login')
    } else {
      invalidationInProgress = false
    }
  })
}

export function resetInvalidationFlag() {
  invalidationInProgress = false
}

export { AUTH_INVALIDATED_EVENT }
export type { AuthInvalidationDetail }

