import { useState, useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import { useToast } from '@/hooks/use-toast'
import { playStationService, type ConsoleInfo } from '@/service/playstation.service'
import { profileService } from '@/service/profile.service'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Loader2, Search, Wifi, ChevronDown, Sparkles, ExternalLink, Key } from 'lucide-react'
import { PS5Icon } from '@/components/icons/PS5Icon'
import { PS4Icon } from '@/components/icons/PS4Icon'

interface BindDeviceDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  onSuccess?: () => void
  initialDevice?: {
    hostIp?: string
    deviceName?: string
    hostId?: string
  }
}

export function BindDeviceDialog({
  open,
  onOpenChange,
  onSuccess,
  initialDevice,
}: BindDeviceDialogProps) {
  const { t } = useTranslation()
  const { toast } = useToast()
  const [step, setStep] = useState<'discover' | 'bind'>('discover')
  const [isDiscovering, setIsDiscovering] = useState(false)
  const [isBinding, setIsBinding] = useState(false)
  const [discoveredDevices, setDiscoveredDevices] = useState<ConsoleInfo[]>([])
  const [selectedDevice, setSelectedDevice] = useState<ConsoleInfo | null>(null)
  const [hostIp, setHostIp] = useState('')
  const [accountId, setAccountId] = useState('')
  const [pin, setPin] = useState('')
  const [deviceName, setDeviceName] = useState('')
  const [needsRegistration, setNeedsRegistration] = useState(false)
  const [hasAutoScanned, setHasAutoScanned] = useState(false)
  const [isGettingAccountId, setIsGettingAccountId] = useState(false)
  const [oauthWindow, setOauthWindow] = useState<Window | null>(null)
  const [showCallbackInput, setShowCallbackInput] = useState(false)
  const [callbackUrl, setCallbackUrl] = useState('')

  // 当对话框打开时，如果有预填充设备信息，直接跳转到绑定步骤
  useEffect(() => {
    if (open && initialDevice) {
      if (initialDevice.hostIp) {
        setHostIp(initialDevice.hostIp)
      }
      if (initialDevice.deviceName) {
        setDeviceName(initialDevice.deviceName)
      }
      setStep('bind')
      // 如果提供了 IP，尝试发现该设备（延迟执行以确保状态已更新）
      if (initialDevice.hostIp) {
        const ipToDiscover = initialDevice.hostIp
        setTimeout(async () => {
          setIsDiscovering(true)
          try {
            const response = await playStationService.discoverDevice(ipToDiscover, 3000)
            if (response.success && response.result) {
              setSelectedDevice(response.result)
              setHostIp(response.result.ip)
            }
          } catch (error) {
            console.error('设备发现错误:', error)
          } finally {
            setIsDiscovering(false)
          }
        }, 100)
      }
    } else if (open && step === 'discover' && !hasAutoScanned && !isDiscovering && !initialDevice) {
      handleDiscover()
      setHasAutoScanned(true)
    }

    if (open) {
      if (initialDevice) {
        setNeedsRegistration(true)
        setAccountId('')
        setPin('')
      }
    }
    // 对话框关闭时重置状态
    if (!open) {
      setHasAutoScanned(false)
      if (initialDevice) {
        // 重置预填充的值
        setHostIp('')
        setDeviceName('')
        setStep('discover')
      }
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, step, hasAutoScanned, initialDevice])

  const handleDiscover = async () => {
    setIsDiscovering(true)
    try {
      const response = await playStationService.discoverDevices(3000)
      console.log('发现设备响应:', response)
      
      if (response.success) {
        // 检查result是否存在且是数组
        if (response.result && Array.isArray(response.result)) {
          setDiscoveredDevices(response.result)
          if (response.result.length > 0) {
            // 如果发现设备，不自动跳转到绑定步骤，让用户选择
            // setStep('bind')
            // setSelectedDevice(response.result[0])
            // setHostIp(response.result[0].ip)
            // setDeviceName(response.result[0].name)
          }
        } else {
          // result为空或不是数组
          setDiscoveredDevices([])
        }
      } else {
        toast({
          title: t('devices.bindDevice.errors.discoverFailed'),
          description: response.errorMessage || response.message || t('devices.bindDevice.errors.discoverFailedHint'),
          variant: 'destructive',
        })
      }
    } catch (error) {
      console.error('设备发现错误:', error)
      toast({
        title: t('devices.bindDevice.errors.discoverFailed'),
        description: error instanceof Error ? error.message : t('devices.bindDevice.errors.discoverFailedHint'),
        variant: 'destructive',
      })
    } finally {
      setIsDiscovering(false)
    }
  }

  const handleDiscoverByIp = async () => {
    if (!hostIp.trim()) {
      toast({
        title: t('devices.bindDevice.errors.ipRequired'),
        description: t('devices.bindDevice.errors.ipRequiredHint'),
        variant: 'destructive',
      })
      return
    }

    setIsDiscovering(true)
    try {
      const response = await playStationService.discoverDevice(hostIp.trim())
      if (response.success && response.result) {
        setSelectedDevice(response.result)
        setHostIp(response.result.ip)
        setDeviceName(response.result.name || '')
        setStep('bind')
      }
    } catch (error) {
      toast({
        title: t('devices.bindDevice.errors.discoverFailed'),
        description: error instanceof Error ? error.message : t('devices.bindDevice.errors.discoverFailedHint'),
        variant: 'destructive',
      })
    } finally {
      setIsDiscovering(false)
    }
  }

  const handleBind = async () => {
    if (!hostIp.trim()) {
      toast({
        title: t('devices.bindDevice.errors.ipInvalid'),
        variant: 'destructive',
      })
      return
    }

    if (needsRegistration) {
      if (!accountId.trim() || !pin.trim()) {
        toast({
          title: t('devices.bindDevice.errors.accountIdAndPinRequired'),
          description: t('devices.bindDevice.errors.accountIdAndPinRequiredHint'),
          variant: 'destructive',
        })
        return
      }
    }

    setIsBinding(true)
    try {
      const response = await playStationService.bindDevice({
        hostIp: hostIp.trim(),
        accountId: needsRegistration ? accountId.trim() : undefined,
        pin: needsRegistration ? pin.trim() : undefined,
        deviceName: deviceName.trim() || undefined,
      })

      if (response.success) {
        toast({
          title: t('devices.bindDevice.errors.bindSuccess'),
          description: t('devices.bindDevice.errors.bindSuccessHint'),
        })
        onSuccess?.()
        handleClose()
      } else {
        toast({
          title: t('devices.bindDevice.errors.bindFailed'),
          description: response.errorMessage || response.message || t('devices.bindDevice.errors.bindFailedHint'),
          variant: 'destructive',
        })
      }
    } catch (error) {
      toast({
        title: t('devices.bindDevice.errors.bindFailed'),
        description: error instanceof Error ? error.message : t('devices.bindDevice.errors.bindFailedHint'),
        variant: 'destructive',
      })
    } finally {
      setIsBinding(false)
    }
  }

  const handleClose = () => {
    setStep('discover')
    setDiscoveredDevices([])
    setSelectedDevice(null)
    setHostIp('')
    setAccountId('')
    setPin('')
    setDeviceName('')
    setNeedsRegistration(false)
    setHasAutoScanned(false)
    setShowCallbackInput(false)
    setCallbackUrl('')
    // 清理OAuth窗口
    if (oauthWindow) {
      oauthWindow.close()
      setOauthWindow(null)
    }
    onOpenChange(false)
  }

  const handleRescan = () => {
    setDiscoveredDevices([])
    handleDiscover()
  }

  // 处理获取账户ID
  const handleGetAccountId = async () => {
    setIsGettingAccountId(true)
    try {
      // PSN只允许使用固定的回调URL，不能自定义
      // 使用默认的PSN回调URL
      const loginUrlResponse = await profileService.getLoginUrl()
      if (!loginUrlResponse.success || !loginUrlResponse.result?.loginUrl) {
        toast({
          title: t('devices.bindDevice.errors.getLoginUrlFailed'),
          description: loginUrlResponse.errorMessage || loginUrlResponse.message || t('devices.bindDevice.errors.bindFailedHint'),
          variant: 'destructive',
        })
        setIsGettingAccountId(false)
        return
      }

      const loginUrl = loginUrlResponse.result.loginUrl
      
      // 打开新窗口进行OAuth登录
      const width = 600
      const height = 700
      const left = window.screenX + (window.outerWidth - width) / 2
      const top = window.screenY + (window.outerHeight - height) / 2
      
      const popup = window.open(
        loginUrl,
        t('devices.bindDevice.oauth.loginTitle'),
        `width=${width},height=${height},left=${left},top=${top},toolbar=no,location=no,status=no,menubar=no,scrollbars=yes,resizable=yes`
      )
      
      if (!popup) {
        toast({
          title: t('devices.bindDevice.errors.cannotOpenWindow'),
          description: t('devices.bindDevice.errors.cannotOpenWindowHint'),
          variant: 'destructive',
        })
        setIsGettingAccountId(false)
        return
      }

      setOauthWindow(popup)

      // 监听弹出窗口的URL变化和消息（尝试自动获取回调URL）
      let lastUrl = ''
      let urlCheckAttempts = 0
      const maxAttempts = 100 // 最多尝试30秒（300ms * 100）
      
      const checkUrlInterval = setInterval(() => {
        urlCheckAttempts++
        
        try {
          if (popup.closed) {
            clearInterval(checkUrlInterval)
            setIsGettingAccountId(false)
            return
          }

          // 尝试读取弹出窗口的URL
          const currentUrl = popup.location.href
          
          // 检查是否是PSN的回调URL
          if (currentUrl && (
            currentUrl.includes('remoteplay.dl.playstation.net/remoteplay/redirect') ||
            currentUrl.includes('code=') ||
            currentUrl.includes('error=')
          )) {
            // 检测到回调URL
            if (currentUrl !== lastUrl) {
              lastUrl = currentUrl
              
              // 检查是否包含code参数
              if (currentUrl.includes('code=')) {
                clearInterval(checkUrlInterval)
                
                // 确保URL格式正确（PSN格式）
                let finalUrl = currentUrl
                if (!currentUrl.includes('remoteplay.dl.playstation.net')) {
                  // 如果不在PSN域名，尝试提取code并构建正确的URL
                  try {
                    const urlObj = new URL(currentUrl)
                    const code = urlObj.searchParams.get('code')
                    const state = urlObj.searchParams.get('state')
                    if (code) {
                      finalUrl = `https://remoteplay.dl.playstation.net/remoteplay/redirect?code=${code}${state ? `&state=${state}` : ''}`
                    }
                  } catch (e) {
                    console.error('解析URL失败:', e)
                  }
                }
                
                handleCallbackUrl(finalUrl)
                if (popup && !popup.closed) {
                  setTimeout(() => {
                    popup.close()
                    setOauthWindow(null)
                  }, 500)
                }
                setIsGettingAccountId(false)
                setShowCallbackInput(false)
                return
              } else if (currentUrl.includes('error=')) {
                // 处理错误
                clearInterval(checkUrlInterval)
                try {
                  const urlObj = new URL(currentUrl)
                  const error = urlObj.searchParams.get('error')
                  const errorDesc = urlObj.searchParams.get('error_description')
                  toast({
                    title: t('devices.bindDevice.errors.loginFailed'),
                    description: errorDesc || error || t('devices.bindDevice.errors.loginFailedHint'),
                    variant: 'destructive',
                  })
                } catch (e) {
                  toast({
                    title: t('devices.bindDevice.errors.loginFailed'),
                    description: t('devices.bindDevice.errors.loginFailedHint'),
                    variant: 'destructive',
                  })
                }
                if (popup && !popup.closed) {
                  popup.close()
                  setOauthWindow(null)
                }
                setIsGettingAccountId(false)
                setShowCallbackInput(false)
                return
              }
            }
          }
          
          // 尝试读取页面内容（检查是否有特定文本，如"redirect"）
          try {
            const doc = popup.document
            if (doc && doc.body) {
              // 检查页面标题或内容
              const pageTitle = doc.title || ''
              const bodyText = (doc.body.innerText || doc.body.textContent || '').toLowerCase()
              
              // 如果页面包含"redirect"文字，说明已经跳转到回调页面
              // 此时URL应该已经包含code参数
              if (bodyText.includes('redirect') || pageTitle.toLowerCase().includes('redirect')) {
                // 再次尝试读取URL（可能此时已经可以读取了）
                try {
                  const redirectUrl = popup.location.href
                  if (redirectUrl && redirectUrl.includes('code=')) {
                    clearInterval(checkUrlInterval)
                    
                    // 确保URL格式正确
                    let finalUrl = redirectUrl
                    if (!redirectUrl.startsWith('http')) {
                      finalUrl = `https://${redirectUrl}`
                    }
                    
                    handleCallbackUrl(finalUrl)
                    if (popup && !popup.closed) {
                      setTimeout(() => {
                        popup.close()
                        setOauthWindow(null)
                      }, 500)
                    }
                    setIsGettingAccountId(false)
                    setShowCallbackInput(false)
                    return
                  }
                } catch (urlError) {
                  // URL读取失败，但页面已经显示"redirect"，说明已经跳转到回调页面
                  // 自动显示输入框，提示用户复制URL
                  if (!showCallbackInput) {
                    setShowCallbackInput(true)
                    toast({
                      title: t('devices.bindDevice.oauth.callbackDetected'),
                      description: t('devices.bindDevice.oauth.callbackDetectedHint'),
                      duration: 6000,
                    })
                  }
                }
              }
              
              // 也尝试从页面文本中提取URL
              const urlMatch = bodyText.match(/remoteplay\.dl\.playstation\.net\/remoteplay\/redirect[^\s]*/i)
              if (urlMatch) {
                const foundUrl = urlMatch[0]
                if (foundUrl.includes('code=')) {
                  clearInterval(checkUrlInterval)
                  handleCallbackUrl(`https://${foundUrl}`)
                  if (popup && !popup.closed) {
                    setTimeout(() => {
                      popup.close()
                      setOauthWindow(null)
                    }, 500)
                  }
                  setIsGettingAccountId(false)
                  setShowCallbackInput(false)
                  return
                }
              }
            }
          } catch (e) {
            // 跨域，无法读取页面内容
            // 这是正常的
          }
          
        } catch (e) {
          // 跨域错误，无法读取URL
          // 这是正常的，因为PSN的页面是跨域的
          // 继续等待用户手动操作
        }
        
        // 如果尝试次数过多，停止检测
        if (urlCheckAttempts >= maxAttempts) {
          clearInterval(checkUrlInterval)
          // 不关闭输入框，让用户可以手动输入
        }
      }, 300)
      
      // 监听来自弹出窗口的消息（如果回调页面支持postMessage）
      const messageHandler = (event: MessageEvent) => {
        // 只接受来自PSN域名的消息（如果可能）
        if (event.data && event.data.type === 'PSN_CALLBACK_URL') {
          const callbackUrl = event.data.url
          if (callbackUrl && callbackUrl.includes('code=')) {
            clearInterval(checkUrlInterval)
            window.removeEventListener('message', messageHandler)
            handleCallbackUrl(callbackUrl)
            if (popup && !popup.closed) {
              setTimeout(() => {
                popup.close()
                setOauthWindow(null)
              }, 500)
            }
            setIsGettingAccountId(false)
            setShowCallbackInput(false)
          }
        }
      }
      
      window.addEventListener('message', messageHandler)
      
      // 清理时移除消息监听器
      setTimeout(() => {
        window.removeEventListener('message', messageHandler)
      }, 5 * 60 * 1000)

      // 显示回调URL输入框
      setShowCallbackInput(true)
      
      // 提示用户
      toast({
        title: t('devices.bindDevice.errors.loginHint'),
        description: t('devices.bindDevice.errors.loginHintDescription'),
        duration: 5000,
      })

      // 检查窗口是否关闭
      const checkClosed = setInterval(() => {
        if (popup.closed) {
          clearInterval(checkUrlInterval)
          clearInterval(checkClosed)
          setOauthWindow(null)
          // 不自动关闭输入框，让用户可以手动输入
        }
      }, 500)

      // 5分钟后清理
      setTimeout(() => {
        clearInterval(checkUrlInterval)
        clearInterval(checkClosed)
        if (popup && !popup.closed) {
          // 不自动关闭，让用户完成登录
        }
        // 不自动关闭输入框
      }, 5 * 60 * 1000)

    } catch (error) {
      console.error('获取账户ID错误:', error)
      toast({
        title: t('devices.bindDevice.errors.getAccountIdFailed'),
        description: error instanceof Error ? error.message : t('devices.bindDevice.errors.bindFailedHint'),
        variant: 'destructive',
      })
      setIsGettingAccountId(false)
    }
  }

  // 处理回调URL
  const handleCallbackUrl = async (url: string) => {
    if (!url.trim()) {
      toast({
        title: t('devices.bindDevice.errors.callbackUrlRequired'),
        variant: 'destructive',
      })
      return
    }

    setIsGettingAccountId(true)
    try {
      const newUserResponse = await profileService.newUser(url.trim())
      
      if (newUserResponse.success && newUserResponse.result) {
        // 填充账户ID（Base64编码的ID）
        setAccountId(newUserResponse.result.id)
        setShowCallbackInput(false)
        setCallbackUrl('')
        toast({
          title: t('devices.bindDevice.errors.getAccountIdSuccess'),
          description: t('devices.bindDevice.errors.getAccountIdSuccessHint', { name: newUserResponse.result.name }),
        })
      } else {
        toast({
          title: t('devices.bindDevice.errors.getAccountIdFailed'),
          description: newUserResponse.errorMessage || newUserResponse.message || t('devices.bindDevice.errors.bindFailedHint'),
          variant: 'destructive',
        })
      }
    } catch (error) {
      console.error('创建用户失败:', error)
      toast({
        title: t('devices.bindDevice.errors.getAccountIdFailed'),
        description: error instanceof Error ? error.message : t('devices.bindDevice.errors.bindFailedHint'),
        variant: 'destructive',
      })
    } finally {
      setIsGettingAccountId(false)
    }
  }

  return (
    <Dialog open={open} onOpenChange={handleClose}>
      <DialogContent className="sm:max-w-[550px] overflow-hidden p-0">
        {/* 头部 */}
        <div className="relative bg-white dark:bg-gray-950 border-b border-gray-200 dark:border-gray-800 p-6">
          <DialogHeader className="relative z-10">
            <DialogTitle className="text-2xl font-bold flex items-center gap-3 text-gray-900 dark:text-white">
              <div className="p-2 bg-blue-50 dark:bg-blue-900/30 rounded-lg">
                <Sparkles className="h-5 w-5 text-blue-600 dark:text-blue-400" />
              </div>
              {t('devices.bindDevice.title')}
            </DialogTitle>
            <DialogDescription className="text-gray-600 dark:text-gray-400 mt-2">
              {step === 'discover'
                ? t('devices.bindDevice.description.discover')
                : t('devices.bindDevice.description.bind')}
            </DialogDescription>
          </DialogHeader>
        </div>

        <div className="p-6 space-y-6 max-h-[60vh] overflow-y-auto bg-white dark:bg-gray-950">

        {step === 'discover' ? (
          <div className="space-y-4">
            {/* 自动扫描状态显示 */}
            {isDiscovering && (
              <div className="flex items-center justify-center py-12">
                <div className="flex flex-col items-center gap-4">
                  <div className="relative">
                    <div className="absolute inset-0 bg-blue-100 dark:bg-blue-900/50 rounded-full blur-xl animate-pulse"></div>
                    <div className="relative z-10">
                      <Loader2 className="h-12 w-12 animate-spin text-blue-600 dark:text-blue-400" />
                    </div>
                  </div>
                  <div className="text-center space-y-1">
                    <p className="text-sm font-medium text-gray-900 dark:text-white">{t('devices.bindDevice.discover.scanning')}</p>
                    <p className="text-xs text-gray-500 dark:text-gray-400">{t('devices.bindDevice.discover.scanningHint')}</p>
                  </div>
                </div>
              </div>
            )}

            {/* 发现的设备列表 */}
            {!isDiscovering && discoveredDevices.length > 0 && (
              <div className="space-y-3">
                <div className="flex items-center justify-between">
                  <Label className="text-base font-semibold flex items-center gap-2">
                    <div className="h-2 w-2 rounded-full bg-green-500 animate-pulse"></div>
                    {t('devices.bindDevice.discover.foundDevices')} ({discoveredDevices.length})
                  </Label>
                  <Button
                    onClick={handleRescan}
                    variant="ghost"
                    size="sm"
                    className="text-xs hover:bg-blue-50 dark:hover:bg-blue-900/30 hover:text-blue-600 dark:hover:text-blue-400 transition-colors"
                  >
                    <Wifi className="mr-1.5 h-3.5 w-3.5" />
                    {t('devices.bindDevice.discover.rescan')}
                  </Button>
                </div>
                <div className="space-y-2.5 max-h-64 overflow-y-auto pr-1 pt-1">
                  {discoveredDevices.map((device) => {
                    const isPS5 = device.hostType?.toLowerCase().includes('ps5') || device.hostType?.toLowerCase().includes('5')
                    return (
                      <div
                        key={device.uuid}
                        className="group relative p-4 bg-white dark:bg-gray-800 border-2 border-gray-200 dark:border-gray-700 rounded-xl cursor-pointer hover:border-blue-500 dark:hover:border-blue-400 hover:shadow-md hover:shadow-blue-50 dark:hover:shadow-blue-900/20 transition-all duration-200 hover:-translate-y-0.5"
                        onClick={() => {
                          setSelectedDevice(device)
                          setHostIp(device.ip)
                          setDeviceName(device.name)
                          setStep('bind')
                        }}
                      >
                        <div className="flex items-center gap-4">
                          {/* 设备图标 */}
                          <div className="flex-shrink-0 w-12 h-12 rounded-lg bg-blue-600 flex items-center justify-center text-white shadow-sm group-hover:bg-blue-700 group-hover:scale-105 transition-all">
                            {isPS5 ? (
                              <PS5Icon className="h-7 w-7" />
                            ) : (
                              <PS4Icon className="h-7 w-7" />
                            )}
                          </div>
                          {/* 设备信息 */}
                          <div className="flex-1 min-w-0">
                            <div className="font-semibold text-base text-gray-900 dark:text-white group-hover:text-blue-600 dark:group-hover:text-blue-400 transition-colors">
                              {device.name}
                            </div>
                            <div className="text-sm text-gray-500 dark:text-gray-400 mt-1 flex items-center gap-2">
                              <span className="inline-flex items-center gap-1">
                                <span className="h-1.5 w-1.5 rounded-full bg-green-500"></span>
                                {device.ip}
                              </span>
                              <span>•</span>
                              <span className="inline-flex items-center px-2 py-0.5 rounded-md bg-blue-50 dark:bg-blue-900/30 text-blue-700 dark:text-blue-300 text-xs font-medium">
                                {device.hostType || t('devices.bindDevice.discover.unknownType')}
                              </span>
                            </div>
                          </div>
                          {/* 箭头图标 */}
                          <div className="flex-shrink-0">
                            <div className="p-2 rounded-lg bg-gray-50 dark:bg-gray-700 group-hover:bg-blue-50 dark:group-hover:bg-blue-900/30 transition-colors">
                              <ChevronDown className="h-5 w-5 text-gray-400 dark:text-gray-500 group-hover:text-blue-600 dark:group-hover:text-blue-400 rotate-[-90deg] transition-colors" />
                            </div>
                          </div>
                        </div>
                      </div>
                    )
                  })}
                </div>
              </div>
            )}

            {/* 未发现设备时的提示 */}
            {!isDiscovering && discoveredDevices.length === 0 && hasAutoScanned && (
              <div className="text-center py-10 space-y-4">
                <div className="inline-flex items-center justify-center w-16 h-16 rounded-full bg-gray-100 dark:bg-gray-800 mb-2">
                  <Wifi className="h-8 w-8 text-gray-400 dark:text-gray-500" />
                </div>
                <div className="text-muted-foreground space-y-1">
                  <p className="text-base font-medium text-gray-900 dark:text-white">{t('devices.bindDevice.discover.noDevices')}</p>
                  <p className="text-sm text-gray-500 dark:text-gray-400">{t('devices.bindDevice.discover.noDevicesHint')}</p>
                </div>
                <Button
                  onClick={handleRescan}
                  variant="outline"
                  size="sm"
                  className="hover:bg-blue-50 hover:border-blue-300 hover:text-blue-600"
                >
                  <Wifi className="mr-2 h-4 w-4" />
                  {t('devices.bindDevice.discover.rescan')}
                </Button>
              </div>
            )}

            {/* 手动输入IP地址 */}
            <div className="space-y-3 pt-4 border-t border-gray-200 dark:border-gray-700">
              <Label htmlFor="hostIp" className="text-sm font-medium flex items-center gap-2">
                <Search className="h-4 w-4 text-gray-400 dark:text-gray-500" />
                {t('devices.bindDevice.discover.manualIp')}
              </Label>
              <div className="flex gap-2">
                <Input
                  id="hostIp"
                  placeholder={t('devices.bindDevice.discover.manualIpPlaceholder')}
                  value={hostIp}
                  onChange={(e) => setHostIp(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') {
                      handleDiscoverByIp()
                    }
                  }}
                  disabled={isDiscovering}
                  className="flex-1 focus:border-blue-500 focus:ring-blue-500"
                />
                <Button
                  onClick={handleDiscoverByIp}
                  disabled={isDiscovering}
                  variant="outline"
                  className="hover:bg-blue-50 dark:hover:bg-blue-900/30 hover:border-blue-300 dark:hover:border-blue-600 hover:text-blue-600 dark:hover:text-blue-400 transition-colors"
                >
                  <Search className="h-4 w-4" />
                </Button>
              </div>
              <p className="text-xs text-gray-500 dark:text-gray-400">
                {t('devices.bindDevice.discover.manualIpHint')}
              </p>
            </div>
          </div>
        ) : (
          <div className="space-y-4">
            {selectedDevice && (
              <div className="p-4 bg-blue-50 dark:bg-blue-900/20 border-2 border-blue-200 dark:border-blue-800 rounded-xl">
                <div className="flex items-center gap-3">
                  <div className="flex-shrink-0 w-10 h-10 rounded-lg bg-blue-600 dark:bg-blue-600 flex items-center justify-center text-white">
                    {selectedDevice.hostType?.toLowerCase().includes('ps5') || selectedDevice.hostType?.toLowerCase().includes('5') ? (
                      <PS5Icon className="h-6 w-6" />
                    ) : (
                      <PS4Icon className="h-6 w-6" />
                    )}
                  </div>
                  <div className="flex-1">
                    <div className="font-semibold text-gray-900 dark:text-white">{selectedDevice.name}</div>
                    <div className="text-sm text-gray-600 dark:text-gray-400 mt-0.5">
                      {selectedDevice.ip} • {selectedDevice.hostType || t('devices.bindDevice.discover.unknownType')}
                    </div>
                  </div>
                </div>
              </div>
            )}

            <div className="space-y-2">
              <Label htmlFor="deviceName">{t('devices.bindDevice.bind.deviceName')}</Label>
              <Input
                id="deviceName"
                placeholder={t('devices.bindDevice.bind.deviceNamePlaceholder')}
                value={deviceName}
                onChange={(e) => setDeviceName(e.target.value)}
              />
            </div>

            <div className="space-y-2">
              <div className="flex items-center space-x-2">
                <input
                  type="checkbox"
                  id="needsRegistration"
                  checked={needsRegistration}
                  onChange={(e) => setNeedsRegistration(e.target.checked)}
                  className="rounded border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 text-blue-600 dark:text-blue-400 focus:ring-2 focus:ring-blue-500 dark:focus:ring-blue-400"
                />
                <Label htmlFor="needsRegistration" className="cursor-pointer">
                  {t('devices.bindDevice.bind.needsRegistration')}
                </Label>
              </div>
              <p className="text-xs text-muted-foreground">
                {t('devices.bindDevice.bind.needsRegistrationHint')}
              </p>
            </div>

            {needsRegistration && (
              <>
                <div className="space-y-2">
                  <div className="flex items-center justify-between">
                    <Label htmlFor="accountId" className="flex items-center gap-2">
                      <Key className="h-4 w-4 text-gray-400 dark:text-gray-500" />
                      {t('devices.bindDevice.bind.accountId')}
                    </Label>
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      onClick={handleGetAccountId}
                      disabled={isGettingAccountId}
                      className="text-xs h-7 px-3 hover:bg-blue-50 hover:border-blue-300 hover:text-blue-600"
                    >
                      {isGettingAccountId ? (
                        <>
                          <Loader2 className="mr-1.5 h-3 w-3 animate-spin" />
                          {t('devices.bindDevice.bind.gettingAccountId')}
                        </>
                      ) : (
                        <>
                          <ExternalLink className="mr-1.5 h-3 w-3" />
                          {t('devices.bindDevice.bind.getAccountId')}
                        </>
                      )}
                    </Button>
                  </div>
                  <Input
                    id="accountId"
                    placeholder={t('devices.bindDevice.bind.accountIdPlaceholder')}
                    value={accountId}
                    onChange={(e) => setAccountId(e.target.value)}
                    className="focus:border-blue-500 focus:ring-blue-500"
                  />
                  <p className="text-xs text-muted-foreground">
                    {t('devices.bindDevice.bind.accountIdHint')}
                  </p>
                  
                  {/* 正在获取账户ID的提示 */}
                  {isGettingAccountId && !showCallbackInput && (
                    <div className="mt-3 p-3 bg-blue-50 dark:bg-blue-900/20 border border-blue-200 dark:border-blue-800 rounded-lg">
                      <div className="flex items-center gap-3">
                        <Loader2 className="h-4 w-4 animate-spin text-blue-600 dark:text-blue-400" />
                        <div className="flex-1">
                          <p className="text-xs font-medium text-blue-900 dark:text-blue-100">{t('devices.bindDevice.bind.gettingAccountIdTitle')}</p>
                          <p className="text-xs text-blue-700 dark:text-blue-300 mt-1">{t('devices.bindDevice.bind.gettingAccountIdHint')}</p>
                        </div>
                      </div>
                    </div>
                  )}

                  {/* 回调URL输入框（当需要手动输入时显示） */}
                  {showCallbackInput && (
                    <div className="mt-3 p-4 bg-amber-50 border border-amber-200 rounded-lg space-y-3">
                      <div className="flex items-start gap-2">
                        <div className="flex-shrink-0 mt-0.5">
                          <div className="w-5 h-5 rounded-full bg-amber-100 flex items-center justify-center">
                            <span className="text-xs font-bold text-amber-600">!</span>
                          </div>
                        </div>
                        <div className="flex-1">
                          <Label className="text-xs font-semibold text-amber-900 block mb-1">
                            {t('devices.bindDevice.oauth.callbackSteps.title')}
                          </Label>
                          <ol className="text-xs text-amber-800 space-y-1 list-decimal list-inside">
                            <li>{t('devices.bindDevice.oauth.callbackSteps.step1')}</li>
                            <li>{t('devices.bindDevice.oauth.callbackSteps.step2', { domain: 'remoteplay.dl.playstation.net' })}</li>
                            <li>{t('devices.bindDevice.oauth.callbackSteps.step3')}</li>
                            <li>{t('devices.bindDevice.oauth.callbackSteps.step4', { codeParam: 'code=' })}</li>
                          </ol>
                          <div className="mt-2 p-2 bg-amber-100 rounded text-xs text-amber-900">
                            <span dangerouslySetInnerHTML={{
                              __html: t('devices.bindDevice.oauth.callbackSteps.hint', { 
                                selectKey1: '<kbd class="px-1.5 py-0.5 bg-white rounded border border-amber-300 text-[10px]">Ctrl+L</kbd>', 
                                selectKey2: '<kbd class="px-1.5 py-0.5 bg-white rounded border border-amber-300 text-[10px]">F6</kbd>', 
                                copyKey: '<kbd class="px-1.5 py-0.5 bg-white rounded border border-amber-300 text-[10px]">Ctrl+C</kbd>' 
                              })
                            }} />
                          </div>
                        </div>
                      </div>
                      <div className="flex gap-2">
                        <div className="flex-1 relative">
                          <Input
                            placeholder={t('devices.bindDevice.oauth.callbackUrlPlaceholder')}
                            value={callbackUrl}
                            onChange={(e) => setCallbackUrl(e.target.value)}
                            onPaste={async (e) => {
                              // 自动处理粘贴的URL
                              const pastedText = e.clipboardData.getData('text')
                              if (pastedText.includes('remoteplay.dl.playstation.net') && pastedText.includes('code=')) {
                                e.preventDefault()
                                const trimmedUrl = pastedText.trim()
                                setCallbackUrl(trimmedUrl)
                                // 自动触发确认
                                setTimeout(() => {
                                  handleCallbackUrl(trimmedUrl)
                                }, 100)
                              }
                            }}
                            className="flex-1 text-xs focus:border-amber-500 focus:ring-amber-500 pr-20"
                            onKeyDown={(e) => {
                              if (e.key === 'Enter' && callbackUrl.trim()) {
                                handleCallbackUrl(callbackUrl)
                              }
                            }}
                          />
                          <Button
                            type="button"
                            variant="ghost"
                            size="sm"
                            onClick={async () => {
                              try {
                                // 尝试从剪贴板读取
                                const text = await navigator.clipboard.readText()
                                if (text.includes('remoteplay.dl.playstation.net') && text.includes('code=')) {
                                  setCallbackUrl(text.trim())
                                  setTimeout(() => {
                                    handleCallbackUrl(text.trim())
                                  }, 100)
                                } else {
                                  toast({
                                    title: t('devices.bindDevice.errors.clipboardInvalid'),
                                    description: t('devices.bindDevice.errors.clipboardInvalidHint'),
                                    variant: 'destructive',
                                  })
                                }
                              } catch (err) {
                                toast({
                                  title: t('devices.bindDevice.errors.clipboardReadFailed'),
                                  description: t('devices.bindDevice.errors.clipboardReadFailedHint'),
                                  variant: 'destructive',
                                })
                              }
                            }}
                            className="absolute right-1 top-1/2 -translate-y-1/2 h-7 px-2 text-xs text-amber-700 hover:text-amber-900 hover:bg-amber-100"
                            title={t('devices.bindDevice.oauth.clipboardRead')}
                          >
                            📋
                          </Button>
                        </div>
                        <Button
                          type="button"
                          size="sm"
                          onClick={() => handleCallbackUrl(callbackUrl)}
                          disabled={!callbackUrl.trim() || isGettingAccountId}
                          className="bg-amber-600 hover:bg-amber-700 text-white whitespace-nowrap"
                        >
                          {isGettingAccountId ? (
                            <Loader2 className="h-3 w-3 animate-spin" />
                          ) : (
                            t('common.confirm')
                          )}
                        </Button>
                      </div>
                      {callbackUrl && !callbackUrl.includes('remoteplay.dl.playstation.net') && (
                        <p className="text-xs text-amber-700 flex items-center gap-1">
                          <span>⚠️</span>
                          <span>{t('devices.bindDevice.oauth.callbackUrlInvalid')}</span>
                        </p>
                      )}
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        onClick={() => {
                          setShowCallbackInput(false)
                          setCallbackUrl('')
                          if (oauthWindow) {
                            oauthWindow.close()
                            setOauthWindow(null)
                          }
                          setIsGettingAccountId(false)
                        }}
                        className="text-xs h-7 px-2 text-amber-700 hover:text-amber-900 hover:bg-amber-100 w-full"
                      >
                        {t('devices.bindDevice.oauth.cancelGet')}
                      </Button>
                    </div>
                  )}
                </div>

                <div className="space-y-2">
                  <Label htmlFor="pin">{t('devices.bindDevice.bind.pin')}</Label>
                  <Input
                    id="pin"
                    type="password"
                    placeholder={t('devices.bindDevice.bind.pinPlaceholder')}
                    value={pin}
                    onChange={(e) => setPin(e.target.value)}
                    maxLength={8}
                  />
                  <p className="text-xs text-muted-foreground">
                    {t('devices.bindDevice.bind.pinHint')}
                  </p>
                </div>
              </>
            )}
          </div>
        )}

        </div>

        <DialogFooter className="px-6 py-4 bg-white dark:bg-gray-950 border-t border-gray-200 dark:border-gray-800 gap-2">
          {step === 'bind' && (
            <Button
              variant="outline"
              onClick={() => setStep('discover')}
              disabled={isBinding}
              className="hover:bg-gray-100 dark:hover:bg-gray-800"
            >
              {t('common.back')}
            </Button>
          )}
          <Button 
            variant="outline" 
            onClick={handleClose} 
            disabled={isBinding || isDiscovering}
            className="hover:bg-gray-100"
          >
            {t('common.cancel')}
          </Button>
          {step === 'bind' && (
            <Button 
              onClick={handleBind} 
              disabled={isBinding}
              className="bg-blue-600 hover:bg-blue-700 text-white shadow-sm hover:shadow-md transition-all"
            >
              {isBinding ? (
                <>
                  <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                  {t('devices.bindDevice.bind.binding')}
                </>
              ) : (
                t('devices.bindDevice.bind.completeBind')
              )}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

