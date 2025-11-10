import { forwardRef } from 'react'
import { Button } from '@/components/ui/button'

interface StreamingVideoPlayerProps {
  isConnected: boolean
  isConnecting: boolean
  onConnect: () => void
}

export const StreamingVideoPlayer = forwardRef<HTMLVideoElement, StreamingVideoPlayerProps>(
  ({ isConnected, isConnecting, onConnect }, ref) => {
    return (
      <div className="w-full h-full bg-black flex items-center justify-center">
        <video
          ref={ref}
          autoPlay
          playsInline
          muted={false}
          preload="auto"
          className="w-full h-full object-contain bg-black"
          style={{
            width: '100%',
            height: '100%',
            objectFit: 'contain',
            backgroundColor: '#000000',
            background: '#000000',
          }}
          tabIndex={-1}
        />
        {!isConnected && (
          <div className="absolute inset-0 flex items-center justify-center bg-black z-10 pointer-events-none">
            <div className="text-center">
              <p className="text-gray-400 mb-4">
                {isConnecting ? '正在连接...' : '等待连接'}
              </p>
              {!isConnecting && (
                <div className="pointer-events-auto">
                  <Button onClick={onConnect} className="bg-blue-600 hover:bg-blue-700">
                    开始连接
                  </Button>
                </div>
              )}
            </div>
          </div>
        )}
      </div>
    )
  }
)

StreamingVideoPlayer.displayName = 'StreamingVideoPlayer'

