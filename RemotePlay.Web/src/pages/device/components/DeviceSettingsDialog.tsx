import { useState, useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import { useToast } from '@/hooks/use-toast'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Settings, Monitor, Gauge, Network, Cpu } from 'lucide-react'
import {
  playStationService,
  type DeviceStreamingSettings,
  type DeviceStreamingOptions,
} from '@/service/playstation.service'

interface DeviceSettingsDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  deviceId: string
  deviceName: string
  defaultSettings?: DeviceStreamingSettings
  onSave?: (settings: DeviceStreamingSettings) => void
}

const DEFAULT_SETTINGS: DeviceStreamingSettings = {
  resolution: '720p',
  frameRate: '30',
  quality: 'default',
  streamType: 'H264',
}

const STREAM_TYPE_PREFERENCE = ['HEVC_HDR', 'HEVC', 'H265', 'H264']

const resolveStreamType = (
  incoming: string | undefined,
  optionList?: DeviceStreamingOptions['streamTypes']
) => {
  if (incoming && optionList?.some((option) => option.value === incoming)) {
    return incoming
  }

  if (optionList && optionList.length > 0) {
    for (const desired of STREAM_TYPE_PREFERENCE) {
      const matched =
        optionList.find((option) => option.value?.toUpperCase?.() === desired) ??
        optionList.find((option) => option.code?.toUpperCase?.() === desired)
      if (matched) {
        return matched.value
      }
    }
  }

  return optionList?.[0]?.value ?? incoming ?? DEFAULT_SETTINGS.streamType
}

const ensureStreamType = (
  incoming: DeviceStreamingSettings,
  availableOptions?: DeviceStreamingOptions | null
) => ({
  ...incoming,
  streamType: resolveStreamType(incoming.streamType, availableOptions?.streamTypes),
})

