import { invalidateAuth, isAuthErrorMessage } from '@/utils/auth-invalidation'

// API 基础配置
const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || '/api'

// API 响应类型
// 新的后端格式：
// 成功: { success: true, data: object, message: "" }
// 失败: { success: false, errorMessage: "" }
export interface ApiResponse<T = any> {
  success: boolean
  data?: T  // 成功时的数据
  message?: string  // 成功时的消息（可选）
  errorMessage?: string  // 失败时的错误消息
  // 为了向后兼容，保留 result 字段（从 data 映射）
  result?: T
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
  
  const defaultOptions: RequestInit = {
    headers: {
      'Content-Type': 'application/json',
      ...options.headers,
    },
    ...options,
  }

  // 如果有 token，添加到请求头
  const token = localStorage.getItem('auth_token')
  if (token) {
    defaultOptions.headers = {
      ...defaultOptions.headers,
      Authorization: `Bearer ${token}`,
    }
  }

  try {
    const response = await fetch(url, defaultOptions)
    const unauthorizedStatus = response.status === 401 || response.status === 403
    
    // 跳过OPTIONS预检请求（204状态码）
    if (response.status === 204 && defaultOptions.method === 'OPTIONS') {
      // 这是CORS预检请求，浏览器会自动处理，这里不需要处理
      return {
        success: true,
        message: '预检请求成功',
      } as ApiResponse<T>
    }
    
    // 检查响应是否有内容
    const contentType = response.headers.get('content-type')
    
    // 如果没有内容类型或者是空响应
    if (!contentType || response.status === 204) {
      if (response.ok) {
        return {
          success: true,
          message: '请求成功',
        } as ApiResponse<T>
      } else {
        throw new Error(`请求失败: ${response.status} ${response.statusText}`)
      }
    }
    
    // 解析JSON响应
    let data: ApiResponse<T>
    try {
      // 先读取文本内容
      const text = await response.text()
      if (!text || text.trim().length === 0) {
        if (unauthorizedStatus) {
          invalidateAuth(`接口 ${endpoint} 返回未授权（空响应）`)
          throw new Error('登录状态已失效，请重新登录')
        }
        throw new Error('响应为空')
      }
      // 尝试解析JSON
      data = JSON.parse(text)
    } catch (parseError) {
      console.error('JSON解析失败:', parseError)
      throw new Error('响应格式错误，无法解析JSON数据')
    }

    // 处理新的响应格式
    if (!response.ok) {
      // 失败时，使用 errorMessage 字段
      const errorMsg =
        (data as any)?.errorMessage || (data as any)?.message || `请求失败: ${response.status}`

      if (unauthorizedStatus || isAuthErrorMessage(errorMsg)) {
        invalidateAuth(errorMsg || `接口 ${endpoint} 返回未授权`)
      }

      throw new Error(errorMsg)
    }

    // 统一处理新的响应格式
    // 成功: { success: true, data: T, message?: string }
    // 失败: { success: false, errorMessage: string }
    if (data && typeof data === 'object') {
      const responseData = data as any
      
      // 如果响应包含 data 字段，映射到 result 字段以保持向后兼容
      if ('data' in responseData) {
        return {
          success: responseData.success ?? true,
          data: responseData.data,
          message: responseData.message,
          errorMessage: responseData.errorMessage,
          result: responseData.data, // 向后兼容
        } as ApiResponse<T>
      }
      
      // 如果响应包含 errorMessage，说明是失败响应
      if ('errorMessage' in responseData && !responseData.success) {
        if (isAuthErrorMessage(responseData.errorMessage)) {
          invalidateAuth(responseData.errorMessage || `接口 ${endpoint} 返回认证失败`)
        }
        return {
          success: false,
          errorMessage: responseData.errorMessage,
          result: undefined,
        } as ApiResponse<T>
      }
      
      // 如果已经有 result 字段（旧格式兼容），保持原样
      if ('result' in responseData) {
        return {
          ...responseData,
          data: responseData.result, // 同时提供 data 字段
        } as ApiResponse<T>
      }
    }

    return data as ApiResponse<T>
  } catch (error) {
    console.error('API请求错误:', error)
    if (error instanceof Error) {
      throw error
    }
    throw new Error('网络错误，请稍后重试')
  }
}

