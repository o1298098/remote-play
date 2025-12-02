import { useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { useAuth } from '@/hooks/use-auth'
import { useGamepad } from '@/hooks/use-gamepad'
import { useToast } from '@/hooks/use-toast'
import { useDevice } from '@/hooks/use-device'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Gamepad2, ChevronDown, User, Settings as SettingsIcon, LogOut, Settings, Power, X } from 'lucide-react'
import { LanguageSwitcher } from '@/components/LanguageSwitcher'
import { ThemeSwitcher } from '@/components/ThemeSwitcher'
import { cn } from '@/lib/utils'

export function DevicesHeader() {
  const { t } = useTranslation()
  const { logout, user } = useAuth()
  const { toast } = useToast()
  const { isConnected: isGamepadConnected, connectedGamepads, isEnabled, setEnabled, disconnectGamepad } = useGamepad()
  const { isMobile } = useDevice()
  const navigate = useNavigate()

  // 简化手柄名称显示
  const getGamepadDisplayName = (gamepadId: string) => {
    return gamepadId
      .replace(/\(.*?\)/g, '') // 移除括号内容
      .replace(/\s+/g, ' ') // 合并多个空格
      .trim() || '游戏手柄'
  }

  return (
    <header className="relative z-10 bg-white/95 dark:bg-gray-900/95 backdrop-blur-sm border-b border-blue-100 dark:border-gray-800">
      <div className={cn('container mx-auto flex items-center justify-between', isMobile ? 'px-3 py-3' : 'px-6 py-4')}>
        <div className="flex items-center space-x-2 sm:space-x-3">
          <Gamepad2 className={cn('text-blue-600 dark:text-blue-400', isMobile ? 'h-5 w-5' : 'h-6 w-6')} />
          <div className="flex items-center space-x-1">
            <h1 className={cn('font-bold text-gray-900 dark:text-white', isMobile ? 'text-base' : 'text-xl')}>{t('devices.header.title')}</h1>
            {!isMobile && <ChevronDown className="h-4 w-4 text-gray-600 dark:text-gray-400" />}
          </div>
        </div>
        <div className={cn('flex items-center', isMobile ? 'space-x-1' : 'space-x-2')}>
          <ThemeSwitcher />
          <LanguageSwitcher />
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button 
                variant="ghost" 
                size="icon" 
                className={cn(
                  'text-gray-700 hover:text-blue-600 dark:text-gray-300 dark:hover:text-blue-400',
                  isMobile && 'min-h-[44px] min-w-[44px]'
                )}
              >
                <Gamepad2 className={cn(isMobile ? 'h-5 w-5' : 'h-5 w-5', isGamepadConnected ? 'animate-gamepad-blink' : '')} />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" className={cn(isMobile ? 'w-56' : 'w-64')}>
              <DropdownMenuLabel>
                <div className="flex items-center space-x-2">
                  <Gamepad2 className="h-4 w-4" />
                  <span>{t('devices.header.gamepad.menuTitle')}</span>
                </div>
              </DropdownMenuLabel>
              <DropdownMenuSeparator />
              {isGamepadConnected && connectedGamepads.length > 0 ? (
                <>
                  {connectedGamepads.map((gamepad) => (
                    <DropdownMenuItem 
                      key={gamepad.index} 
                      className="flex items-center space-x-2 cursor-default group"
                      onSelect={(e) => e.preventDefault()}
                    >
                      <div className={`h-2 w-2 rounded-full ${isEnabled ? 'bg-green-500' : 'bg-gray-400'}`}></div>
                      <div className="flex-1 min-w-0">
                        <p className="text-sm font-medium truncate">{getGamepadDisplayName(gamepad.id)}</p>
                        <p className="text-xs text-muted-foreground">
                          {t('devices.header.gamepad.stats', {
                            buttons: gamepad.buttons,
                            axes: gamepad.axes,
                          })}
                        </p>
                      </div>
                      <button
                        onClick={(e) => {
                          e.stopPropagation()
                          const displayName = getGamepadDisplayName(gamepad.id)
                          disconnectGamepad(gamepad.index)
                          toast({
                            title: t('devices.header.gamepad.disconnectedToastTitle'),
                            description: t('devices.header.gamepad.disconnectedToastDescription', { name: displayName }),
                            duration: 3000,
                          })
                        }}
                        className="opacity-0 group-hover:opacity-100 transition-opacity p-1 hover:bg-destructive/10 rounded"
                        title={t('devices.header.gamepad.disconnectTooltip')}
                      >
                        <X className="h-3 w-3 text-destructive" />
                      </button>
                    </DropdownMenuItem>
                  ))}
                  <DropdownMenuSeparator />
                  <DropdownMenuItem onClick={() => setEnabled(!isEnabled)}>
                    <Power className={`mr-2 h-4 w-4 ${isEnabled ? 'text-green-600' : 'text-gray-400'}`} />
                    <span>
                      {isEnabled
                        ? t('devices.header.gamepad.disable')
                        : t('devices.header.gamepad.enable')}
                    </span>
                  </DropdownMenuItem>
                  <DropdownMenuSeparator />
                </>
              ) : (
                <>
                  <DropdownMenuItem disabled className="text-muted-foreground">
                    {t('devices.header.gamepad.notDetected')}
                  </DropdownMenuItem>
                  <DropdownMenuSeparator />
                </>
              )}
              <DropdownMenuItem onClick={() => navigate('/settings?tab=controller')}>
                <Settings className="mr-2 h-4 w-4" />
                <span>{t('devices.controllerMapping.title')}</span>
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button 
                variant="ghost" 
                size="icon" 
                className={cn(
                  'text-gray-700 dark:text-gray-300 hover:text-blue-600 dark:hover:text-blue-400',
                  isMobile && 'min-h-[44px] min-w-[44px]'
                )}
              >
                <User className={cn(isMobile ? 'h-5 w-5' : 'h-5 w-5')} />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" className={cn(isMobile ? 'w-52' : 'w-56')}>
              <DropdownMenuLabel>
                <div className="flex flex-col space-y-1">
                  <p className="text-sm font-medium leading-none">{user?.name || 'User'}</p>
                  <p className="text-xs leading-none text-muted-foreground">{user?.email}</p>
                </div>
              </DropdownMenuLabel>
              <DropdownMenuSeparator />
              <DropdownMenuItem onClick={() => navigate('/settings')}>
                <SettingsIcon className="mr-2 h-4 w-4" />
                <span>{t('devices.header.settings')}</span>
              </DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem onClick={logout}>
                <LogOut className="mr-2 h-4 w-4" />
                <span>{t('devices.header.logout')}</span>
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
      </div>
    </header>
  )
}

