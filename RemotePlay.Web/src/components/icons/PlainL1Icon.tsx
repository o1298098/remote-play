import React, { useMemo } from 'react'

interface PlainL1IconProps {
  className?: string
  style?: React.CSSProperties
  size?: number | string
}

export function PlainL1Icon({ className = '', style = {}, size = 46 }: PlainL1IconProps) {
  const filter0 = useMemo(() => `f0-${Math.random().toString(36).slice(2)}`, [])
  const filter1 = useMemo(() => `f1-${Math.random().toString(36).slice(2)}`, [])

  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 46 46"
      xmlns="http://www.w3.org/2000/svg"
      className={className}
      style={{
        ...style,
        shapeRendering: 'auto',
        textRendering: 'optimizeLegibility',
        imageRendering: 'auto',
      }}
      preserveAspectRatio="xMidYMid meet"
    >
      <defs>
        {/* drop shadow */}
        <filter
          id={filter0}
          x="1.24265"
          y="1.24264"
          width="37.3848"
          height="37.3848"
          filterUnits="userSpaceOnUse"
          colorInterpolationFilters="sRGB"
          filterRes="500 500"
        >
          <feFlood floodOpacity="0" result="BackgroundImageFix" />
          <feColorMatrix
            in="SourceAlpha"
            type="matrix"
            values="0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 127 0"
            result="hardAlpha"
          />
          <feOffset />
          <feComposite in2="hardAlpha" operator="out" />
          <feColorMatrix type="matrix" values="0 0 0 0 1 0 0 0 0 1 0 0 0 0 1 0 0 0 0.2 0" />
          <feBlend mode="normal" in2="BackgroundImageFix" result="effect1_dropShadow" />
          <feBlend mode="normal" in="SourceGraphic" in2="effect1_dropShadow" result="shape" />
        </filter>

        {/* inner glow */}
        <filter
          id={filter1}
          x="1.24265"
          y="1.24264"
          width="37.3848"
          height="37.3848"
          filterUnits="userSpaceOnUse"
          colorInterpolationFilters="sRGB"
          filterRes="500 500"
        >
          <feFlood floodOpacity="0" result="BackgroundImageFix" />
          <feBlend mode="normal" in="SourceGraphic" in2="BackgroundImageFix" result="shape" />
          <feColorMatrix
            in="SourceAlpha"
            type="matrix"
            values="0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 127 0"
            result="hardAlpha"
          />
          <feOffset />
          <feGaussianBlur stdDeviation="2.5" />
          <feComposite in2="hardAlpha" operator="arithmetic" k2="-1" k3="1" />
          <feColorMatrix
            type="matrix"
            values="0 0 0 0 0.985577 0 0 0 0 0.991048 0 0 0 0 1 0 0 0 1 0"
          />
          <feBlend mode="normal" in2="shape" result="effect1_innerShadow" />
        </filter>
      </defs>

      <g filter={`url(#${filter0})`}>
        <g filter={`url(#${filter1})`}>
          <path
            d="M33.9411 11.3137C40.1895 17.5621 40.1895 27.6927 33.9411 33.9411C27.6927 40.1895 17.5621 40.1895 11.3137 33.9411L2.12132 24.7487C0.949751 23.5772 0.949751 21.6777 2.12132 20.5061L20.5061 2.12132C21.6777 0.949747 23.5772 0.949747 24.7487 2.12132L33.9411 11.3137Z"
            fill="#7F8A9C"
            fillOpacity="0.01"
          />
        </g>

        <path
          d="M16.3215 26.6279V17.9006H18.1666V25.1066H21.9081V26.6279H16.3215ZM26.325 17.9006V26.6279H24.4798V19.6521H24.4287L22.4301 20.9049V19.2685L24.5906 17.9006H26.325Z"
          fill="#FBFDFF"
          fillOpacity="0.46"
        />
      </g>
    </svg>
  )
}
