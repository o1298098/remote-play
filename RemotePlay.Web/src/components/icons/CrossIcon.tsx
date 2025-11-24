import React, { useMemo } from 'react'

interface CrossIconProps {
  className?: string
  style?: React.CSSProperties
  size?: number | string
}

export function CrossIcon({ className = '', style = {}, size = 20 }: CrossIconProps) {
  const filter1 = useMemo(() => `f1-${Math.random().toString(36).slice(2)}`, [])

  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 20 20"
      fill="none"
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
      <g filter={`url(#${filter1})`}>
        <circle cx="10" cy="10" r="10" fill="#FBFDFF" fillOpacity="0.01" />
      </g>
      <circle cx="10" cy="10" r="9.5" stroke="#FBFDFF" strokeOpacity="0.15" />
      <path
        d="M5 5L15 15M15 5L5 15"
        stroke="#FBFDFF"
        strokeOpacity="0.5"
        strokeWidth="2"
      />
      <defs>
        <filter
          id={filter1}
          x="0"
          y="0"
          width="20"
          height="20"
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
    </svg>
  )
}
