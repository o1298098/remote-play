import { useState, useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import { useToast } from '@/hooks/use-toast'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Label } from '@/components/ui/label'
import { Input } from '@/components/ui/input'
import { Save, Plus, Trash2, Server, Edit2, X, TestTube, CheckCircle2, XCircle, Loader2, Activity, Zap } from 'lucide-react'
import { streamingService, type TurnServerConfig } from '@/service/streaming.service'
import { testTurnServer, testTurnServerBandwidth, type TurnServerTestResult } from '@/utils/turn-test'

export default function TurnServerSettingsTab() {
  const { t } = useTranslation()
  const { toast } = useToast()
  const [turnServers, setTurnServers] = useState<TurnServerConfig[]>([])
  const [isLoading, setIsLoading] = useState(true)
  const [isSaving, setIsSaving] = useState(false)
  const [editingIndex, setEditingIndex] = useState<number | null>(null)
  const [editingServer, setEditingServer] = useState<TurnServerConfig | null>(null)
  const [testingIndex, setTestingIndex] = useState<number | null>(null)
  const [testResults, setTestResults] = useState<Map<number, TurnServerTestResult>>(new Map())
  const [bandwidthTestMode, setBandwidthTestMode] = useState(false)

  useEffect(() => {
    loadTurnConfig()
  }, [])

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
        title: t('devices.settings.turnServer.loadFailed'),
        description: t('devices.settings.turnServer.loadFailedDescription'),
        variant: 'destructive',
      })
      setTurnServers([])
    } finally {
      setIsLoading(false)
    }
  }

  const handleAddServer = () => {
    const newServer = { url: '', username: '', credential: '' }
    setTurnServers([...turnServers, newServer])
    setEditingIndex(turnServers.length)
    setEditingServer({ ...newServer })
  }

  const handleRemoveServer = (index: number) => {
    setTurnServers(turnServers.filter((_, i) => i !== index))
    if (editingIndex === index) {
      setEditingIndex(null)
      setEditingServer(null)
    } else if (editingIndex !== null && editingIndex > index) {
      setEditingIndex(editingIndex - 1)
    }
  }

  const handleEditServer = (index: number) => {
    setEditingIndex(index)
    setEditingServer({ ...turnServers[index] })
  }

  const handleCancelEdit = () => {
    setEditingIndex(null)
    setEditingServer(null)
  }

  const handleSaveEdit = (index: number) => {
    if (editingServer) {
      const updated = [...turnServers]
      updated[index] = { ...editingServer }
      setTurnServers(updated)
      setEditingIndex(null)
      setEditingServer(null)
    }
  }

  const handleEditingServerChange = (field: keyof TurnServerConfig, value: string) => {
    if (editingServer) {
      setEditingServer({ ...editingServer, [field]: value })
    }
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
          title: t('devices.settings.turnServer.saveFailed'),
          description: t('devices.settings.turnServer.saveFailedInvalid'),
          variant: 'destructive',
        })
        return
      }

      const response = await streamingService.saveTurnConfig({
        turnServers: validServers,
      })

      if (response.success) {
        toast({
          title: t('devices.settings.turnServer.saveSuccess'),
          description: t('devices.settings.turnServer.saveSuccessDescription'),
        })
        setTurnServers(validServers)
        setEditingIndex(null)
        setEditingServer(null)
      } else {
        throw new Error(response.errorMessage || t('devices.settings.turnServer.saveFailed'))
      }
    } catch (error) {
      console.error('保存 TURN 配置失败:', error)
      toast({
        title: t('devices.settings.turnServer.saveFailed'),
        description: error instanceof Error ? error.message : t('devices.settings.turnServer.saveFailedDescription'),
        variant: 'destructive',
      })
    } finally {
      setIsSaving(false)
    }
  }

  const handleTestServer = async (index: number) => {
    const server = turnServers[index]
    if (!server || !server.url || !server.url.trim()) {
      toast({
        title: t('devices.settings.turnServer.testFailed'),
        description: t('devices.settings.turnServer.testFailedNoUrl'),
        variant: 'destructive',
      })
      return
    }

    setTestingIndex(index)
    try {
      let result: TurnServerTestResult
      
      if (bandwidthTestMode) {
        // 带宽测试模式
        toast({
          title: t('devices.settings.turnServer.testingBandwidth'),
          description: t('devices.settings.turnServer.testingBandwidthDescription'),
        })
        result = await testTurnServerBandwidth(server, 5, 30000)
      } else {
        // 快速延迟测试模式
        result = await testTurnServer(server, 10000)
      }

      const newResults = new Map(testResults)
      newResults.set(index, result)
      setTestResults(newResults)

      if (result.status === 'success') {
        let description = ''
        if (result.latency) {
          description += `${t('devices.settings.webrtc.test.latency')}: ${result.latency}ms`
        }
        if (result.bandwidth) {
          if (description) description += ' | '
          description += `${t('devices.settings.webrtc.test.avgBandwidth')}: ${result.bandwidth} Mbps`
        }
        
        toast({
          title: t('devices.settings.turnServer.testSuccess'),
          description: description || t('devices.settings.turnServer.testSuccessDescription', { latency: '' }),
        })
      } else {
        toast({
          title: t('devices.settings.turnServer.testFailed'),
          description: result.errorMessage || t('devices.settings.turnServer.testFailedDescription'),
          variant: 'destructive',
        })
      }
    } catch (error) {
      const errorResult: TurnServerTestResult = {
        url: server.url || 'unknown',
        status: 'failed',
        errorMessage: error instanceof Error ? error.message : t('devices.settings.turnServer.testFailedDescription'),
      }
      const newResults = new Map(testResults)
      newResults.set(index, errorResult)
      setTestResults(newResults)
      
      toast({
        title: t('devices.settings.turnServer.testFailed'),
        description: error instanceof Error ? error.message : t('devices.settings.turnServer.testFailedDescription'),
        variant: 'destructive',
      })
    } finally {
      setTestingIndex(null)
    }
  }


  if (isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <div className="text-center">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600 dark:border-blue-400 mx-auto mb-4"></div>
          <p className="text-gray-600 dark:text-gray-400">{t('devices.settings.turnServer.loading')}</p>
        </div>
      </div>
    )
  }

  return (
    <div className="space-y-4">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Server className="h-5 w-5" />
            {t('devices.settings.turnServer.title')}
          </CardTitle>
          <CardDescription>
            {t('devices.settings.turnServer.description')}
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          {/* 测试模式切换 */}
          {turnServers.length > 0 && (
            <div className="flex items-center justify-between p-3 bg-blue-50 dark:bg-blue-900/20 rounded-md border border-blue-200 dark:border-blue-800">
              <div className="flex items-center gap-2">
                {bandwidthTestMode ? (
                  <Activity className="h-4 w-4 text-blue-600 dark:text-blue-400" />
                ) : (
                  <Zap className="h-4 w-4 text-yellow-600 dark:text-yellow-400" />
                )}
                <div>
                  <div className="text-sm font-medium">
                    {bandwidthTestMode
                      ? t('devices.settings.webrtc.test.bandwidthMode')
                      : t('devices.settings.webrtc.test.latencyMode')}
                  </div>
                  <div className="text-xs text-gray-600 dark:text-gray-400">
                    {bandwidthTestMode
                      ? t('devices.settings.webrtc.test.bandwidthModeHint')
                      : t('devices.settings.webrtc.test.latencyModeHint')}
                  </div>
                </div>
              </div>
              <input
                type="checkbox"
                checked={bandwidthTestMode}
                onChange={(e) => setBandwidthTestMode(e.target.checked)}
                disabled={testingIndex !== null}
                className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
              />
            </div>
          )}

          {turnServers.length === 0 ? (
            <div className="text-center py-8 text-gray-500 dark:text-gray-400">
              <p className="text-gray-700 dark:text-gray-300">{t('devices.settings.turnServer.empty')}</p>
              <p className="text-sm mt-2 text-gray-500 dark:text-gray-400">{t('devices.settings.turnServer.emptyHint')}</p>
            </div>
          ) : (
            turnServers.map((server, index) => {
              const isEditing = editingIndex === index
              
              return (
                <div
                  key={index}
                  className="border border-gray-200 dark:border-gray-700 rounded-lg p-4 bg-gray-50 dark:bg-gray-800/50"
                >
                  {isEditing ? (
                    // 编辑模式
                    <div className="space-y-3">
                      <div className="flex items-center justify-between mb-3">
                        <Label className="text-sm font-medium text-gray-900 dark:text-gray-100">
                          {t('devices.settings.turnServer.serverNumber', { number: index + 1 })}
                        </Label>
                        <div className="flex gap-2">
                          <Button
                            variant="ghost"
                            size="sm"
                            onClick={() => handleSaveEdit(index)}
                            className="text-blue-600 hover:text-blue-700 dark:text-blue-400 dark:hover:text-blue-300"
                          >
                            <Save className="h-4 w-4" />
                          </Button>
                          <Button
                            variant="ghost"
                            size="sm"
                            onClick={handleCancelEdit}
                            className="text-gray-600 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-300"
                          >
                            <X className="h-4 w-4" />
                          </Button>
                        </div>
                      </div>

                      <div className="space-y-2">
                        <div>
                          <Label htmlFor={`url-${index}`}>{t('devices.settings.turnServer.urlRequired')}</Label>
                          <Input
                            id={`url-${index}`}
                            placeholder={t('devices.settings.turnServer.urlPlaceholder')}
                            value={editingServer?.url || ''}
                            onChange={(e) => handleEditingServerChange('url', e.target.value)}
                            className="mt-1"
                          />
                          <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                            {t('devices.settings.turnServer.urlHint')}
                          </p>
                        </div>

                        <div>
                          <Label htmlFor={`username-${index}`}>{t('devices.settings.turnServer.username')}</Label>
                          <Input
                            id={`username-${index}`}
                            placeholder={t('devices.settings.turnServer.usernamePlaceholder')}
                            value={editingServer?.username || ''}
                            onChange={(e) => handleEditingServerChange('username', e.target.value)}
                            className="mt-1"
                          />
                        </div>

                        <div>
                          <Label htmlFor={`credential-${index}`}>{t('devices.settings.turnServer.credential')}</Label>
                          <Input
                            id={`credential-${index}`}
                            type="password"
                            placeholder={t('devices.settings.turnServer.credentialPlaceholder')}
                            value={editingServer?.credential || ''}
                            onChange={(e) => handleEditingServerChange('credential', e.target.value)}
                            className="mt-1"
                          />
                        </div>
                      </div>
                    </div>
                  ) : (
                    // 只读模式 - 显示简要信息
                    <div className="space-y-3">
                      <div className="flex items-center justify-between">
                        <div className="flex-1 min-w-0">
                          <div className="flex items-center gap-2 mb-1">
                            <Label className="text-sm font-medium text-gray-900 dark:text-gray-100">
                              {t('devices.settings.turnServer.serverNumber', { number: index + 1 })}
                            </Label>
                            {testResults.has(index) && (
                              <div className="flex items-center gap-1">
                                {testResults.get(index)?.status === 'success' ? (
                                  <CheckCircle2 className="h-4 w-4 text-green-500" />
                                ) : (
                                  <XCircle className="h-4 w-4 text-red-500" />
                                )}
                              </div>
                            )}
                          </div>
                          <div className="text-sm text-gray-700 dark:text-gray-300 truncate">
                            {server.url || t('devices.settings.turnServer.empty')}
                          </div>
                          {server.username && (
                            <div className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                              {t('devices.settings.turnServer.username')}: {server.username}
                            </div>
                          )}
                          {testResults.has(index) && (
                            <div className="mt-2 text-xs space-y-1">
                              {testResults.get(index)?.status === 'success' ? (
                                <div className="space-y-1">
                                  <div className="text-green-600 dark:text-green-400">
                                    ✓ {t('devices.settings.webrtc.test.success')}
                                    {testResults.get(index)?.type && (
                                      <span className="ml-2 text-gray-500 dark:text-gray-400">
                                        ({testResults.get(index)!.type})
                                      </span>
                                    )}
                                  </div>
                                  {testResults.get(index)?.latency && (
                                    <div className="text-gray-600 dark:text-gray-400">
                                      ⏱️ {t('devices.settings.webrtc.test.latency')}: <span className="font-semibold">{testResults.get(index)!.latency}ms</span>
                                    </div>
                                  )}
                                  {testResults.get(index)?.bandwidth && (
                                    <div className="text-blue-600 dark:text-blue-400">
                                      📊 {t('devices.settings.webrtc.test.avgBandwidth')}: <span className="font-semibold">{testResults.get(index)!.bandwidth} Mbps</span>
                                    </div>
                                  )}
                                  {testResults.get(index)?.uploadSpeed !== undefined && (
                                    <div className="text-gray-600 dark:text-gray-400">
                                      ↑ {t('devices.settings.webrtc.test.uploadSpeed')}: <span className="font-semibold">{testResults.get(index)!.uploadSpeed} Mbps</span>
                                    </div>
                                  )}
                                  {testResults.get(index)?.downloadSpeed !== undefined && (
                                    <div className="text-gray-600 dark:text-gray-400">
                                      ↓ {t('devices.settings.webrtc.test.downloadSpeed')}: <span className="font-semibold">{testResults.get(index)!.downloadSpeed} Mbps</span>
                                    </div>
                                  )}
                                </div>
                              ) : (
                                <div className="text-red-600 dark:text-red-400">
                                  ✗ {testResults.get(index)?.errorMessage || t('devices.settings.turnServer.testFailed')}
                                </div>
                              )}
                            </div>
                          )}
                        </div>
                        <div className="flex items-center gap-2 ml-4">
                          <Button
                            variant="ghost"
                            size="sm"
                            onClick={() => handleTestServer(index)}
                            disabled={testingIndex === index || !server.url || !server.url.trim()}
                            className="text-green-600 hover:text-green-700 dark:text-green-400 dark:hover:text-green-300 hover:bg-green-50 dark:hover:bg-green-900/20"
                            title={t('devices.settings.turnServer.test')}
                          >
                            {testingIndex === index ? (
                              <Loader2 className="h-4 w-4 animate-spin" />
                            ) : (
                              <TestTube className="h-4 w-4" />
                            )}
                          </Button>
                          <Button
                            variant="ghost"
                            size="sm"
                            onClick={() => handleEditServer(index)}
                            className="text-blue-600 hover:text-blue-700 dark:text-blue-400 dark:hover:text-blue-300 hover:bg-blue-50 dark:hover:bg-blue-900/20"
                          >
                            <Edit2 className="h-4 w-4" />
                          </Button>
                          <Button
                            variant="ghost"
                            size="sm"
                            onClick={() => handleRemoveServer(index)}
                            className="text-red-500 hover:text-red-600 dark:text-red-400 dark:hover:text-red-300 hover:bg-red-50 dark:hover:bg-red-900/20"
                          >
                            <Trash2 className="h-4 w-4" />
                          </Button>
                        </div>
                      </div>
                    </div>
                  )}
                </div>
              )
            })
          )}

          <Button
            variant="outline"
            onClick={handleAddServer}
            className="w-full"
          >
            <Plus className="mr-2 h-4 w-4" />
            {t('devices.settings.turnServer.addServer')}
          </Button>

          <div className="flex gap-2 pt-4">
            <Button
              onClick={handleSave}
              disabled={isSaving || testingIndex !== null}
              className="flex-1"
            >
              <Save className="mr-2 h-4 w-4" />
              {isSaving ? t('devices.settings.turnServer.saving') : t('devices.settings.turnServer.save')}
            </Button>
            <Button
              variant="outline"
              onClick={loadTurnConfig}
              disabled={isSaving || testingIndex !== null}
            >
              {t('devices.settings.turnServer.reset')}
            </Button>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-sm">{t('devices.settings.turnServer.instructions.title')}</CardTitle>
        </CardHeader>
        <CardContent className="text-sm text-gray-600 dark:text-gray-400 space-y-2">
          <p>• {t('devices.settings.turnServer.instructions.point1')}</p>
          <p>• {t('devices.settings.turnServer.instructions.point2')}</p>
          <p>• {t('devices.settings.turnServer.instructions.point3')}</p>
          <p>• {t('devices.settings.turnServer.instructions.point4')}</p>
        </CardContent>
      </Card>
    </div>
  )
}

