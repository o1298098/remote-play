import { useState, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { useToast } from '@/hooks/use-toast'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Label } from '@/components/ui/label'
import { Input } from '@/components/ui/input'
import { ArrowLeft, Save, Plus, Trash2, Server } from 'lucide-react'
import { useAuth } from '@/hooks/use-auth'
import { streamingService, type TurnServerConfig } from '@/service/streaming.service'

export default function TurnServerSettings() {
  const { t } = useTranslation()
  const { toast } = useToast()
  const navigate = useNavigate()
  const { isAuthenticated } = useAuth()
  const [turnServers, setTurnServers] = useState<TurnServerConfig[]>([])
  const [isLoading, setIsLoading] = useState(true)
  const [isSaving, setIsSaving] = useState(false)

  useEffect(() => {
    if (!isAuthenticated) {
      navigate('/login')
      return
    }

    loadTurnConfig()
  }, [isAuthenticated, navigate])

  const loadTurnConfig = async () => {
    try {
      setIsLoading(true)
      const response = await streamingService.getTurnConfig()
      if (response.success && response.data) {
        setTurnServers(response.data.turnServers || [])
      } else {
        setTurnServers([])
      }
    } catch (error) {
      console.error('加载 TURN 配置失败:', error)
      toast({
        title: '加载失败',
        description: '无法加载 TURN 服务器配置',
        variant: 'destructive',
      })
      setTurnServers([])
    } finally {
      setIsLoading(false)
    }
  }

  const handleAddServer = () => {
    setTurnServers([...turnServers, { url: '', username: '', credential: '' }])
  }

  const handleRemoveServer = (index: number) => {
    setTurnServers(turnServers.filter((_, i) => i !== index))
  }

  const handleServerChange = (index: number, field: keyof TurnServerConfig, value: string) => {
    const updated = [...turnServers]
    updated[index] = { ...updated[index], [field]: value }
    setTurnServers(updated)
  }

  const handleSave = async () => {
    try {
      setIsSaving(true)

      // 验证配置
      const validServers = turnServers.filter(
        (server) => server.url && server.url.trim().length > 0
      )

      if (validServers.length === 0 && turnServers.length > 0) {
        toast({
          title: '保存失败',
          description: '请至少填写一个有效的 TURN 服务器 URL',
          variant: 'destructive',
        })
        return
      }

      const response = await streamingService.saveTurnConfig({
        turnServers: validServers,
      })

      if (response.success) {
        toast({
          title: '保存成功',
          description: 'TURN 服务器配置已保存',
        })
        setTurnServers(validServers)
      } else {
        throw new Error(response.errorMessage || '保存失败')
      }
    } catch (error) {
      console.error('保存 TURN 配置失败:', error)
      toast({
        title: '保存失败',
        description: error instanceof Error ? error.message : '无法保存 TURN 服务器配置',
        variant: 'destructive',
      })
    } finally {
      setIsSaving(false)
    }
  }

  if (isLoading) {
    return (
      <div className="container mx-auto p-4">
        <div className="flex items-center justify-center min-h-screen">
          <div className="text-center">
            <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-white mx-auto mb-4"></div>
            <p className="text-gray-400">加载中...</p>
          </div>
        </div>
      </div>
    )
  }

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
        <h1 className="text-2xl font-bold mb-2">TURN 服务器配置</h1>
        <p className="text-gray-400">
          配置 TURN 服务器以改善 WebRTC 连接在 NAT 环境下的稳定性
        </p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Server className="h-5 w-5" />
            TURN 服务器列表
          </CardTitle>
          <CardDescription>
            添加 TURN 服务器以帮助 WebRTC 在复杂网络环境下建立连接
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          {turnServers.length === 0 ? (
            <div className="text-center py-8 text-gray-400">
              <p>暂无 TURN 服务器配置</p>
              <p className="text-sm mt-2">点击下方按钮添加服务器</p>
            </div>
          ) : (
            turnServers.map((server, index) => (
              <div
                key={index}
                className="border rounded-lg p-4 space-y-3 bg-gray-900/50"
              >
                <div className="flex items-center justify-between mb-3">
                  <Label className="text-sm font-medium">服务器 #{index + 1}</Label>
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => handleRemoveServer(index)}
                    className="text-red-400 hover:text-red-300"
                  >
                    <Trash2 className="h-4 w-4" />
                  </Button>
                </div>

                <div className="space-y-2">
                  <div>
                    <Label htmlFor={`url-${index}`}>服务器 URL *</Label>
                    <Input
                      id={`url-${index}`}
                      placeholder="turn:example.com:3478?transport=udp"
                      value={server.url || ''}
                      onChange={(e) => handleServerChange(index, 'url', e.target.value)}
                      className="mt-1"
                    />
                    <p className="text-xs text-gray-500 mt-1">
                      格式: turn:host:port?transport=udp 或 turn:host:port?transport=tcp
                    </p>
                  </div>

                  <div>
                    <Label htmlFor={`username-${index}`}>用户名（可选）</Label>
                    <Input
                      id={`username-${index}`}
                      placeholder="TURN 服务器用户名"
                      value={server.username || ''}
                      onChange={(e) => handleServerChange(index, 'username', e.target.value)}
                      className="mt-1"
                    />
                  </div>

                  <div>
                    <Label htmlFor={`credential-${index}`}>密码（可选）</Label>
                    <Input
                      id={`credential-${index}`}
                      type="password"
                      placeholder="TURN 服务器密码"
                      value={server.credential || ''}
                      onChange={(e) => handleServerChange(index, 'credential', e.target.value)}
                      className="mt-1"
                    />
                  </div>
                </div>
              </div>
            ))
          )}

          <Button
            variant="outline"
            onClick={handleAddServer}
            className="w-full"
          >
            <Plus className="mr-2 h-4 w-4" />
            添加服务器
          </Button>

          <div className="flex gap-2 pt-4">
            <Button
              onClick={handleSave}
              disabled={isSaving}
              className="flex-1"
            >
              <Save className="mr-2 h-4 w-4" />
              {isSaving ? '保存中...' : '保存配置'}
            </Button>
            <Button
              variant="outline"
              onClick={loadTurnConfig}
              disabled={isSaving}
            >
              重置
            </Button>
          </div>
        </CardContent>
      </Card>

      <Card className="mt-4">
        <CardHeader>
          <CardTitle className="text-sm">使用说明</CardTitle>
        </CardHeader>
        <CardContent className="text-sm text-gray-400 space-y-2">
          <p>• TURN 服务器用于在复杂网络环境下（如对称 NAT）建立 WebRTC 连接</p>
          <p>• 如果您的网络环境良好，可能不需要配置 TURN 服务器</p>
          <p>• 建议配置多个 TURN 服务器以提高连接成功率</p>
          <p>• 配置保存后，新的 WebRTC 连接将使用这些服务器</p>
        </CardContent>
      </Card>
    </div>
  )
}

