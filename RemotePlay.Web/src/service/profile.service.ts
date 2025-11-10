import { apiRequest, type ApiResponse } from './api-client'

// Profile API 类型
export interface ProfileCredentials {
  id: string  // Base64编码的账户ID
  credentials?: string  // SHA256凭证
}

export interface UserProfile {
  name: string
  id: string
  credentials: string
  hosts?: Array<{
    name: string
    type: string
  }>
}

/**
 * Profile 服务
 */
export const profileService = {
  /**
   * 获取PSN登录URL
   */
  getLoginUrl: async (redirectUri?: string): Promise<ApiResponse<{ loginUrl: string }>> => {
    const params = redirectUri ? `?redirectUri=${encodeURIComponent(redirectUri)}` : ''
    return apiRequest<{ loginUrl: string }>(`/profile/login-url${params}`, {
      method: 'GET',
    })
  },

  /**
   * 通过OAuth回调URL创建新用户并获取账户ID
   */
  newUser: async (redirectUrl: string, profilePath?: string, save: boolean = true): Promise<ApiResponse<UserProfile>> => {
    return apiRequest<UserProfile>('/profile/new-user', {
      method: 'POST',
      body: JSON.stringify({
        redirectUrl,
        profilePath,
        save,
      }),
    })
  },

  /**
   * 获取用户Profile
   */
  getUserProfile: async (username: string, profilePath?: string): Promise<ApiResponse<UserProfile>> => {
    const params = profilePath ? `?path=${encodeURIComponent(profilePath)}` : ''
    return apiRequest<UserProfile>(`/profile/user/${username}${params}`, {
      method: 'GET',
    })
  },
}

