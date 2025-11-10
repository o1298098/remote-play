import { useEffect, useState } from 'react'

interface Particle {
  id: number
  angle: number // 粒子的角度（从中心向外）
  distance: number // 距离中心的距离（百分比）
  size: number
  speed: number
  opacity: number
  startDelay: number
}

export function StreamingLoading() {
  const [particles, setParticles] = useState<Particle[]>([])

  useEffect(() => {
    // 创建粒子 - 从中心（飞船前方）向外移动，模拟飞船向前飞行
    const createParticles = () => {
      const newParticles: Particle[] = []
      const particleCount = 300

      for (let i = 0; i < particleCount; i++) {
        // 粒子从中心开始，随机角度向外辐射
        const angle = Math.random() * Math.PI * 2
        
        newParticles.push({
          id: i,
          angle,
          distance: 0, // 从中心开始
          size: Math.random() * 3 + 1, // 1-4px
          speed: Math.random() * 0.5 + 0.4, // 0.4-0.9
          opacity: Math.random() * 0.8 + 0.3, // 0.3-1.0
          startDelay: Math.random() * 3, // 0-3秒延迟
        })
      }

      setParticles(newParticles)
    }

    createParticles()
  }, [])

  return (
    <div className="fixed inset-0 bg-black flex items-center justify-center z-50 overflow-hidden">
      {/* 宇宙背景 - 深色渐变 */}
      <div className="absolute inset-0 bg-gradient-to-b from-gray-900 via-black to-gray-900"></div>
      
      {/* 粒子层 - 从中心向外移动，形成速度线效果 */}
      <div className="absolute inset-0" style={{ transform: 'translateZ(0)', zIndex: 1 }}>
        {particles.map((particle) => {
          // 计算粒子移动的方向（从中心向外）
          const moveDistance = 150 // 移动距离（vw/vh单位）
          const moveX = Math.cos(particle.angle) * moveDistance
          const moveY = Math.sin(particle.angle) * moveDistance
          
          return (
            <div
              key={particle.id}
              className="absolute rounded-full bg-white particle-optimized"
              style={{
                left: '50%',
                top: '50%',
                width: `${particle.size}px`,
                height: `${particle.size}px`,
                opacity: particle.opacity,
                transform: 'translate(-50%, -50%) translateZ(0)',
                animation: `particleMoveOut ${5 / particle.speed}s cubic-bezier(0.4, 0, 0.2, 1) infinite`,
                animationDelay: `${particle.startDelay}s`,
                willChange: 'transform, opacity',
                backfaceVisibility: 'hidden',
                transformStyle: 'preserve-3d',
                boxShadow: `0 0 ${particle.size * 2}px rgba(255, 255, 255, ${particle.opacity}), 0 0 ${particle.size * 4}px rgba(100, 150, 255, ${particle.opacity * 0.5})`,
                '--move-x': `${moveX}vw`,
                '--move-y': `${moveY}vh`,
              } as React.CSSProperties & { '--move-x': string; '--move-y': string }}
            />
          )
        })}
      </div>

      {/* 真实光源效果 - 向光飞去 */}
      <div className="relative z-20 flex items-center justify-center">
        {/* 最外层光晕 - 大范围散射 */}
        <div 
          className="absolute"
          style={{
            width: '600px',
            height: '600px',
            borderRadius: '50%',
            background: 'radial-gradient(circle, rgba(255, 255, 255, 0.15) 0%, rgba(255, 255, 255, 0.08) 20%, rgba(255, 255, 255, 0.03) 40%, transparent 70%)',
            filter: 'blur(60px)',
            animation: 'lightFlicker 4s cubic-bezier(0.4, 0, 0.6, 1) infinite',
            transform: 'translateZ(0)',
            willChange: 'opacity, transform',
            zIndex: 0,
          }}
        />
        
        {/* 外层光晕 - 中等散射 */}
        <div 
          className="absolute"
          style={{
            width: '450px',
            height: '450px',
            borderRadius: '50%',
            background: 'radial-gradient(circle, rgba(255, 255, 255, 0.25) 0%, rgba(255, 255, 255, 0.15) 25%, rgba(255, 255, 255, 0.05) 50%, transparent 75%)',
            filter: 'blur(40px)',
            animation: 'lightFlicker 3.5s cubic-bezier(0.4, 0, 0.6, 1) infinite 0.4s',
            transform: 'translateZ(0)',
            willChange: 'opacity, transform',
            zIndex: 1,
          }}
        />
        
        {/* 中层光晕 - 明亮散射 */}
        <div 
          className="absolute"
          style={{
            width: '320px',
            height: '320px',
            borderRadius: '50%',
            background: 'radial-gradient(circle, rgba(255, 255, 255, 0.4) 0%, rgba(255, 255, 255, 0.25) 30%, rgba(255, 255, 255, 0.1) 60%, transparent 85%)',
            filter: 'blur(30px)',
            animation: 'lightFlicker 3s cubic-bezier(0.4, 0, 0.6, 1) infinite 0.8s',
            transform: 'translateZ(0)',
            willChange: 'opacity, transform',
            zIndex: 2,
          }}
        />
        
        {/* 内层光晕 - 强烈散射 */}
        <div 
          className="absolute"
          style={{
            width: '220px',
            height: '220px',
            borderRadius: '50%',
            background: 'radial-gradient(circle, rgba(255, 255, 255, 0.6) 0%, rgba(255, 255, 255, 0.4) 40%, rgba(255, 255, 255, 0.15) 70%, transparent 90%)',
            filter: 'blur(20px)',
            animation: 'lightFlicker 2.5s cubic-bezier(0.4, 0, 0.6, 1) infinite 1.2s',
            transform: 'translateZ(0)',
            willChange: 'opacity, transform',
            zIndex: 3,
          }}
        />
        
        {/* 核心光晕 - 非常明亮 */}
        <div 
          className="absolute"
          style={{
            width: '140px',
            height: '140px',
            borderRadius: '50%',
            background: 'radial-gradient(circle, rgba(255, 255, 255, 0.9) 0%, rgba(255, 255, 255, 0.7) 50%, rgba(255, 255, 255, 0.3) 80%, transparent 100%)',
            filter: 'blur(15px)',
            animation: 'lightFlicker 2s cubic-bezier(0.4, 0, 0.6, 1) infinite 1.6s',
            transform: 'translateZ(0)',
            willChange: 'opacity, transform',
            zIndex: 4,
          }}
        />
        
        {/* 中心强光点 - 最亮核心 */}
        <div 
          className="absolute"
          style={{
            width: '60px',
            height: '60px',
            borderRadius: '50%',
            background: 'radial-gradient(circle, rgba(255, 255, 255, 1) 0%, rgba(255, 255, 255, 0.95) 30%, rgba(255, 255, 255, 0.7) 60%, transparent 100%)',
            boxShadow: '0 0 40px rgba(255, 255, 255, 1), 0 0 80px rgba(255, 255, 255, 0.9), 0 0 120px rgba(255, 255, 255, 0.7), 0 0 160px rgba(255, 255, 255, 0.5)',
            animation: 'lightPulse 1.2s cubic-bezier(0.4, 0, 0.6, 1) infinite',
            transform: 'translateZ(0)',
            willChange: 'opacity, transform',
            zIndex: 5,
          }}
        />
        
        {/* 中心最亮点 - 闪烁核心 */}
        <div 
          className="absolute"
          style={{
            width: '16px',
            height: '16px',
            borderRadius: '50%',
            background: 'rgba(255, 255, 255, 1)',
            boxShadow: '0 0 15px rgba(255, 255, 255, 1), 0 0 30px rgba(255, 255, 255, 0.9), 0 0 45px rgba(255, 255, 255, 0.8)',
            animation: 'lightCore 0.8s cubic-bezier(0.4, 0, 0.6, 1) infinite',
            transform: 'translateZ(0)',
            willChange: 'opacity, transform',
            zIndex: 6,
          }}
        />
        
      </div>

      {/* 连接中提示 - 屏幕下方 */}
      <div 
        className="absolute bottom-16 left-1/2 transform -translate-x-1/2 z-30"
        style={{
          transform: 'translateX(-50%) translateZ(0)',
        }}
      >
        <p 
          className="text-white text-lg font-medium tracking-wide"
          style={{
            textShadow: '0 0 10px rgba(255, 255, 255, 0.5), 0 0 20px rgba(255, 255, 255, 0.3)',
            animation: 'textFade 2s ease-in-out infinite',
          }}
        >
          正在连接设备
        </p>
        <div className="flex items-center justify-center gap-2 mt-3">
          <div 
            className="w-2 h-2 bg-white rounded-full"
            style={{ 
              animation: 'dotBounce 1.4s cubic-bezier(0.4, 0, 0.6, 1) infinite',
              animationDelay: '0s',
              boxShadow: '0 0 8px rgba(255, 255, 255, 0.8)',
              transform: 'translateZ(0)',
              willChange: 'transform, opacity',
            }}
          />
          <div 
            className="w-2 h-2 bg-white rounded-full"
            style={{ 
              animation: 'dotBounce 1.4s cubic-bezier(0.4, 0, 0.6, 1) infinite',
              animationDelay: '0.2s',
              boxShadow: '0 0 8px rgba(255, 255, 255, 0.8)',
              transform: 'translateZ(0)',
              willChange: 'transform, opacity',
            }}
          />
          <div 
            className="w-2 h-2 bg-white rounded-full"
            style={{ 
              animation: 'dotBounce 1.4s cubic-bezier(0.4, 0, 0.6, 1) infinite',
              animationDelay: '0.4s',
              boxShadow: '0 0 8px rgba(255, 255, 255, 0.8)',
              transform: 'translateZ(0)',
              willChange: 'transform, opacity',
            }}
          />
        </div>
      </div>

      {/* CSS 动画定义 - 流畅动画优化 */}
      <style>{`
        @keyframes particleMoveOut {
          0% {
            transform: translate(-50%, -50%) translateZ(0) scale(1);
            opacity: 1;
          }
          50% {
            opacity: 0.6;
          }
          100% {
            transform: translate(calc(-50% + var(--move-x, 0)), calc(-50% + var(--move-y, 0))) translateZ(0) scale(0.2);
            opacity: 0;
          }
        }
        
        @keyframes lightFlicker {
          0% {
            opacity: 0.65;
            transform: scale(1) translateZ(0);
          }
          20% {
            opacity: 0.75;
            transform: scale(1.02) translateZ(0);
          }
          40% {
            opacity: 0.9;
            transform: scale(1.05) translateZ(0);
          }
          60% {
            opacity: 1;
            transform: scale(1.08) translateZ(0);
          }
          80% {
            opacity: 0.85;
            transform: scale(1.03) translateZ(0);
          }
          100% {
            opacity: 0.65;
            transform: scale(1) translateZ(0);
          }
        }
        
        @keyframes lightPulse {
          0% {
            opacity: 0.92;
            transform: scale(1) translateZ(0);
            box-shadow: 0 0 40px rgba(255, 255, 255, 1), 0 0 80px rgba(255, 255, 255, 0.9), 0 0 120px rgba(255, 255, 255, 0.7), 0 0 160px rgba(255, 255, 255, 0.5);
          }
          30% {
            opacity: 0.96;
            transform: scale(1.05) translateZ(0);
          }
          50% {
            opacity: 1;
            transform: scale(1.12) translateZ(0);
            box-shadow: 0 0 60px rgba(255, 255, 255, 1), 0 0 100px rgba(255, 255, 255, 1), 0 0 150px rgba(255, 255, 255, 0.9), 0 0 220px rgba(255, 255, 255, 0.7);
          }
          70% {
            opacity: 0.96;
            transform: scale(1.05) translateZ(0);
          }
          100% {
            opacity: 0.92;
            transform: scale(1) translateZ(0);
            box-shadow: 0 0 40px rgba(255, 255, 255, 1), 0 0 80px rgba(255, 255, 255, 0.9), 0 0 120px rgba(255, 255, 255, 0.7), 0 0 160px rgba(255, 255, 255, 0.5);
          }
        }
        
        @keyframes lightCore {
          0% {
            opacity: 0.85;
            transform: scale(1) translateZ(0);
            box-shadow: 0 0 15px rgba(255, 255, 255, 1), 0 0 30px rgba(255, 255, 255, 0.9), 0 0 45px rgba(255, 255, 255, 0.8);
          }
          40% {
            opacity: 0.95;
            transform: scale(1.15) translateZ(0);
          }
          50% {
            opacity: 1;
            transform: scale(1.25) translateZ(0);
            box-shadow: 0 0 25px rgba(255, 255, 255, 1), 0 0 50px rgba(255, 255, 255, 1), 0 0 75px rgba(255, 255, 255, 0.9);
          }
          60% {
            opacity: 0.95;
            transform: scale(1.15) translateZ(0);
          }
          100% {
            opacity: 0.85;
            transform: scale(1) translateZ(0);
            box-shadow: 0 0 15px rgba(255, 255, 255, 1), 0 0 30px rgba(255, 255, 255, 0.9), 0 0 45px rgba(255, 255, 255, 0.8);
          }
        }
        
        @keyframes rayFlicker {
          0% {
            opacity: 0.35;
          }
          30% {
            opacity: 0.5;
          }
          50% {
            opacity: 0.65;
          }
          70% {
            opacity: 0.5;
          }
          100% {
            opacity: 0.35;
          }
        }
        
        @keyframes textFade {
          0%, 100% {
            opacity: 0.8;
          }
          50% {
            opacity: 1;
          }
        }
        
        @keyframes dotBounce {
          0%, 80%, 100% {
            transform: translateY(0) scale(1) translateZ(0);
            opacity: 0.7;
          }
          40% {
            transform: translateY(-8px) scale(1.1) translateZ(0);
            opacity: 1;
          }
        }
        
        /* 优化渲染性能 - GPU加速 */
        @supports (will-change: transform) {
          .particle-optimized {
            will-change: transform, opacity;
            transform: translateZ(0);
            backface-visibility: hidden;
            transform-style: preserve-3d;
          }
        }
        
        /* 全局性能优化 */
        * {
          -webkit-font-smoothing: antialiased;
          -moz-osx-font-smoothing: grayscale;
        }
      `}</style>
    </div>
  )
}
