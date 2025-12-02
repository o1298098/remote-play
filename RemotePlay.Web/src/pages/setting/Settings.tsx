import { useState, useEffect } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { useAuth } from '@/hooks/use-auth'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { ArrowLeft, Gamepad2, Server, Settings as SettingsIcon } from 'lucide-react'
import { cn } from '@/lib/utils'
import TurnServerSettingsTab from './components/TurnServerSettingsTab'
import WebRTCSettingsTab from './components/WebRTCSettingsTab'
import ControllerMapping from './ControllerMapping'

type SettingsTab = 'controller' | 'turn-server' | 'webrtc'

export default function Settings() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const { isAuthenticated } = useAuth()
  const [searchParams, setSearchParams] = useSearchParams()
  const [activeTab, setActiveTab] = useState<SettingsTab>(() => {
    const tab = searchParams.get('tab') as SettingsTab
    return tab && ['controller', 'turn-server', 'webrtc'].includes(tab) ? tab : 'controller'
  })

  useEffect(() => {
    if (!isAuthenticated) {
      navigate('/login')
    }
  }, [isAuthenticated, navigate])

  useEffect(() => {
    setSearchParams({ tab: activeTab }, { replace: true })
  }, [activeTab, setSearchParams])

  const tabs: Array<{ id: SettingsTab; label: string; icon: typeof Gamepad2 }> = [
    {
      id: 'controller',
      label: t('devices.settings.tabs.controller.label'),
      icon: Gamepad2,
    },
    {
      id: 'turn-server',
      label: t('devices.settings.tabs.turnServer.label'),
      icon: Server,
    },
    {
      id: 'webrtc',
      label: t('devices.settings.tabs.webrtc.label'),
      icon: SettingsIcon,
    },
  ]

  return (
    <div className="container mx-auto p-4 max-w-6xl">
      <div className="mb-6">
        <Button
          variant="ghost"
          size="sm"
          onClick={() => navigate(-1)}
          className="mb-4"
        >
          <ArrowLeft className="mr-2 h-4 w-4" />
          {t('common.back')}
        </Button>
        <h1 className="text-2xl font-bold mb-2 text-gray-900 dark:text-white">{t('devices.settings.title')}</h1>
        <p className="text-gray-600 dark:text-gray-400">{t('devices.settings.subtitle')}</p>
      </div>

      <div className="flex flex-col md:flex-row gap-6">
        {/* 侧边栏菜单 */}
        <div className="w-full md:w-64 flex-shrink-0">
          <Card className="bg-white dark:bg-gray-900 border-gray-200 dark:border-gray-800">
            <CardContent className="p-2">
              <nav className="space-y-1">
                {tabs.map((tab) => {
                  const Icon = tab.icon
                  return (
                    <button
                      key={tab.id}
                      onClick={() => setActiveTab(tab.id)}
                      className={cn(
                        'w-full flex items-center space-x-3 px-3 py-2.5 rounded-lg text-left transition-all',
                        activeTab === tab.id
                          ? 'bg-blue-50 dark:bg-blue-900/30 text-blue-600 dark:text-blue-400 border border-blue-200 dark:border-blue-800'
                          : 'text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800/50'
                      )}
                    >
                      <Icon className={cn(
                        'h-5 w-5 flex-shrink-0',
                        activeTab === tab.id
                          ? 'text-blue-600 dark:text-blue-400'
                          : 'text-gray-500 dark:text-gray-400'
                      )} />
                      <span className={cn(
                        'text-sm font-medium',
                        activeTab === tab.id
                          ? 'text-blue-600 dark:text-blue-400'
                          : 'text-gray-900 dark:text-gray-100'
                      )}>{tab.label}</span>
                    </button>
                  )
                })}
              </nav>
            </CardContent>
          </Card>
        </div>

        {/* 主内容区域 */}
        <div className="flex-1">
          {activeTab === 'controller' && (
            <div className="space-y-4">
              <ControllerMapping />
            </div>
          )}
          {activeTab === 'turn-server' && <TurnServerSettingsTab />}
          {activeTab === 'webrtc' && <WebRTCSettingsTab />}
        </div>
      </div>
    </div>
  )
}

