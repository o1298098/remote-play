import { apiRequest, type ApiResponse } from './api-client'

// 认证响应类型
export interface AuthResponse {
  token: string
  username: string
  email: string
  expiresAt: string
}

// 注册请求类型
export interface RegisterRequest {
  username: string
  email: string
  password: string
}

// 登录请求类型
export interface LoginRequest {
  usernameOrEmail: string
  password: string
}

/**
 * 认证服务
 */
export const authService = {
  /**
   * 用户注册
   */
  register: async (data: RegisterRequest): Promise<ApiResponse<AuthResponse>> => {
    return apiRequest<AuthResponse>('/auth/register', {
      method: 'POST',
      body: JSON.stringify(data),
    })
  },

  /**
   * 用户登录
   */
  login: async (data: LoginRequest): Promise<ApiResponse<AuthResponse>> => {
    return apiRequest<AuthResponse>('/auth/login', {
      method: 'POST',
      body: JSON.stringify(data),
    })
  },

  /**
   * 获取当前用户信息
   */
  getCurrentUser: async (): Promise<ApiResponse<any>> => {
    return apiRequest('/auth/me', {
      method: 'GET',
    })
  },
}

