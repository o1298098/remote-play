import { useState, useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import { useToast } from '@/hooks/use-toast'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Label } from '@/components/ui/label'
import { Input } from '@/components/ui/input'
import { Save, Settings } from 'lucide-react'
import { streamingService, type WebRTCConfig } from '@/service/streaming.service'

export default function WebRTCSettingsTab() {
  const { t } = useTranslation()
  const { toast } = useToast()
  const [config, setConfig] = useState<WebRTCConfig>({
    publicIp: '',
    icePortMin: null,
    icePortMax: null,
    turnServers: [],
  })
  const [isLoading, setIsLoading] = useState(true)
  const [isSaving, setIsSaving] = useState(false)

  useEffect(() => {
    loadConfig()
  }, [])

  const loadConfig = async () => {
    try {
      setIsLoading(true)
      const response = await streamingService.getWebRTCConfig()
      if (response.success && response.data) {
        setConfig({
          publicIp: response.data.publicIp || '',
          icePortMin: response.data.icePortMin ?? null,
          icePortMax: response.data.icePortMax ?? null,
          turnServers: response.data.turnServers || [],
        })
      }
    } catch (error) {
      console.error('加载 WebRTC 配置失败:', error)
      toast({
        title: t('devices.settings.webrtc.loadFailed'),
        description: t('devices.settings.webrtc.loadFailedDescription'),
        variant: 'destructive',
      })
    } finally {
      setIsLoading(false)
    }
  }

  const handleSave = async () => {
    try {
      setIsSaving(true)

      // 验证配置
      const icePortMin = config.icePortMin
      const icePortMax = config.icePortMax
      
      if (icePortMin !== null && icePortMin !== undefined) {
        if (icePortMin < 1024 || icePortMin > 65535) {
          toast({
            title: t('devices.settings.webrtc.saveFailed'),
            description: t('devices.settings.webrtc.invalidPortRange'),
            variant: 'destructive',
          })
          return
        }
      }
      
      if (icePortMax !== null && icePortMax !== undefined) {
        if (icePortMax < 1024 || icePortMax > 65535) {
          toast({
            title: t('devices.settings.webrtc.saveFailed'),
            description: t('devices.settings.webrtc.invalidPortRange'),
            variant: 'destructive',
          })
          return
        }
      }
      
      if (icePortMin !== null && icePortMin !== undefined && 
          icePortMax !== null && icePortMax !== undefined) {
        if (icePortMin > icePortMax) {
          toast({
            title: t('devices.settings.webrtc.saveFailed'),
            description: t('devices.settings.webrtc.portMinGreaterThanMax'),
            variant: 'destructive',
          })
          return
        }
      }

      const response = await streamingService.saveWebRTCConfig(config)

      if (response.success) {
        toast({
          title: t('devices.settings.webrtc.saveSuccess'),
          description: t('devices.settings.webrtc.saveSuccessDescription'),
        })
      } else {
        throw new Error(response.errorMessage || t('devices.settings.webrtc.saveFailed'))
      }
    } catch (error) {
      console.error('保存 WebRTC 配置失败:', error)
      toast({
        title: t('devices.settings.webrtc.saveFailed'),
        description: error instanceof Error ? error.message : t('devices.settings.webrtc.saveFailedDescription'),
        variant: 'destructive',
      })
    } finally {
      setIsSaving(false)
    }
  }

  if (isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <div className="text-center">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600 dark:border-blue-400 mx-auto mb-4"></div>
          <p className="text-gray-600 dark:text-gray-400">{t('devices.settings.webrtc.loading')}</p>
        </div>
      </div>
    )
  }

  return (
    <div className="space-y-4">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Settings className="h-5 w-5" />
            {t('devices.settings.webrtc.title')}
          </CardTitle>
          <CardDescription>
            {t('devices.settings.webrtc.description')}
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div>
            <Label htmlFor="publicIp">{t('devices.settings.webrtc.publicIp')}</Label>
            <Input
              id="publicIp"
              placeholder={t('devices.settings.webrtc.publicIpPlaceholder')}
              value={config.publicIp || ''}
              onChange={(e) => setConfig({ ...config, publicIp: e.target.value })}
              className="mt-1"
            />
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
              {t('devices.settings.webrtc.publicIpHint')}
            </p>
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div>
              <Label htmlFor="icePortMin">{t('devices.settings.webrtc.icePortMin')}</Label>
              <Input
                id="icePortMin"
                type="number"
                placeholder={t('devices.settings.webrtc.icePortMinPlaceholder')}
                value={config.icePortMin ?? ''}
                onChange={(e) => {
                  const value = e.target.value === '' ? null : parseInt(e.target.value, 10)
                  setConfig({ ...config, icePortMin: isNaN(value as number) ? null : value })
                }}
                className="mt-1"
                min={1024}
                max={65535}
              />
              <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                {t('devices.settings.webrtc.icePortMinHint')}
              </p>
            </div>

            <div>
              <Label htmlFor="icePortMax">{t('devices.settings.webrtc.icePortMax')}</Label>
              <Input
                id="icePortMax"
                type="number"
                placeholder={t('devices.settings.webrtc.icePortMaxPlaceholder')}
                value={config.icePortMax ?? ''}
                onChange={(e) => {
                  const value = e.target.value === '' ? null : parseInt(e.target.value, 10)
                  setConfig({ ...config, icePortMax: isNaN(value as number) ? null : value })
                }}
                className="mt-1"
                min={1024}
                max={65535}
              />
              <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                {t('devices.settings.webrtc.icePortMaxHint')}
              </p>
            </div>
          </div>

          <div className="flex gap-2 pt-4">
            <Button
              onClick={handleSave}
              disabled={isSaving}
              className="flex-1"
            >
              <Save className="mr-2 h-4 w-4" />
              {isSaving ? t('devices.settings.webrtc.saving') : t('devices.settings.webrtc.save')}
            </Button>
            <Button
              variant="outline"
              onClick={loadConfig}
              disabled={isSaving}
            >
              {t('devices.settings.webrtc.reset')}
            </Button>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-sm">{t('devices.settings.webrtc.instructions.title')}</CardTitle>
        </CardHeader>
        <CardContent className="text-sm text-gray-600 dark:text-gray-400 space-y-2">
          <p>• {t('devices.settings.webrtc.instructions.point1')}</p>
          <p>• {t('devices.settings.webrtc.instructions.point2')}</p>
          <p>• {t('devices.settings.webrtc.instructions.point3')}</p>
        </CardContent>
      </Card>
    </div>
  )
}

