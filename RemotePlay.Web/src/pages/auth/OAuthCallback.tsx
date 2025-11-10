import { useEffect } from 'react'
import { useSearchParams } from 'react-router-dom'
import { Loader2, CheckCircle, XCircle } from 'lucide-react'

export default function OAuthCallback() {
  const [searchParams] = useSearchParams()

  useEffect(() => {
    // 获取完整的URL（包括查询参数）
    const currentUrl = window.location.href
    
    // 检查是否是有效的OAuth回调（包含code参数）
    const code = searchParams.get('code')
    const state = searchParams.get('state')
    const error = searchParams.get('error')
    
    // 如果是从PSN直接重定向过来的，需要构建完整的回调URL（使用PSN的固定redirect_uri）
    // 否则，如果已经有code参数，直接使用当前URL
    let redirectUrl = currentUrl
    
    // 如果当前URL是我们的前端回调页面，需要构建PSN的回调URL格式
    // PSN的回调URL格式是：https://remoteplay.dl.playstation.net/remoteplay/redirect?code=xxx&state=xxx
    if (code && !currentUrl.includes('remoteplay.dl.playstation.net')) {
      // 构建PSN格式的回调URL（后端需要这个格式）
      const psnRedirectBase = 'https://remoteplay.dl.playstation.net/remoteplay/redirect'
      const params = new URLSearchParams()
      if (code) params.set('code', code)
      if (state) params.set('state', state)
      redirectUrl = `${psnRedirectBase}?${params.toString()}`
    }
    
    if (code && redirectUrl) {
      // 发送消息给父窗口（打开登录窗口的窗口）
      if (window.opener && !window.opener.closed) {
        window.opener.postMessage(
          {
            type: 'PSN_OAUTH_CALLBACK',
            redirectUrl: redirectUrl,
            code,
            state,
          },
          window.location.origin
        )
        
        // 显示成功消息
        setTimeout(() => {
          window.close()
        }, 2000)
      } else {
        // 如果没有父窗口，可能是直接访问的
        console.log('No opener window found, cannot send message to parent')
      }
    } else if (error) {
      // 处理错误情况
      if (window.opener && !window.opener.closed) {
        window.opener.postMessage(
          {
            type: 'PSN_OAUTH_ERROR',
            error: error,
            errorDescription: searchParams.get('error_description') || '登录失败',
          },
          window.location.origin
        )
        
        setTimeout(() => {
          window.close()
        }, 3000)
      }
    }
  }, [searchParams])

  const code = searchParams.get('code')
  const error = searchParams.get('error')

  return (
    <div className="min-h-screen bg-gradient-to-br from-blue-50 to-indigo-50 flex items-center justify-center p-4">
      <div className="bg-white rounded-2xl shadow-xl p-8 max-w-md w-full text-center">
        {code ? (
          <>
            <div className="flex justify-center mb-4">
              <div className="w-16 h-16 rounded-full bg-green-100 flex items-center justify-center">
                <CheckCircle className="h-10 w-10 text-green-600" />
              </div>
            </div>
            <h2 className="text-2xl font-bold text-gray-900 mb-2">登录成功</h2>
            <p className="text-gray-600 mb-4">正在获取账户信息...</p>
            <div className="flex justify-center">
              <Loader2 className="h-6 w-6 animate-spin text-blue-600" />
            </div>
            <p className="text-sm text-gray-500 mt-4">窗口将自动关闭</p>
          </>
        ) : error ? (
          <>
            <div className="flex justify-center mb-4">
              <div className="w-16 h-16 rounded-full bg-red-100 flex items-center justify-center">
                <XCircle className="h-10 w-10 text-red-600" />
              </div>
            </div>
            <h2 className="text-2xl font-bold text-gray-900 mb-2">登录失败</h2>
            <p className="text-gray-600 mb-4">{searchParams.get('error_description') || '登录过程中出现错误'}</p>
            <p className="text-sm text-gray-500">窗口将自动关闭</p>
          </>
        ) : (
          <>
            <div className="flex justify-center mb-4">
              <Loader2 className="h-10 w-10 animate-spin text-blue-600" />
            </div>
            <h2 className="text-2xl font-bold text-gray-900 mb-2">处理中...</h2>
            <p className="text-gray-600">正在处理OAuth回调</p>
          </>
        )}
      </div>
    </div>
  )
}

