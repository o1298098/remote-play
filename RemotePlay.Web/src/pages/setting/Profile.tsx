import { useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { useAuth } from '@/hooks/use-auth'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { ArrowLeft, Gamepad2, Server, Settings } from 'lucide-react'
import { useEffect } from 'react'

export default function Profile() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const { isAuthenticated } = useAuth()

  useEffect(() => {
    if (!isAuthenticated) {
      navigate('/login')
    }
  }, [isAuthenticated, navigate])

  const settingsItems = [
    {
      title: '控制器映射',
      description: '配置游戏手柄按键映射和震动设置',
      icon: Gamepad2,
      path: '/controller-mapping',
    },
    {
      title: 'TURN 服务器设置',
      description: '配置 WebRTC TURN 服务器以改善网络连接',
      icon: Server,
      path: '/turn-server-settings',
    },
  ]

  return (
    <div className="container mx-auto p-4 max-w-4xl">
      <div className="mb-6">
        <Button
          variant="ghost"
          size="sm"
          onClick={() => navigate(-1)}
          className="mb-4"
        >
          <ArrowLeft className="mr-2 h-4 w-4" />
          返回
        </Button>
        <h1 className="text-2xl font-bold mb-2">设置</h1>
        <p className="text-gray-400">管理您的应用设置和偏好</p>
      </div>

      <div className="grid gap-4 md:grid-cols-2">
        {settingsItems.map((item) => {
          const Icon = item.icon
          return (
            <Card
              key={item.path}
              className="cursor-pointer hover:bg-gray-900/50 transition-colors"
              onClick={() => navigate(item.path)}
            >
              <CardHeader>
                <div className="flex items-center space-x-3">
                  <div className="p-2 bg-blue-600/20 rounded-lg">
                    <Icon className="h-5 w-5 text-blue-400" />
                  </div>
                  <div>
                    <CardTitle className="text-lg">{item.title}</CardTitle>
                  </div>
                </div>
                <CardDescription className="mt-2">{item.description}</CardDescription>
              </CardHeader>
            </Card>
          )
        })}
      </div>
    </div>
  )
}

