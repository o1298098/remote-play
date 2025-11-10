import { Card, CardContent, CardHeader } from '@/components/ui/card'

export function DeviceCardSkeleton() {
  return (
    <Card className="w-[280px] animate-pulse bg-white dark:bg-gray-800 rounded-2xl border-2 border-gray-200 dark:border-gray-700 shadow-lg overflow-hidden flex flex-col">
      <CardHeader className="pt-8 pb-6">
        <div className="relative mb-4">
          <div className="absolute top-0 right-0 h-7 w-7 rounded-full bg-gray-200 dark:bg-gray-700"></div>
          <div className="absolute inset-0 flex items-center justify-center">
            <div className="w-32 h-32 rounded-full bg-gradient-to-br from-gray-200 dark:from-gray-700 via-gray-300 dark:via-gray-600 to-gray-200 dark:to-gray-700 opacity-70 blur-xl"></div>
          </div>
          <div className="relative mx-auto h-24 w-24 rounded-full bg-gradient-to-br from-gray-200 dark:from-gray-700 to-gray-300 dark:to-gray-600"></div>
        </div>
        <div className="flex items-center justify-center gap-3 mt-2">
          <div className="h-6 w-20 bg-gray-200 dark:bg-gray-700 rounded-full"></div>
          <div className="h-6 w-16 bg-gray-200 dark:bg-gray-700 rounded-full"></div>
        </div>
      </CardHeader>
      <CardContent className="px-6 pb-6 space-y-4 flex flex-col flex-grow">
        <div className="space-y-2 flex-grow">
          <div className="h-5 bg-gray-200 dark:bg-gray-700 rounded w-3/4"></div>
          <div className="space-y-2">
            <div className="h-3 bg-gray-200 dark:bg-gray-700 rounded w-full"></div>
            <div className="h-3 bg-gray-200 dark:bg-gray-700 rounded w-2/3"></div>
            <div className="h-3 bg-gray-200 dark:bg-gray-700 rounded w-1/2"></div>
          </div>
        </div>
        <div className="pt-4 border-t border-gray-200 dark:border-gray-700">
          <div className="h-11 bg-gray-200 dark:bg-gray-700 rounded-lg"></div>
        </div>
      </CardContent>
    </Card>
  )
}

