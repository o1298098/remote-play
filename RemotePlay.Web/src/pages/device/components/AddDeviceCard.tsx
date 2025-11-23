import { useTranslation } from 'react-i18next'
import { Card, CardContent, CardHeader } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Plus, ChevronDown } from 'lucide-react'
import { useDevice } from '@/hooks/use-device'
import { cn } from '@/lib/utils'

interface AddDeviceCardProps {
  onClick: () => void
}

export function AddDeviceCard({ onClick }: AddDeviceCardProps) {
  const { t } = useTranslation()
  const { isMobile, isTablet } = useDevice()

  return (
    <Card
      className={cn(
        'group h-full bg-white dark:bg-gray-800 hover:bg-gradient-to-br hover:from-gray-50 dark:hover:from-gray-700/50 hover:to-blue-50 dark:hover:to-blue-900/20 rounded-2xl border-2 border-dashed border-gray-300 dark:border-gray-700 hover:border-blue-400 dark:hover:border-blue-600 shadow-lg hover:shadow-2xl cursor-pointer transition-all duration-300 overflow-hidden relative flex flex-col',
        isMobile ? 'w-full min-h-[320px]' : isTablet ? 'w-[calc(50%-0.75rem)] min-h-[360px]' : 'w-[280px] min-h-[360px]'
      )}
      onClick={onClick}
    >
      {/* 装饰性渐变背景 */}
      <div className="absolute inset-0 bg-gradient-to-br from-gray-100/50 dark:from-gray-700/30 via-transparent to-blue-100/50 dark:to-blue-900/20 opacity-0 group-hover:opacity-100 transition-opacity duration-300"></div>
      
      <CardHeader className={cn('relative z-10', isMobile ? 'pt-6 pb-4' : 'pt-8 pb-6')}>
        <div className="flex justify-center relative">
          {/* 背景装饰圆环 */}
          <div className="absolute inset-0 flex items-center justify-center">
            <div className={cn(
              'rounded-full bg-gradient-to-br from-gray-200 dark:from-gray-700 via-blue-100 dark:via-blue-900/40 to-indigo-100 dark:to-indigo-900/40 opacity-40 group-hover:opacity-70 group-hover:scale-110 transition-all duration-300 blur-xl',
              isMobile ? 'w-20 h-20' : 'w-28 h-28'
            )}></div>
          </div>
          {/* 加号图标 */}
          <div className={cn('relative flex items-center justify-center', isMobile ? 'h-20 w-20' : 'h-28 w-28')}>
            <div className={cn(
              'rounded-full bg-gradient-to-br from-gray-100 dark:from-gray-700 to-blue-100 dark:to-blue-900/40 group-hover:from-blue-100 dark:group-hover:from-blue-900/40 group-hover:to-indigo-100 dark:group-hover:to-indigo-900/40 flex items-center justify-center transition-all duration-300 group-hover:scale-110 border-2 border-dashed border-gray-300 dark:border-gray-600 group-hover:border-blue-400 dark:group-hover:border-blue-500',
              isMobile ? 'w-16 h-16' : 'w-20 h-20'
            )}>
              <Plus className={cn(
                'text-gray-400 dark:text-gray-500 group-hover:text-blue-600 dark:group-hover:text-blue-400 transition-all duration-300',
                isMobile ? 'h-10 w-10' : 'h-12 w-12'
              )} />
            </div>
          </div>
        </div>
        {/* 占位空间，与已有主机卡片的状态指示器高度对齐 */}
        <div className="flex justify-center">
          <div className="invisible flex items-center gap-1.5 px-3 py-1 bg-gray-50 dark:bg-gray-800 rounded-full border border-gray-200 dark:border-gray-700">
            <span className="w-2 h-2 rounded-full"></span>
            <span className="text-xs font-medium">{t('devices.console.status.offline')}</span>
          </div>
        </div>
      </CardHeader>
      
      <CardContent className={cn('relative z-10 flex flex-col flex-1', isMobile ? 'px-4 pb-4 space-y-3' : 'px-6 pb-6 space-y-4')}>
        <div className="space-y-2">
          <h3 className={cn(
            'text-gray-900 dark:text-white font-bold group-hover:text-blue-900 dark:group-hover:text-blue-400 transition-colors',
            isMobile ? 'text-base' : 'text-lg'
          )}>
            {t('devices.console.addConsole.title')}
          </h3>
          <p className={cn(
            'text-gray-500 dark:text-gray-400 leading-relaxed flex items-center',
            isMobile ? 'text-xs min-h-[36px]' : 'text-xs min-h-[42px]'
          )}>
            {t('devices.console.addConsole.description')}
          </p>
        </div>
        <div className={cn('border-t border-gray-200 dark:border-gray-700 group-hover:border-blue-200 dark:group-hover:border-blue-700 transition-colors mt-auto', isMobile ? 'pt-3' : 'pt-4')}>
          <Button
            className={cn(
              'w-full bg-white dark:bg-gray-800 hover:bg-gradient-to-r hover:from-blue-600 hover:to-indigo-600 text-gray-700 dark:text-gray-300 hover:text-white border-2 border-gray-300 dark:border-gray-600 hover:border-transparent shadow-sm hover:shadow-lg hover:scale-[1.02] transition-all duration-200 flex items-center justify-center gap-2 font-semibold',
              isMobile ? 'min-h-[44px] text-base' : 'text-sm'
            )}
            variant="outline"
          >
            <span>{t('devices.console.addConsole.button')}</span>
            <ChevronDown className={cn('rotate-[-90deg] group-hover:translate-x-1 transition-transform', isMobile ? 'h-4 w-4' : 'h-3.5 w-3.5')} />
          </Button>
        </div>
      </CardContent>
    </Card>
  )
}