export function DeviceSettingsDialog({
  open,
  onOpenChange,
  deviceId,
  deviceName,
  defaultSettings,
  onSave,
}: DeviceSettingsDialogProps) {
  const { t } = useTranslation()
  const { toast } = useToast()
  const [settings, setSettings] = useState<DeviceStreamingSettings>(
    ensureStreamType(defaultSettings ?? DEFAULT_SETTINGS)
  )
  const [options, setOptions] = useState<DeviceStreamingOptions | null>(null)
  const [isSaving, setIsSaving] = useState(false)
  const [isLoading, setIsLoading] = useState(false)

  useEffect(() => {
    if (!open || !deviceId) {
      return
    }

    let cancelled = false

    const fetchSettings = async () => {
      setIsLoading(true)
      try {
        const response = await playStationService.getDeviceSettings(deviceId)
        if (!cancelled) {
          if (response.success && response.data) {
            setOptions(response.data.options)
            setSettings(ensureStreamType(response.data.settings, response.data.options))
          } else {
            toast({
              title: t('devices.settings.loadFailed'),
              description:
                response.errorMessage ?? t('devices.settings.loadFailedDescription'),
              variant: 'destructive',
            })
          }
        }
      } catch (error) {
        if (!cancelled) {
          console.error('加载设备设置失败:', error)
          toast({
            title: t('devices.settings.loadFailed'),
            description:
              error instanceof Error
                ? error.message
                : t('devices.settings.loadFailedDescription'),
            variant: 'destructive',
          })
        }
      } finally {
        if (!cancelled) {
          setIsLoading(false)
        }
      }
    }

    fetchSettings()

    return () => {
      cancelled = true
    }
  }, [open, deviceId, t, toast])

  useEffect(() => {
    if (!open && defaultSettings) {
      setSettings(ensureStreamType(defaultSettings, options))
    }
  }, [open, defaultSettings, options])

  const handleSave = async () => {
    setIsSaving(true)
    try {
      const response = await playStationService.updateDeviceSettings(deviceId, settings)
      if (response.success && response.data) {
        setOptions(response.data.options)
        const nextSettings = ensureStreamType(response.data.settings, response.data.options)
        setSettings(nextSettings)
        onSave?.(nextSettings)

        toast({
          title: t('devices.settings.saveSuccess'),
          description: t('devices.settings.saveSuccessDescription'),
        })

        onOpenChange(false)
      } else {
        toast({
          title: t('devices.settings.saveFailed'),
          description:
            response.errorMessage ?? t('devices.settings.saveFailedDescription'),
          variant: 'destructive',
        })
      }
    } catch (error) {
      console.error('保存设置失败:', error)
      toast({
        title: t('devices.settings.saveFailed'),
        description: error instanceof Error ? error.message : t('devices.settings.saveFailedDescription'),
        variant: 'destructive',
      })
    } finally {
      setIsSaving(false)
    }
  }

  const handleReset = () => {
    let nextSettings: DeviceStreamingSettings

    if (options) {
      const defaultResolution = options.resolutions[0]?.key ?? DEFAULT_SETTINGS.resolution
      const defaultFrameRate = options.frameRates[0]?.value ?? DEFAULT_SETTINGS.frameRate
      const defaultBitrate = options.bitrates[0]
      const defaultStreamType = resolveStreamType(undefined, options.streamTypes)

      nextSettings = {
        resolution: defaultResolution,
        frameRate: defaultFrameRate,
        bitrate: defaultBitrate?.bitrate,
        quality: defaultBitrate?.quality ?? DEFAULT_SETTINGS.quality,
        streamType: defaultStreamType,
      }
    } else if (defaultSettings) {
      nextSettings = ensureStreamType(defaultSettings)
    } else {
      nextSettings = DEFAULT_SETTINGS
    }

    setSettings(nextSettings)
    toast({
      title: t('devices.settings.reset'),
      description: t('devices.settings.resetDescription'),
    })
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="w-[calc(100vw-1.5rem)] max-w-[520px] max-h-[90vh] p-0 sm:rounded-3xl flex flex-col overflow-hidden">
        <DialogHeader className="px-4 sm:px-6 pt-5 sm:pt-6 pb-4 bg-white dark:bg-gray-950 border-b border-gray-100 dark:border-gray-800">
          <DialogTitle className="flex items-center gap-2 text-xl font-semibold">
            <div className="flex items-center justify-center w-8 h-8 rounded-lg bg-blue-100 dark:bg-blue-900/30">
              <Settings className="h-4 w-4 text-blue-600 dark:text-blue-400" />
            </div>
            {t('devices.settings.title')}
          </DialogTitle>
          <DialogDescription className="text-sm text-muted-foreground mt-2">
            {t('devices.settings.description', { name: deviceName })}
          </DialogDescription>
        </DialogHeader>

        <div className="flex-1 overflow-y-auto bg-white dark:bg-gray-950 px-4 sm:px-6 pb-6 space-y-6">
          {/* 分辨率 */}
          <div className="space-y-3">
            <Label htmlFor="resolution" className="text-sm font-medium flex items-center gap-2 text-foreground">
              <div className="flex items-center justify-center w-5 h-5 rounded bg-gray-100 dark:bg-gray-800">
                <Monitor className="h-3.5 w-3.5 text-gray-600 dark:text-gray-400" />
              </div>
              {t('devices.settings.resolution')}
            </Label>
            <Select
              value={settings.resolution ?? ''}
              onValueChange={(value) =>
                setSettings((prev) => ({
                  ...prev,
                  resolution: value,
                }))
              }
              disabled={isLoading || !options}
            >
              <SelectTrigger
                id="resolution"
                className="h-11 bg-white dark:bg-gray-800 border-gray-200 dark:border-gray-700 hover:border-blue-400 dark:hover:border-blue-600 focus:ring-2 focus:ring-blue-500 dark:focus:ring-blue-400"
              >
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {options?.resolutions.map((option) => (
                  <SelectItem key={option.key} value={option.key}>
                    {t(`devices.settings.options.resolution.${option.labelKey || option.key}`, {
                      defaultValue: option.label,
                    })}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <p className="text-xs text-muted-foreground leading-relaxed">
              {t('devices.settings.resolutionHint')}
            </p>
          </div>

          {/* 帧率 */}
          <div className="space-y-3">
            <Label htmlFor="frameRate" className="text-sm font-medium flex items-center gap-2 text-foreground">
              <div className="flex items-center justify-center w-5 h-5 rounded bg-gray-100 dark:bg-gray-800">
                <Gauge className="h-3.5 w-3.5 text-gray-600 dark:text-gray-400" />
              </div>
              {t('devices.settings.frameRate')}
            </Label>
            <Select
              value={settings.frameRate ?? ''}
              onValueChange={(value) =>
                setSettings((prev) => ({
                  ...prev,
                  frameRate: value,
                }))
              }
              disabled={isLoading || !options}
            >
              <SelectTrigger
                id="frameRate"
                className="h-11 bg-white dark:bg-gray-800 border-gray-200 dark:border-gray-700 hover:border-blue-400 dark:hover:border-blue-600 focus:ring-2 focus:ring-blue-500 dark:focus:ring-blue-400"
              >
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {options?.frameRates.map((option) => (
                  <SelectItem key={option.value} value={option.value}>
                    {t(`devices.settings.options.frameRate.${option.labelKey || option.value}`, {
                      defaultValue: option.label,
                    })}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <p className="text-xs text-muted-foreground leading-relaxed">
              {t('devices.settings.frameRateHint')}
            </p>
          </div>

          {/* 码率 */}
          <div className="space-y-3">
            <Label htmlFor="bitrate" className="text-sm font-medium flex items-center gap-2 text-foreground">
              <div className="flex items-center justify-center w-5 h-5 rounded bg-gray-100 dark:bg-gray-800">
                <Network className="h-3.5 w-3.5 text-gray-600 dark:text-gray-400" />
              </div>
              {t('devices.settings.bitrate')}
            </Label>
            <Select
              value={settings.bitrate ?? ''}
              onValueChange={(value) => {
                if (!options) return
                const matched = options.bitrates.find((item) => item.bitrate === value)
                setSettings((prev) => ({
                  ...prev,
                  bitrate: value,
                  quality: matched?.quality ?? prev.quality,
                }))
              }}
              disabled={isLoading || !options}
            >
              <SelectTrigger
                id="bitrate"
                className="h-11 bg-white dark:bg-gray-800 border-gray-200 dark:border-gray-700 hover:border-blue-400 dark:hover:border-blue-600 focus:ring-2 focus:ring-blue-500 dark:focus:ring-blue-400"
              >
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {options?.bitrates.map((option) => (
                  <SelectItem key={option.bitrate} value={option.bitrate}>
                    {t(`devices.settings.options.bitrate.${option.labelKey || option.quality}`, {
                      defaultValue: option.label,
                    })}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <p className="text-xs text-muted-foreground leading-relaxed">
              {t('devices.settings.bitrateHint')}
            </p>
          </div>

          {/* 串流编码 */}
          <div className="space-y-3">
            <Label htmlFor="streamType" className="text-sm font-medium flex items-center gap-2 text-foreground">
              <div className="flex items-center justify-center w-5 h-5 rounded bg-gray-100 dark:bg-gray-800">
                <Cpu className="h-3.5 w-3.5 text-gray-600 dark:text-gray-400" />
              </div>
              {t('devices.settings.streamType')}
            </Label>
            <Select
              value={settings.streamType ?? ''}
              onValueChange={(value) =>
                setSettings((prev) => ({
                  ...prev,
                  streamType: value,
                }))
              }
              disabled={isLoading || !options}
            >
              <SelectTrigger
                id="streamType"
                className="h-11 bg-white dark:bg-gray-800 border-gray-200 dark:border-gray-700 hover:border-blue-400 dark:hover:border-blue-600 focus:ring-2 focus:ring-blue-500 dark:focus:ring-blue-400"
              >
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {options?.streamTypes.map((option) => (
                  <SelectItem key={option.value} value={option.value}>
                    {t(`devices.settings.options.streamType.${option.labelKey || option.value}`, {
                      defaultValue: option.label,
                    })}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <p className="text-xs text-muted-foreground leading-relaxed">
              {t('devices.settings.streamTypeHint')}
            </p>
          </div>

        </div>

        <DialogFooter className="shrink-0 gap-2 sm:gap-2 px-4 sm:px-6 pb-5 sm:pb-6 pt-4 border-t border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-950">
          <Button 
            variant="outline" 
            onClick={handleReset} 
            disabled={isSaving || isLoading || !options}
            className="flex-1 sm:flex-initial"
          >
            {t('devices.settings.reset')}
          </Button>
          <Button 
            variant="outline" 
            onClick={() => onOpenChange(false)} 
            disabled={isSaving}
            className="flex-1 sm:flex-initial"
          >
            {t('common.cancel')}
          </Button>
          <Button 
            onClick={handleSave} 
            disabled={isSaving || isLoading || !options}
            className="flex-1 sm:flex-initial bg-blue-600 hover:bg-blue-700 dark:bg-blue-600 dark:hover:bg-blue-700 text-white"
          >
            {isSaving ? (
              <>
                <span className="mr-2">{t('common.loading')}</span>
              </>
            ) : (
              t('common.save')
            )}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

