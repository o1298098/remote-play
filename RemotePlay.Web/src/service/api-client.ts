import { invalidateAuth, isAuthErrorMessage } from '@/utils/auth-invalidation'
import { getAccessToken } from '@/utils/auth-storage'
import { refreshAccessToken } from '@/utils/token-refresh'
import { getTranslatedErrorMessage } from '@/utils/error-code-translator'

// API 基础配置
const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || '/api'

// API 响应类型
// 新的后端格式：
// 成功: { success: true, data: object, message: "" }
// 失败: { success: false, errorMessage: "", errorCode?: number }
export interface ApiResponse<T = any> {
  success: boolean
  data?: T  // 成功时的数据
  message?: string  // 成功时的消息（可选）
  errorMessage?: string  // 失败时的错误消息
  errorCode?: number | string  // 错误码，前端根据此码在 i18n 中查找翻译
  // 为了向后兼容，保留 result 字段（从 data 映射）
  result?: T
}

function isAuthRefreshEndpoint(endpoint: string): boolean {
  return /\/auth\/(login|register|refresh)\b/i.test(endpoint)
}

function buildRequestInit(options: RequestInit = {}, accessToken?: string | null): RequestInit {
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    ...(options.headers as Record<string, string> | undefined),
  }

  const token = accessToken ?? getAccessToken()
  if (token) {
    headers.Authorization = `Bearer ${token}`
  }

  return {
    ...options,
    headers,
  }
}

async function parseApiResponse<T>(
  response: Response,
  endpoint: string,
  requestInit: RequestInit
): Promise<{ unauthorizedStatus: boolean; data: ApiResponse<T> | null; emptyUnauthorized: boolean }> {
  const unauthorizedStatus = response.status === 401 || response.status === 403

  if (response.status === 204 && requestInit.method === 'OPTIONS') {
    return {
      unauthorizedStatus,
      emptyUnauthorized: false,
      data: {
        success: true,
        message: '预检请求成功',
      } as ApiResponse<T>,
    }
  }

  const contentType = response.headers.get('content-type')
  if (!contentType || response.status === 204) {
    if (response.ok) {
      return {
        unauthorizedStatus,
        emptyUnauthorized: false,
        data: {
          success: true,
          message: '请求成功',
        } as ApiResponse<T>,
      }
    }
    throw new Error(`请求失败: ${response.status} ${response.statusText}`)
  }

  const text = await response.text()
  if (!text || text.trim().length === 0) {
    return {
      unauthorizedStatus,
      emptyUnauthorized: unauthorizedStatus,
      data: null,
    }
  }

  try {
    return {
      unauthorizedStatus,
      emptyUnauthorized: false,
      data: JSON.parse(text) as ApiResponse<T>,
    }
  } catch (parseError) {
    console.error('JSON解析失败:', parseError)
    throw new Error('响应格式错误，无法解析JSON数据')
  }
}

function normalizeSuccessResponse<T>(data: ApiResponse<T>): ApiResponse<T> {
  if (data && typeof data === 'object') {
    const responseData = data as any

    if ('data' in responseData) {
      return {
        success: responseData.success ?? true,
        data: responseData.data,
        message: responseData.message,
        errorMessage: responseData.errorMessage,
        result: responseData.data,
      } as ApiResponse<T>
    }

    if ('errorMessage' in responseData && !responseData.success) {
      const translatedError = getTranslatedErrorMessage({
        errorCode: responseData.errorCode,
        errorMessage: responseData.errorMessage,
      })
      return {
        success: false,
        errorMessage: translatedError,
        errorCode: responseData.errorCode,
        result: undefined,
      } as ApiResponse<T>
    }

    if ('result' in responseData) {
      return {
        ...responseData,
        data: responseData.result,
      } as ApiResponse<T>
    }
  }

  return data
}

/**
 * 通用 API 请求函数
 * @param endpoint API端点路径
 * @param options 请求选项
 * @returns Promise<ApiResponse<T>>
 */
export async function apiRequest<T>(
  endpoint: string,
  options: RequestInit = {}
): Promise<ApiResponse<T>> {
  const url = `${API_BASE_URL}${endpoint}`
  const skipRefresh = isAuthRefreshEndpoint(endpoint)

  const execute = async (
    accessToken?: string | null,
    hasRetriedAfterRefresh = false
  ): Promise<ApiResponse<T>> => {
    const requestInit = buildRequestInit(options, accessToken)
    const response = await fetch(url, requestInit)
    const parsed = await parseApiResponse<T>(response, endpoint, requestInit)

    if (parsed.emptyUnauthorized) {
      if (!skipRefresh && !hasRetriedAfterRefresh) {
        const refreshed = await refreshAccessToken()
        if (refreshed?.token) {
          return execute(refreshed.token, true)
        }
      }
      invalidateAuth(`接口 ${endpoint} 返回未授权（空响应）`)
      throw new Error('登录状态已失效，请重新登录')
    }

    const data = parsed.data
    if (!data) {
      throw new Error('响应为空')
    }

    if (!response.ok) {
      const errorResponse = data as any
      const errorMsg = getTranslatedErrorMessage({
        errorCode: errorResponse?.errorCode,
        errorMessage:
          errorResponse?.errorMessage ||
          errorResponse?.message ||
          `请求失败: ${response.status}`,
      })

      const shouldTryRefresh =
        !skipRefresh &&
        !hasRetriedAfterRefresh &&
        (parsed.unauthorizedStatus || isAuthErrorMessage(errorMsg))

      if (shouldTryRefresh) {
        const refreshed = await refreshAccessToken()
        if (refreshed?.token) {
          return execute(refreshed.token, true)
        }
        invalidateAuth(errorMsg || `接口 ${endpoint} 返回未授权`)
      } else if (parsed.unauthorizedStatus || isAuthErrorMessage(errorMsg)) {
        invalidateAuth(errorMsg || `接口 ${endpoint} 返回未授权`)
      }

      throw new Error(errorMsg)
    }

    const normalized = normalizeSuccessResponse(data)
    if (
      normalized.success === false &&
      isAuthErrorMessage(normalized.errorMessage) &&
      !skipRefresh &&
      !hasRetriedAfterRefresh
    ) {
      const refreshed = await refreshAccessToken()
      if (refreshed?.token) {
        return execute(refreshed.token, true)
      }
      invalidateAuth(normalized.errorMessage || `接口 ${endpoint} 返回认证失败`)
    }

    return normalized
  }

  try {
    return await execute()
  } catch (error) {
    console.error('API请求错误:', error)
    if (error instanceof Error) {
      throw error
    }
    throw new Error('网络错误，请稍后重试')
  }
}
